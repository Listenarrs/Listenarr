using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

[Trait("Name", "EfLibraryDirectoryOwnershipStoreTests")]
[Trait("Category", "Infrastructure")]
public sealed class EfLibraryDirectoryOwnershipStoreTests : BaseTests
{
    private readonly string _databasePath = Path.Join(
        Path.GetTempPath(),
        "listenarr-tests",
        $"directory-ownership-{Guid.NewGuid():N}.db");
    private string _root = string.Empty;
    private IDbContextFactory<ListenArrDbContext> _factory = null!;
    private EfLibraryDirectoryOwnershipStore _store = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        _root = OperatingSystem.IsWindows()
            ? WindowsPathTestFixture.CreateRootRelativeAliasCompatibleDirectory(
                "directory-ownership-root")
            : Path.Join(
                Path.GetTempPath(),
                "listenarr-tests",
                $"directory-ownership-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={_databasePath};Pooling=False")
            .Options;
        _factory = new TestDbContextFactory(options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        var rootIdentity = await new DirectoryObjectIdentityResolver()
            .ResolveAsync(_root);
        Assert.True(rootIdentity.IsAvailable, rootIdentity.UnavailableReason);
        db.RootFolders.Add(new RootFolder
        {
            Name = "Test library",
            Path = _root,
            ResolvedCaseSensitivity =
                FileSystemPathSemantics.CurrentHostDefault.CaseSensitivity,
            PathIdentityState = PathIdentityState.Valid,
            DirectoryObjectIdentityVersion = rootIdentity.Version,
            DirectoryObjectIdentity = rootIdentity.Value,
            DirectoryObjectIdentityUnavailableReason =
                rootIdentity.UnavailableReason
        });
        await db.SaveChangesAsync();
        _store = new EfLibraryDirectoryOwnershipStore(_factory, TimeProvider.System);
    }

    public override async Task DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
        await base.DisposeAsync();
    }

    [WindowsFact]
    public async Task BoundaryAuthorizer_ForeignPersistedUnixRoot_CannotAuthorizeWindowsAlias()
    {
        var foreignRoot = "/" + Path.GetRelativePath(
                Path.GetPathRoot(_root)!,
                _root)
            .Replace('\\', '/');
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = await db.RootFolders.SingleAsync();
            root.Path = foreignRoot;
            await db.SaveChangesAsync();
        }

        var authorizer = new LibraryDirectoryOwnershipBoundaryAuthorizer(_factory);
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var authorization = await authorizer.AuthorizeAsync(
                foreignRoot,
                semantics,
                CancellationToken.None);
        });

        Assert.Contains(
            "this host uses Windows syntax",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordCreatedAsync_PersistsIdentityWithoutPermanentFilesystemMarkers()
    {
        var directory = Path.Join(_root, "Author");
        Directory.CreateDirectory(directory);

        var ownership = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test",
                Guid.NewGuid(),
                AudiobookId: 7));

        Assert.NotEqual(0, ownership.Id);
        Assert.False(string.IsNullOrWhiteSpace(ownership.PathOwnershipKey));
        Assert.False(string.IsNullOrWhiteSpace(ownership.OwnershipToken));
        AssertNoPersistentOwnershipArtifacts(ownership);

        var resolution = await _store.ResolveOwnedAsync(
            directory,
            FileSystemPathSemantics.CurrentHostDefault);
        Assert.Equal(LibraryDirectoryOwnershipResolutionState.Owned, resolution.State);
        Assert.Equal(ownership.Id, resolution.Ownership?.Id);
    }

    [Fact]
    public async Task RecordCreatedAsync_RequestCancelledBeforeCommit_LeavesNoOwnershipOrArtifacts()
    {
        var directory = Path.Join(_root, "CancelledBeforeCommit");
        Directory.CreateDirectory(directory);
        using var cancellation = new CancellationTokenSource();
        _store.BeforeNewOwnershipCommitForTest = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _store.RecordCreatedAsync(
                new LibraryDirectoryOwnershipClaim(
                    directory,
                    FileSystemPathSemantics.CurrentHostDefault,
                    "test",
                    Guid.NewGuid(),
                    AudiobookId: 11),
                cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.LibraryDirectoryOwnerships.ToListAsync());
    }

    [Fact]
    public async Task RecordCreatedAsync_InterruptedBeforeCommit_LeavesNoClaimOrArtifacts()
    {
        var directory = Path.Join(_root, "InterruptedBeforeCommit");
        Directory.CreateDirectory(directory);
        _store.BeforeNewOwnershipCommitForTest = () =>
            throw new IOException("Injected interruption before ownership commit.");

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            _store.RecordCreatedAsync(
                new LibraryDirectoryOwnershipClaim(
                    directory,
                    FileSystemPathSemantics.CurrentHostDefault,
                    "test",
                    Guid.NewGuid(),
                    AudiobookId: 12)));

        Assert.Contains("before ownership commit", exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.LibraryDirectoryOwnerships.ToListAsync());
    }

    [Fact]
    public async Task RecordCreatedAsync_RetryAfterPreCommitInterruption_CreatesSingleClaim()
    {
        var directory = Path.Join(_root, "RetryInterruptedCommit");
        Directory.CreateDirectory(directory);
        var claim = new LibraryDirectoryOwnershipClaim(
            directory,
            FileSystemPathSemantics.CurrentHostDefault,
            "test",
            Guid.NewGuid(),
            AudiobookId: 13);
        _store.BeforeNewOwnershipCommitForTest = () =>
            throw new IOException("Injected ownership persistence interruption.");

        await Assert.ThrowsAsync<IOException>(() =>
            _store.RecordCreatedAsync(claim));
        _store.BeforeNewOwnershipCommitForTest = null;

        var repaired = await _store.RecordCreatedAsync(claim);

        Assert.Equal(LibraryDirectoryOwnershipState.Owned, repaired.State);
        Assert.Null(repaired.DirectoryObjectIdentityUnavailableReason);
        AssertNoPersistentOwnershipArtifacts(repaired);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Single(await verification.LibraryDirectoryOwnerships.ToListAsync());
    }

    [Fact]
    public async Task RecordCreatedAsync_IsIdempotentForTheSameIdentity()
    {
        var directory = Path.Join(_root, "Author");
        Directory.CreateDirectory(directory);
        var claim = new LibraryDirectoryOwnershipClaim(
            directory,
            FileSystemPathSemantics.CurrentHostDefault,
            "test");

        var first = await _store.RecordCreatedAsync(claim);
        var second = await _store.RecordCreatedAsync(claim);

        Assert.Equal(first.Id, second.Id);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.LibraryDirectoryOwnerships.ToListAsync());
    }

    [Fact]
    public async Task RecordCreatedAsync_CrossSensitivityAliasBecomesConflict()
    {
        var directory = Path.Join(_root, "Library");
        Directory.CreateDirectory(directory);
        var syntax = OperatingSystem.IsWindows()
            ? FileSystemPathSyntax.Windows
            : FileSystemPathSyntax.Unix;
        await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                new FileSystemPathSemantics(
                    syntax,
                    FileSystemCaseSensitivity.Sensitive),
                "test"));
        var alias = Path.Join(_root, "library");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.RecordCreatedAsync(
                new LibraryDirectoryOwnershipClaim(
                    alias,
                    new FileSystemPathSemantics(
                        syntax,
                        FileSystemCaseSensitivity.Insensitive),
                    "test")));

        Assert.Contains("conflicts", exception.Message, StringComparison.OrdinalIgnoreCase);
        var resolution = await _store.ResolveOwnedAsync(
            directory,
            new FileSystemPathSemantics(
                syntax,
                FileSystemCaseSensitivity.Sensitive));
        Assert.Equal(LibraryDirectoryOwnershipResolutionState.Conflict, resolution.State);
    }

    [Fact]
    public async Task PhysicalPathReplacementWithoutMarkerFailsNativeGenerationValidation()
    {
        var directory = Path.Join(_root, "Author");
        Directory.CreateDirectory(directory);
        await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test"));
        Assert.False(File.Exists(Path.Join(
            directory,
            LibraryDirectoryOwnershipMarker.FileName)));

        Directory.Delete(directory, recursive: false);
        Directory.CreateDirectory(directory);

        var resolution = await _store.ResolveOwnedAsync(
            directory,
            FileSystemPathSemantics.CurrentHostDefault);
        Assert.Equal(
            LibraryDirectoryOwnershipResolutionState.Unavailable,
            resolution.State);
        Assert.Contains(
            "physical",
            resolution.Reason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PhysicalPathReplacementWithCopiedMarkersFailsClosed()
    {
        var directory = Path.Join(_root, "ReplacedAuthor");
        Directory.CreateDirectory(directory);
        var ownership = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test"));
        await PublishLegacyOwnershipMarkersAsync(ownership);
        var insideMarker = Path.Join(
            directory,
            LibraryDirectoryOwnershipMarker.FileName);
        var insidePayload = await File.ReadAllTextAsync(insideMarker);

        File.Delete(insideMarker);
        Directory.Delete(directory);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(insideMarker, insidePayload);

        var resolution = await _store.ResolveOwnedAsync(
            directory,
            FileSystemPathSemantics.CurrentHostDefault);

        Assert.Equal(
            LibraryDirectoryOwnershipResolutionState.Unavailable,
            resolution.State);
        Assert.Contains(
            "physical identity",
            resolution.Reason,
            StringComparison.OrdinalIgnoreCase);
    }

    [LinuxFact]
    public async Task ResolveOwnedAsync_DirectoryReplacedAfterPhysicalIdentityPin_DoesNotMixMarkerGeneration()
    {
        var directory = Path.Join(_root, "PinnedGenerationReplacement");
        var displacedDirectory = directory + ".original";
        Directory.CreateDirectory(directory);
        var ownership = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test"));
        using (var parent = PinnedDirectoryCreation.OpenPinnedBoundary(_root))
        using (var publication = parent.OpenExistingChildForPublication(
            Path.GetFileName(directory)))
        {
            await PinnedLibraryDirectoryOwnershipMarker.EnsureAsync(
                ownership,
                publication,
                CancellationToken.None);
        }
        var insideMarker = Path.Join(
            directory,
            LibraryDirectoryOwnershipMarker.FileName);
        var insidePayload = await File.ReadAllTextAsync(insideMarker);
        var replaced = false;
        _store.AfterOwnedDirectoryPhysicalIdentityPinnedForTest = () =>
        {
            if (replaced)
            {
                return;
            }

            replaced = true;
            Directory.Move(directory, displacedDirectory);
            Directory.CreateDirectory(directory);
            File.WriteAllText(insideMarker, insidePayload);
        };

        var resolution = await _store.ResolveOwnedAsync(
            directory,
            FileSystemPathSemantics.CurrentHostDefault);

        Assert.True(replaced);
        Assert.Equal(
            LibraryDirectoryOwnershipResolutionState.Unavailable,
            resolution.State);
        Assert.Contains(
            "proof",
            resolution.Reason,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(displacedDirectory));
        Assert.Equal(insidePayload, await File.ReadAllTextAsync(insideMarker));
        Assert.Null(resolution.Ownership);
        await using var verification = await _factory.CreateDbContextAsync();
        var persisted = await verification.LibraryDirectoryOwnerships
            .SingleAsync(candidate => candidate.Id == ownership.Id);
        Assert.Equal(ownership.Id, persisted.Id);
    }

    [Fact]
    public async Task EnsureCreatedHierarchyAsync_ClaimsOnlyDirectoriesCreatedExclusively()
    {
        var destination = Path.Join(_root, "Author", "Book");

        var ownerships = await _store.EnsureCreatedHierarchyAsync(
            destination,
            _root,
            FileSystemPathSemantics.CurrentHostDefault,
            "test",
            Guid.NewGuid(),
            audiobookId: 7);

        Assert.Equal(2, ownerships.Count);
        Assert.True(Directory.Exists(destination));
        var rootResolution = await _store.ResolveOwnedAsync(
            _root,
            FileSystemPathSemantics.CurrentHostDefault);
        Assert.Equal(LibraryDirectoryOwnershipResolutionState.Unowned, rootResolution.State);
        foreach (var ownership in ownerships)
        {
            AssertNoPersistentOwnershipArtifacts(ownership);
        }
    }

    [Fact]
    public async Task EnrolledDestinationRemovedBeforePublication_IsNotRecreatedAndOwnershipFailsClosed()
    {
        var sourceDirectory = Path.Join(_root, "Source");
        Directory.CreateDirectory(sourceDirectory);
        var source = Path.Join(sourceDirectory, "book.m4b");
        await File.WriteAllTextAsync(source, "audio");
        var destinationDirectory = Path.Join(_root, "Author", "Book");
        var destination = Path.Join(destinationDirectory, "book.m4b");
        var ownerships = await _store.EnsureCreatedHierarchyAsync(
            destinationDirectory,
            _root,
            FileSystemPathSemantics.CurrentHostDefault,
            "publication-race",
            Guid.NewGuid(),
            audiobookId: 7);
        var destinationOwnership = ownerships.Single(ownership =>
            FileSystemPathIdentity.AreEquivalent(
                ownership.CanonicalPath,
                destinationDirectory,
                FileSystemPathSemantics.CurrentHostDefault));
        var siblingMarker = LibraryDirectoryOwnershipMarker
            .GetMarkerPaths(destinationOwnership)
            .Single(marker => !FileSystemPathIdentity.IsSameOrInside(
                marker,
                destinationDirectory,
                FileSystemPathSemantics.CurrentHostDefault));
        Assert.False(File.Exists(siblingMarker));
        Directory.Delete(destinationDirectory, recursive: true);
        var mover = new FileMover(
            new NullLogger<FileMover>(),
            semanticsResolver: new FileSystemSemanticsResolver());

        var copied = await mover.CopyFileAsync(source, destination);

        Assert.False(copied);
        Assert.True(File.Exists(source));
        Assert.False(Directory.Exists(destinationDirectory));
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(siblingMarker));
        var resolution = await _store.ResolveOwnedAsync(
            destinationDirectory,
            FileSystemPathSemantics.CurrentHostDefault);
        Assert.Equal(
            LibraryDirectoryOwnershipResolutionState.Unavailable,
            resolution.State);
        await using var db = await _factory.CreateDbContextAsync();
        var durableOwnership = await db.LibraryDirectoryOwnerships
            .AsNoTracking()
            .SingleAsync(ownership => ownership.Id == destinationOwnership.Id);
        Assert.Equal(
            LibraryDirectoryOwnershipState.Owned,
            durableOwnership.State);
    }

    [Fact]
    public async Task EnsureCreatedHierarchyAsync_PersistenceFailureRemovesOnlyUnchangedEmptyCreation()
    {
        var destination = Path.Join(_root, "FailedEmptyCreation");
        var store = new EfLibraryDirectoryOwnershipStore(
            new FailFirstContextCreationFactory(_factory),
            TimeProvider.System,
            new LibraryDirectoryOwnershipBoundaryAuthorizer(_factory));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.EnsureCreatedHierarchyAsync(
                destination,
                _root,
                FileSystemPathSemantics.CurrentHostDefault,
                "test-failed-create"));

        Assert.False(Directory.Exists(destination));
        var resolution = await _store.ResolveOwnedAsync(
            destination,
            FileSystemPathSemantics.CurrentHostDefault);
        Assert.Equal(LibraryDirectoryOwnershipResolutionState.Unowned, resolution.State);
    }

    [Fact]
    public async Task EnsureCreatedHierarchyAsync_PersistenceFailurePreservesChangedCreation()
    {
        var destination = Path.Join(_root, "FailedChangedCreation");
        var foreignFile = Path.Join(destination, "foreign.txt");
        var store = new EfLibraryDirectoryOwnershipStore(
            new FailFirstContextCreationFactory(
                _factory,
                () => File.WriteAllText(foreignFile, "foreign")),
            TimeProvider.System,
            new LibraryDirectoryOwnershipBoundaryAuthorizer(_factory));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.EnsureCreatedHierarchyAsync(
                destination,
                _root,
                FileSystemPathSemantics.CurrentHostDefault,
                "test-failed-changed-create"));

        Assert.True(Directory.Exists(destination));
        Assert.Equal("foreign", await File.ReadAllTextAsync(foreignFile));
        var resolution = await _store.ResolveOwnedAsync(
            destination,
            FileSystemPathSemantics.CurrentHostDefault);
        Assert.Equal(LibraryDirectoryOwnershipResolutionState.Unowned, resolution.State);
    }

    [Fact]
    public async Task EnsureCreatedHierarchyAsync_CancellationAfterExclusiveCreationFinishesDurableClaim()
    {
        var destination = Path.Join(_root, "CanceledAfterCreate");
        using var cancellation = new CancellationTokenSource();
        var store = new EfLibraryDirectoryOwnershipStore(
            new CancelOnFirstContextCreationFactory(_factory, cancellation),
            TimeProvider.System,
            new LibraryDirectoryOwnershipBoundaryAuthorizer(_factory));

        var ownerships = await store.EnsureCreatedHierarchyAsync(
            destination,
            _root,
            FileSystemPathSemantics.CurrentHostDefault,
            "test-canceled-after-create",
            cancellationToken: cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        var ownership = Assert.Single(ownerships);
        AssertNoPersistentOwnershipArtifacts(ownership);
        var resolution = await _store.ResolveOwnedAsync(
            destination,
            FileSystemPathSemantics.CurrentHostDefault);
        Assert.Equal(LibraryDirectoryOwnershipResolutionState.Owned, resolution.State);
        Assert.Equal(ownership.Id, resolution.Ownership?.Id);
    }

    [Fact]
    public async Task EnsureCreatedHierarchyAsync_ExistingDurableClaimDoesNotRequireMarkers()
    {
        var destination = Path.Join(_root, "Author", "Book");
        var ownerships = await _store.EnsureCreatedHierarchyAsync(
            destination,
            _root,
            FileSystemPathSemantics.CurrentHostDefault,
            "test");
        var ownership = ownerships.Single(item =>
            FileSystemPathIdentity.AreEquivalent(
                item.CanonicalPath,
                destination,
                FileSystemPathSemantics.CurrentHostDefault));
        AssertNoPersistentOwnershipArtifacts(ownership);

        var repaired = await _store.EnsureCreatedHierarchyAsync(
            destination,
            _root,
            FileSystemPathSemantics.CurrentHostDefault,
            "test-retry");

        Assert.Empty(repaired);
        AssertNoPersistentOwnershipArtifacts(ownership);
        var resolution = await _store.ResolveOwnedAsync(
            destination,
            FileSystemPathSemantics.CurrentHostDefault);
        Assert.Equal(
            LibraryDirectoryOwnershipResolutionState.Owned,
            resolution.State);
    }

    [Fact]
    public async Task EnsureCreatedHierarchyAsync_DoesNotClaimPreExistingParent()
    {
        var author = Path.Join(_root, "Author");
        var destination = Path.Join(author, "Book");
        Directory.CreateDirectory(author);

        var ownerships = await _store.EnsureCreatedHierarchyAsync(
            destination,
            _root,
            FileSystemPathSemantics.CurrentHostDefault,
            "test");

        var ownership = Assert.Single(ownerships);
        Assert.Equal(
            FileSystemPathIdentity.Canonicalize(
                destination,
                FileSystemPathSemantics.CurrentHostDefault.Syntax),
            ownership.CanonicalPath);
        var parentResolution = await _store.ResolveOwnedAsync(
            author,
            FileSystemPathSemantics.CurrentHostDefault);
        Assert.Equal(LibraryDirectoryOwnershipResolutionState.Unowned, parentResolution.State);
    }

    [Fact]
    public async Task ExclusiveDirectoryCreator_ConcurrentAttemptsHaveSingleCreator()
    {
        var directory = Path.Join(_root, "Concurrent");
        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => ExclusiveDirectoryCreator.TryCreate(directory))));

        Assert.Single(results, created => created);
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public async Task RemovingDirectory_CanCompleteAfterDirectoryDeletionAndRestart()
    {
        var directory = Path.Join(_root, "Author");
        Directory.CreateDirectory(directory);
        var ownership = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test"));
        var ownershipKey = Assert.IsType<string>(ownership.PathOwnershipKey);

        await _store.BeginRemovalAsync(ownership.Id, ownershipKey);
        AssertNoPersistentOwnershipArtifacts(ownership);
        Directory.Delete(directory, recursive: false);

        var restartedStore = new EfLibraryDirectoryOwnershipStore(_factory, TimeProvider.System);
        var resolution = await restartedStore.ResolveOwnedAsync(
            directory,
            FileSystemPathSemantics.CurrentHostDefault);
        var removing = Assert.IsType<LibraryDirectoryOwnership>(resolution.Ownership);
        Assert.Equal(LibraryDirectoryOwnershipState.Removing, removing.State);
        await restartedStore.MarkRemovedAsync(removing.Id, ownershipKey);
        await using (var evidenceDb = await _factory.CreateDbContextAsync())
        {
            var retired = await evidenceDb.LibraryDirectoryOwnerships
                .SingleAsync(candidate => candidate.Id == removing.Id);
            var evidence = await evidenceDb
                .LibraryDirectoryOwnershipRetiredMarkers
                .SingleAsync(candidate =>
                    candidate.OwnershipId == removing.Id);
            Assert.Null(retired.ManagedRootFolderId);
            Assert.Null(retired.PathOwnershipKey);
            Assert.Equal(
                LibraryDirectoryOwnershipRetiredMarkerState.Pending,
                evidence.State);
            Assert.False(string.IsNullOrWhiteSpace(
                evidence.CanonicalPayload));
            Assert.False(string.IsNullOrWhiteSpace(
                evidence.PayloadSha256));
            Assert.Equal(ownership.ManagedRootFolderId,
                evidence.OriginalManagedRootFolderId);
        }
        Assert.True(LibraryDirectoryOwnershipMarker.TryDeleteRetiredSiblingMarker(
            removing,
            out var markerDeleteReason), markerDeleteReason);

        var removed = await restartedStore.ResolveOwnedAsync(
            directory,
            FileSystemPathSemantics.CurrentHostDefault);
        Assert.Equal(LibraryDirectoryOwnershipResolutionState.Unowned, removed.State);
    }

    [Fact]
    public async Task RecordCreatedAsync_RemovesRetiredSiblingMarkerFromPriorOwnership()
    {
        var directory = Path.Join(_root, "Recreated");
        Directory.CreateDirectory(directory);
        var prior = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test"));
        var ownershipKey = Assert.IsType<string>(prior.PathOwnershipKey);
        await PublishLegacyOwnershipMarkersAsync(prior);
        var retiredSiblingMarker = LibraryDirectoryOwnershipMarker.GetMarkerPaths(prior)
            .Single(path => !FileSystemPathIdentity.IsSameOrInside(
                path,
                directory,
                FileSystemPathSemantics.CurrentHostDefault));

        await _store.BeginRemovalAsync(prior.Id, ownershipKey);
        LibraryDirectoryOwnershipMarker.DeleteInsideMarker(prior, directory);
        Directory.Delete(directory, recursive: false);
        await _store.MarkRemovedAsync(prior.Id, ownershipKey);
        Assert.True(File.Exists(retiredSiblingMarker));
        await CreateOwnershipReconciler().ReconcileAsync();
        Assert.False(File.Exists(retiredSiblingMarker));
        Directory.CreateDirectory(directory);

        var recreated = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test-recreated"));

        Assert.NotEqual(prior.Id, recreated.Id);
        Assert.NotEqual(prior.OwnershipToken, recreated.OwnershipToken);
        Assert.False(File.Exists(retiredSiblingMarker));
        AssertNoPersistentOwnershipArtifacts(recreated);
    }

    [Fact]
    public async Task RemovalPath_InvalidOwnershipTokenCannotEscapeParent()
    {
        var directory = Path.Join(_root, "InvalidToken");
        Directory.CreateDirectory(directory);
        var ownership = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test"));
        var insideMarker = Path.Join(
            directory,
            LibraryDirectoryOwnershipMarker.FileName);
        Assert.False(File.Exists(insideMarker));
        ownership.OwnershipToken = $"..{Path.DirectorySeparatorChar}outside";

        var quarantineException = Assert.Throws<InvalidOperationException>(() =>
            LibraryDirectoryOwnershipRemoval.GetQuarantinePath(ownership));
        using var publication = PinnedDirectoryCreation.OpenExistingForPublication(
            Path.GetDirectoryName(directory)!,
            Path.GetFileName(directory));
        var markerException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PinnedLibraryDirectoryOwnershipMarker.EnsureAsync(
                ownership,
                publication,
                CancellationToken.None));

        Assert.Contains("token is invalid", quarantineException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("token is invalid", markerException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(insideMarker));
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public async Task RemovalPath_FileReplacementAtOriginalPathFailsClosed()
    {
        var directory = Path.Join(_root, "OriginalFileReplacement");
        Directory.CreateDirectory(directory);
        var ownership = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test"));
        var ownershipKey = Assert.IsType<string>(ownership.PathOwnershipKey);
        await _store.BeginRemovalAsync(ownership.Id, ownershipKey);
        ownership.State = LibraryDirectoryOwnershipState.Removing;
        Directory.Delete(directory, recursive: false);
        await File.WriteAllTextAsync(directory, "user file");

        using var parent = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(_root);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            LibraryDirectoryOwnershipRemoval.RemoveEmptyDirectory(ownership, parent));

        Assert.Contains("occupied by a file", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("user file", await File.ReadAllTextAsync(directory));
    }

    [Fact]
    public async Task RemovalPath_FileAtQuarantinePathFailsClosed()
    {
        var directory = Path.Join(_root, "QuarantineFileReplacement");
        Directory.CreateDirectory(directory);
        var ownership = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test"));
        var ownershipKey = Assert.IsType<string>(ownership.PathOwnershipKey);
        await _store.BeginRemovalAsync(ownership.Id, ownershipKey);
        ownership.State = LibraryDirectoryOwnershipState.Removing;
        Directory.Delete(directory, recursive: false);
        var quarantinePath = LibraryDirectoryOwnershipRemoval.GetQuarantinePath(ownership);
        await File.WriteAllTextAsync(quarantinePath, "foreign file");

        using var parent = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(_root);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            LibraryDirectoryOwnershipRemoval.RemoveEmptyDirectory(ownership, parent));

        Assert.Contains("occupied by a file", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("foreign file", await File.ReadAllTextAsync(quarantinePath));
    }

    [Fact]
    public async Task RemovalPath_EmptyQuarantineAfterInsideMarkerRetirementCompletes()
    {
        var directory = Path.Join(_root, "InterruptedAfterMarkerRetirement");
        Directory.CreateDirectory(directory);
        var ownership = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test"));
        var ownershipKey = Assert.IsType<string>(ownership.PathOwnershipKey);
        await _store.BeginRemovalAsync(ownership.Id, ownershipKey);
        ownership.State = LibraryDirectoryOwnershipState.Removing;
        var quarantinePath =
            LibraryDirectoryOwnershipRemoval.GetQuarantinePath(ownership);
        Directory.Move(directory, quarantinePath);

        LibraryDirectoryOwnershipRemoval.ValidateRecoverableState(ownership);
        using var parent =
            PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(_root);
        var outcome = LibraryDirectoryOwnershipRemoval.RemoveEmptyDirectory(
            ownership,
            parent);

        Assert.Equal(LibraryDirectoryRemovalOutcome.Removed, outcome);
        Assert.False(Directory.Exists(quarantinePath));
        AssertNoPersistentOwnershipArtifacts(ownership);
    }

    [Fact]
    public async Task RecordCreatedAsync_CorruptRemovedIdentityDoesNotBlockNewClaim()
    {
        var directory = Path.Join(_root, "RecreatedAfterCorruptRetiredRow");
        Directory.CreateDirectory(directory);
        var prior = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test"));
        var ownershipKey = Assert.IsType<string>(prior.PathOwnershipKey);

        await _store.BeginRemovalAsync(prior.Id, ownershipKey);
        Directory.Delete(directory, recursive: false);
        await _store.MarkRemovedAsync(prior.Id, ownershipKey);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var retired = await db.LibraryDirectoryOwnerships.SingleAsync(
                candidate => candidate.Id == prior.Id);
            retired.PathIdentityBoundary = "relative-invalid-boundary";
            await db.SaveChangesAsync();
        }
        Directory.CreateDirectory(directory);

        var recreated = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test-recreated"));

        Assert.NotEqual(prior.Id, recreated.Id);
        AssertNoPersistentOwnershipArtifacts(recreated);
    }

    [Fact]
    public async Task Reconciler_TransientRootOutage_PreservesAndRecoversClaim()
    {
        var directory = Path.Join(_root, "TransientOutage");
        Directory.CreateDirectory(directory);
        var ownership = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test"));
        var ownershipKey = Assert.IsType<string>(ownership.PathOwnershipKey);
        var unavailableRoot = $"{_root}-offline";
        Directory.Move(_root, unavailableRoot);
        var reconciler = new LibraryDirectoryOwnershipReconciler(
            _factory,
            new LibraryDirectoryOwnershipBoundaryAuthorizer(_factory),
            new FilesystemMutationCoordinator(),
            NullLogger<LibraryDirectoryOwnershipReconciler>.Instance);

        await reconciler.ReconcileAsync();

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var unavailable = await db.LibraryDirectoryOwnerships.SingleAsync();
            Assert.Equal(
                LibraryDirectoryOwnershipState.Owned,
                unavailable.State);
            Assert.Equal(ownershipKey, unavailable.PathOwnershipKey);
            Assert.False(string.IsNullOrWhiteSpace(
                unavailable.DirectoryObjectIdentityUnavailableReason));
        }

        Directory.Move(unavailableRoot, _root);
        await reconciler.ReconcileAsync();

        await using var verification = await _factory.CreateDbContextAsync();
        var recovered = await verification.LibraryDirectoryOwnerships.SingleAsync();
        Assert.Equal(LibraryDirectoryOwnershipState.Owned, recovered.State);
        Assert.Equal(ownershipKey, recovered.PathOwnershipKey);
        Assert.Null(recovered.DirectoryObjectIdentityUnavailableReason);
        Assert.Null(recovered.StateReason);
    }

    [Fact]
    public async Task Reconciler_MissingRemovingDirectoryConvergesWithoutMarkerProof()
    {
        var directory = Path.Join(_root, "SiblingOnlyRemoval");
        Directory.CreateDirectory(directory);
        var ownership = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test"));
        var ownershipKey = Assert.IsType<string>(ownership.PathOwnershipKey);
        await _store.BeginRemovalAsync(ownership.Id, ownershipKey);
        Directory.Delete(directory);
        var reconciler = new LibraryDirectoryOwnershipReconciler(
            _factory,
            new LibraryDirectoryOwnershipBoundaryAuthorizer(_factory),
            new FilesystemMutationCoordinator(),
            NullLogger<LibraryDirectoryOwnershipReconciler>.Instance);

        await reconciler.ReconcileAsync();

        await using var verification = await _factory.CreateDbContextAsync();
        var persisted = await verification.LibraryDirectoryOwnerships.SingleAsync();
        Assert.Equal(
            LibraryDirectoryOwnershipState.Removed,
            persisted.State);
        Assert.Null(persisted.PathOwnershipKey);
        AssertNoPersistentOwnershipArtifacts(persisted);
    }

    [Fact]
    public async Task Reconciler_LegacyRemovedRowWithoutEvidence_BackfillsAndRetiresSiblingMarker()
    {
        var directory = Path.Join(_root, "LegacyRemovedWithoutEvidence");
        Directory.CreateDirectory(directory);
        var ownership = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "legacy-test"));
        var originalRootId = Assert.IsType<int>(ownership.ManagedRootFolderId);
        var ownershipKey = Assert.IsType<string>(ownership.PathOwnershipKey);
        var siblingMarker = LibraryDirectoryOwnershipMarker
            .GetMarkerPaths(ownership)[1];
        const string originalStateReason =
            "Legacy cleanup completed.\nPreserve this diagnostic.";

        await PublishLegacyOwnershipMarkersAsync(ownership);
        await _store.BeginRemovalAsync(ownership.Id, ownershipKey);
        LibraryDirectoryOwnershipMarker.DeleteInsideMarker(ownership, directory);
        Directory.Delete(directory);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var legacy = await db.LibraryDirectoryOwnerships.SingleAsync(
                candidate => candidate.Id == ownership.Id);
            legacy.State = LibraryDirectoryOwnershipState.Removed;
            legacy.PathOwnershipKey = null;
            legacy.ManagedRootFolderId = null;
            legacy.StateReason =
                LibraryDirectoryOwnershipMigrationPreflight
                    .CreateLegacyRemovedRootStateReason(
                        originalRootId,
                        originalStateReason);
            await db.SaveChangesAsync();
            Assert.Empty(await db.LibraryDirectoryOwnershipRetiredMarkers
                .Where(marker => marker.OwnershipId == ownership.Id)
                .ToListAsync());
        }
        Assert.True(File.Exists(siblingMarker));

        await CreateOwnershipReconciler().ReconcileAsync();

        await using (var verification = await _factory.CreateDbContextAsync())
        {
            var persisted = await verification.LibraryDirectoryOwnerships
                .SingleAsync(candidate => candidate.Id == ownership.Id);
            Assert.Equal(LibraryDirectoryOwnershipState.Removed, persisted.State);
            Assert.Null(persisted.ManagedRootFolderId);
            Assert.Equal(originalStateReason, persisted.StateReason);
            var evidence = await verification
                .LibraryDirectoryOwnershipRetiredMarkers
                .SingleAsync(marker => marker.OwnershipId == ownership.Id);
            Assert.Equal(originalRootId, evidence.OriginalManagedRootFolderId);
            Assert.Equal(
                LibraryDirectoryOwnershipMarker.Version,
                evidence.PayloadVersion);
            Assert.Equal(
                LibraryDirectoryOwnershipRetiredMarkerState.Removed,
                evidence.State);
        }
        Assert.False(File.Exists(siblingMarker));

        await using (var interruptedRace = await _factory.CreateDbContextAsync())
        {
            var persisted = await interruptedRace.LibraryDirectoryOwnerships
                .SingleAsync(candidate => candidate.Id == ownership.Id);
            persisted.StateReason =
                LibraryDirectoryOwnershipMigrationPreflight
                    .CreateLegacyRemovedRootStateReason(
                        originalRootId,
                        originalStateReason);
            await interruptedRace.SaveChangesAsync();
        }

        await CreateOwnershipReconciler().ReconcileAsync();
        await using var repeated = await _factory.CreateDbContextAsync();
        Assert.Equal(
            originalStateReason,
            (await repeated.LibraryDirectoryOwnerships
                .SingleAsync(candidate => candidate.Id == ownership.Id)).StateReason);
        Assert.Single(await repeated.LibraryDirectoryOwnershipRetiredMarkers
            .Where(marker => marker.OwnershipId == ownership.Id)
            .ToListAsync());
    }

    [Fact]
    public async Task Reconciler_LegacyMissingBothProofMarksOnlyDatabaseRowRemoved()
    {
        var fixture = await PrepareLegacyMissingBothAsync("LegacyMissingBoth");
        var reconciler = CreateOwnershipReconciler();

        await reconciler.ReconcileAsync();

        await using var verification = await _factory.CreateDbContextAsync();
        var persisted = await verification.LibraryDirectoryOwnerships
            .SingleAsync(candidate => candidate.Id == fixture.Ownership.Id);
        Assert.Equal(LibraryDirectoryOwnershipState.Removed, persisted.State);
        Assert.Null(persisted.PathOwnershipKey);
        Assert.True(File.Exists(fixture.SiblingMarkerPath));
        Assert.False(Directory.Exists(fixture.DirectoryPath));
        Assert.False(Directory.Exists(fixture.QuarantinePath));

        await CreateOwnershipReconciler().ReconcileAsync();
        Assert.False(File.Exists(fixture.SiblingMarkerPath));
        var evidence = await verification
            .LibraryDirectoryOwnershipRetiredMarkers
            .SingleAsync(candidate =>
                candidate.OwnershipId == fixture.Ownership.Id);
        Assert.Equal(
            LibraryDirectoryOwnershipRetiredMarkerState.Removed,
            evidence.State);
    }

    [Fact]
    public async Task Reconciler_LegacyMissingBothCorruptMarker_PreservesArtifactAndConvergesRemoval()
    {
        var fixture = await PrepareLegacyMissingBothAsync(
            "LegacyMissingBothCorrupt");
        File.SetAttributes(
            fixture.SiblingMarkerPath,
            FileAttributes.Normal);
        await File.WriteAllTextAsync(fixture.SiblingMarkerPath, "{invalid");

        await CreateOwnershipReconciler().ReconcileAsync();

        await AssertRemovalConvergedWithPreservedLegacyArtifactAsync(fixture);
    }

    [Fact]
    public async Task Reconciler_LegacyMissingBothWrongToken_PreservesArtifactAndConvergesRemoval()
    {
        var fixture = await PrepareLegacyMissingBothAsync(
            "LegacyMissingBothWrongToken");
        File.SetAttributes(
            fixture.SiblingMarkerPath,
            FileAttributes.Normal);
        await File.WriteAllTextAsync(
            fixture.SiblingMarkerPath,
            System.Text.Json.JsonSerializer.Serialize(
                new LibraryDirectoryOwnershipMarker.MarkerPayload(
                    1,
                    Guid.NewGuid().ToString("N"),
                    fixture.Ownership.CanonicalPath)));

        await CreateOwnershipReconciler().ReconcileAsync();

        await AssertRemovalConvergedWithPreservedLegacyArtifactAsync(fixture);
    }

    [Fact]
    public async Task Reconciler_PreUpgradeLegacyMissingBothWithoutV2IdentityMarksRemoved()
    {
        var fixture = await PrepareLegacyMissingBothAsync(
            "LegacyMissingBothNoIdentity");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var persisted = await db.LibraryDirectoryOwnerships
                .SingleAsync(candidate =>
                    candidate.Id == fixture.Ownership.Id);
            persisted.ManagedRootFolderId = null;
            persisted.DirectoryObjectIdentityVersion = null;
            persisted.DirectoryObjectIdentity = null;
            persisted.DirectoryObjectIdentityUnavailableReason = null;
            await db.SaveChangesAsync();
        }

        await CreateOwnershipReconciler().ReconcileAsync();

        await using var verification = await _factory.CreateDbContextAsync();
        var recovered = await verification.LibraryDirectoryOwnerships
            .SingleAsync(candidate =>
                candidate.Id == fixture.Ownership.Id);
        Assert.Equal(LibraryDirectoryOwnershipState.Removed, recovered.State);
        Assert.Null(recovered.PathOwnershipKey);
        Assert.True(File.Exists(fixture.SiblingMarkerPath));
    }

    [Fact]
    public async Task Reconciler_PredecessorDisplacedBeforeUpgradePublication_CompletesInOnePass()
    {
        var directory = Path.Join(_root, "UpgradePredecessorDisplaced");
        Directory.CreateDirectory(directory);
        var ownership = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test"));
        await PublishLegacyOwnershipMarkersAsync(ownership);
        var markerPath = Path.Join(
            directory,
            LibraryDirectoryOwnershipMarker.FileName);
        var currentPayload = await File.ReadAllTextAsync(markerPath);
        File.SetAttributes(markerPath, FileAttributes.Normal);
        await File.WriteAllTextAsync(
            markerPath,
            JsonSerializer.Serialize(
                new LibraryDirectoryOwnershipMarker.MarkerPayload(
                    1,
                    ownership.OwnershipToken,
                    ownership.CanonicalPath)));
        var backupPath = Path.Join(
            directory,
            PinnedDirectoryCreation.GetConditionalReplacementBackupName(
                LibraryDirectoryOwnershipMarker.FileName));
        File.Move(markerPath, backupPath);
        var temporaryPath = markerPath + ".v2.tmp";
        await File.WriteAllTextAsync(temporaryPath, currentPayload);

        await CreateOwnershipReconciler().ReconcileAsync();

        Assert.False(File.Exists(backupPath));
        Assert.False(File.Exists(temporaryPath));
        AssertNoPersistentOwnershipArtifacts(ownership);
    }

    [Fact]
    public async Task Reconciler_UpgradePublishedBeforePredecessorCleanup_RetiresBackupInOnePass()
    {
        var directory = Path.Join(_root, "UpgradePublishedBackupRetained");
        Directory.CreateDirectory(directory);
        var ownership = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test"));
        await PublishLegacyOwnershipMarkersAsync(ownership);
        var backupPath = Path.Join(
            directory,
            PinnedDirectoryCreation.GetConditionalReplacementBackupName(
                LibraryDirectoryOwnershipMarker.FileName));
        await File.WriteAllTextAsync(
            backupPath,
            JsonSerializer.Serialize(
                new LibraryDirectoryOwnershipMarker.MarkerPayload(
                    1,
                    ownership.OwnershipToken,
                    ownership.CanonicalPath)));

        await CreateOwnershipReconciler().ReconcileAsync();

        Assert.False(File.Exists(backupPath));
        AssertNoPersistentOwnershipArtifacts(ownership);
    }

    [Fact]
    public async Task Reconciler_CurrentMarkerWithDisplacedLegacyTemporary_RetiresCrashArtifact()
    {
        var directory = Path.Join(_root, "CompletedLegacyUpgrade");
        Directory.CreateDirectory(directory);
        var ownership = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test"));
        await PublishLegacyOwnershipMarkersAsync(ownership);
        var temporaryPath = Path.Join(
            directory,
            LibraryDirectoryOwnershipMarker.FileName + ".v2.tmp");
        await File.WriteAllTextAsync(
            temporaryPath,
            LibraryDirectoryOwnershipMarker.SerializePayload(
                new LibraryDirectoryOwnershipMarker.MarkerPayload(
                    1,
                    ownership.OwnershipToken,
                    ownership.CanonicalPath)));

        await CreateOwnershipReconciler().ReconcileAsync();

        Assert.False(File.Exists(temporaryPath));
        AssertNoPersistentOwnershipArtifacts(ownership);
        await using var verification = await _factory.CreateDbContextAsync();
        var persisted = await verification.LibraryDirectoryOwnerships
            .SingleAsync(candidate => candidate.Id == ownership.Id);
        Assert.Equal(LibraryDirectoryOwnershipState.Owned, persisted.State);
    }

    [Fact]
    public async Task Reconciler_LegacyMissingBothMixedUpgradeMarkers_PreservesArtifactsAndConvergesRemoval()
    {
        var fixture = await PrepareLegacyMissingBothAsync(
            "LegacyMissingBothMixed");
        await File.WriteAllTextAsync(
            fixture.SiblingMarkerPath + ".v2.tmp",
            LibraryDirectoryOwnershipMarker.SerializePayload(
                fixture.Ownership));

        await CreateOwnershipReconciler().ReconcileAsync();

        await AssertRemovalConvergedWithPreservedLegacyArtifactAsync(fixture);
        Assert.True(File.Exists(fixture.SiblingMarkerPath + ".v2.tmp"));
    }

    [Fact]
    public async Task Reconciler_LegacyMissingBothReplacementDirectoryFailsClosed()
    {
        var fixture = await PrepareLegacyMissingBothAsync(
            "LegacyMissingBothReplacement");
        Directory.CreateDirectory(fixture.DirectoryPath);
        await File.WriteAllTextAsync(
            Path.Join(fixture.DirectoryPath, "foreign.txt"),
            "user content");

        await CreateOwnershipReconciler().ReconcileAsync();

        await AssertLegacyRecoveryRejectedAsync(fixture);
        Assert.Equal(
            "user content",
            await File.ReadAllTextAsync(
                Path.Join(fixture.DirectoryPath, "foreign.txt")));
    }

    [WindowsFact]
    public async Task Reconciler_ForeignRetiredMarkerPath_DoesNotDeleteWindowsAlias()
    {
        var directory = Path.Join(_root, "ForeignRetiredMarkerPath");
        Directory.CreateDirectory(directory);
        var ownership = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test"));
        var ownershipKey = Assert.IsType<string>(ownership.PathOwnershipKey);
        await PublishLegacyOwnershipMarkersAsync(ownership);
        await _store.BeginRemovalAsync(ownership.Id, ownershipKey);
        LibraryDirectoryOwnershipMarker.DeleteInsideMarker(ownership, directory);
        Directory.Delete(directory);
        await _store.MarkRemovedAsync(ownership.Id, ownershipKey);

        var siblingMarkerPath =
            LibraryDirectoryOwnershipMarker.GetMarkerPaths(ownership)[1];
        Assert.True(File.Exists(siblingMarkerPath));
        var foreignMarkerPath = WindowsPathTestFixture
            .GetRootRelativeForeignAlias(siblingMarkerPath);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var evidence = await db.LibraryDirectoryOwnershipRetiredMarkers
                .SingleAsync(candidate => candidate.OwnershipId == ownership.Id);
            evidence.CanonicalMarkerPath = foreignMarkerPath;
            await db.SaveChangesAsync();
        }

        await CreateOwnershipReconciler().ReconcileAsync();

        Assert.True(File.Exists(siblingMarkerPath));
        await using var verification = await _factory.CreateDbContextAsync();
        var persisted = await verification.LibraryDirectoryOwnershipRetiredMarkers
            .SingleAsync(candidate => candidate.OwnershipId == ownership.Id);
        Assert.Equal(
            LibraryDirectoryOwnershipRetiredMarkerState.Pending,
            persisted.State);
    }

    [LinuxFact]
    public async Task TryDeleteRetiredSiblingMarker_AmbiguousPersistedPayload_PreservesMarker()
    {
        var directory = Path.Join(_root, "AmbiguousRetiredMarkerPayload");
        Directory.CreateDirectory(directory);
        var ownership = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test"));
        var ownershipKey = Assert.IsType<string>(ownership.PathOwnershipKey);
        await PublishLegacyOwnershipMarkersAsync(ownership);
        await _store.BeginRemovalAsync(ownership.Id, ownershipKey);
        LibraryDirectoryOwnershipMarker.DeleteInsideMarker(ownership, directory);
        Directory.Delete(directory);
        await _store.MarkRemovedAsync(ownership.Id, ownershipKey);

        var siblingMarkerPath =
            LibraryDirectoryOwnershipMarker.GetMarkerPaths(ownership)[1];
        Assert.True(File.Exists(siblingMarkerPath));
        var ambiguousCanonicalPath = "/" + ownership.CanonicalPath;
        Assert.False(FileSystemPathIdentity.TryDetectAbsoluteSyntax(
            ambiguousCanonicalPath,
            out _));
        File.SetAttributes(siblingMarkerPath, FileAttributes.Normal);
        await File.WriteAllTextAsync(
            siblingMarkerPath,
            LibraryDirectoryOwnershipMarker.SerializePayload(
                new LibraryDirectoryOwnershipMarker.MarkerPayload(
                    LibraryDirectoryOwnershipMarker.Version,
                    ownership.OwnershipToken,
                    ambiguousCanonicalPath,
                    ownership.ManagedRootFolderId,
                    ownership.DirectoryObjectIdentityVersion,
                    ownership.DirectoryObjectIdentity)));

        Assert.False(LibraryDirectoryOwnershipMarker.TryDeleteRetiredSiblingMarker(
            ownership,
            out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.True(File.Exists(siblingMarkerPath));
    }

    [Fact]
    public async Task Reconciler_RetiredMarkerReplacementRemainsPending()
    {
        var directory = Path.Join(_root, "RetiredMarkerReplacement");
        Directory.CreateDirectory(directory);
        var ownership = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test"));
        var ownershipKey = Assert.IsType<string>(
            ownership.PathOwnershipKey);
        await PublishLegacyOwnershipMarkersAsync(ownership);
        await _store.BeginRemovalAsync(ownership.Id, ownershipKey);
        LibraryDirectoryOwnershipMarker.DeleteInsideMarker(
            ownership,
            directory);
        Directory.Delete(directory);
        await _store.MarkRemovedAsync(ownership.Id, ownershipKey);
        var siblingMarkerPath =
            LibraryDirectoryOwnershipMarker.GetMarkerPaths(ownership)[1];
        File.SetAttributes(siblingMarkerPath, FileAttributes.Normal);
        await File.WriteAllTextAsync(
            siblingMarkerPath,
            LibraryDirectoryOwnershipMarker.SerializePayload(
                new LibraryDirectoryOwnershipMarker.MarkerPayload(
                    LibraryDirectoryOwnershipMarker.Version,
                    Guid.NewGuid().ToString("N"),
                    ownership.CanonicalPath,
                    ownership.ManagedRootFolderId,
                    ownership.DirectoryObjectIdentityVersion,
                    ownership.DirectoryObjectIdentity)));

        await CreateOwnershipReconciler().ReconcileAsync();

        Assert.True(File.Exists(siblingMarkerPath));
        await using var verification = await _factory.CreateDbContextAsync();
        var evidence = await verification
            .LibraryDirectoryOwnershipRetiredMarkers
            .SingleAsync(candidate =>
                candidate.OwnershipId == ownership.Id);
        Assert.Equal(
            LibraryDirectoryOwnershipRetiredMarkerState.Pending,
            evidence.State);
    }

    private static void AssertNoPersistentOwnershipArtifacts(
        LibraryDirectoryOwnership ownership)
    {
        var markerPaths = LibraryDirectoryOwnershipMarker.GetMarkerPaths(ownership);
        Assert.False(File.Exists(markerPaths[0]));
        Assert.False(File.Exists(markerPaths[0] + ".v2.tmp"));
        Assert.False(File.Exists(markerPaths[0] + ".migration.tmp"));
        Assert.False(File.Exists(Path.Join(
            Path.GetDirectoryName(markerPaths[0])!,
            PinnedDirectoryCreation.GetConditionalReplacementBackupName(
                Path.GetFileName(markerPaths[0])))));
        Assert.False(File.Exists(markerPaths[1]));
        Assert.False(File.Exists(markerPaths[1] + ".v2.tmp"));
        Assert.False(File.Exists(markerPaths[1] + ".migration.tmp"));
        Assert.False(File.Exists(Path.Join(
            Path.GetDirectoryName(markerPaths[1])!,
            PinnedDirectoryCreation.GetConditionalReplacementBackupName(
                Path.GetFileName(markerPaths[1])))));
    }

    private static async Task PublishLegacyOwnershipMarkersAsync(
        LibraryDirectoryOwnership ownership)
    {
        var parentPath = Path.GetDirectoryName(ownership.CanonicalPath)
            ?? throw new InvalidOperationException(
                "The test ownership path has no parent directory.");
        using var parent = PinnedDirectoryCreation.OpenPinnedBoundary(parentPath);
        using var publication = parent.OpenExistingChildForPublication(
            Path.GetFileName(ownership.CanonicalPath));
        await PinnedLibraryDirectoryOwnershipMarker.EnsureAsync(
            ownership,
            publication,
            CancellationToken.None);
    }

    private LibraryDirectoryOwnershipReconciler CreateOwnershipReconciler() =>
        new(
            _factory,
            new LibraryDirectoryOwnershipBoundaryAuthorizer(_factory),
            new FilesystemMutationCoordinator(),
            NullLogger<LibraryDirectoryOwnershipReconciler>.Instance);

    private async Task<LegacyRemovalFixture> PrepareLegacyMissingBothAsync(
        string directoryName)
    {
        var directory = Path.Join(_root, directoryName);
        Directory.CreateDirectory(directory);
        var ownership = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test"));
        var ownershipKey = Assert.IsType<string>(ownership.PathOwnershipKey);
        await PublishLegacyOwnershipMarkersAsync(ownership);
        await _store.BeginRemovalAsync(ownership.Id, ownershipKey);
        LibraryDirectoryOwnershipMarker.DeleteInsideMarker(
            ownership,
            directory);
        Directory.Delete(directory);
        var siblingMarkerPath =
            LibraryDirectoryOwnershipMarker.GetMarkerPaths(ownership)[1];
        File.SetAttributes(siblingMarkerPath, FileAttributes.Normal);
        await File.WriteAllTextAsync(
            siblingMarkerPath,
            System.Text.Json.JsonSerializer.Serialize(
                new LibraryDirectoryOwnershipMarker.MarkerPayload(
                    1,
                    ownership.OwnershipToken,
                    ownership.CanonicalPath)));
        return new LegacyRemovalFixture(
            ownership,
            ownershipKey,
            directory,
            LibraryDirectoryOwnershipRemoval.GetQuarantinePath(ownership),
            siblingMarkerPath);
    }

    private async Task AssertRemovalConvergedWithPreservedLegacyArtifactAsync(
        LegacyRemovalFixture fixture)
    {
        await using var verification = await _factory.CreateDbContextAsync();
        var persisted = await verification.LibraryDirectoryOwnerships
            .SingleAsync(candidate => candidate.Id == fixture.Ownership.Id);
        Assert.Equal(LibraryDirectoryOwnershipState.Removed, persisted.State);
        Assert.Null(persisted.PathOwnershipKey);
        Assert.True(File.Exists(fixture.SiblingMarkerPath));
        Assert.False(Directory.Exists(fixture.DirectoryPath));
        Assert.False(Directory.Exists(fixture.QuarantinePath));
    }

    private async Task AssertLegacyRecoveryRejectedAsync(
        LegacyRemovalFixture fixture)
    {
        await using var verification = await _factory.CreateDbContextAsync();
        var persisted = await verification.LibraryDirectoryOwnerships
            .SingleAsync(candidate => candidate.Id == fixture.Ownership.Id);
        Assert.Equal(LibraryDirectoryOwnershipState.Removing, persisted.State);
        Assert.Equal(fixture.OwnershipKey, persisted.PathOwnershipKey);
        Assert.False(string.IsNullOrWhiteSpace(persisted.StateReason));
    }

    private sealed record LegacyRemovalFixture(
        LibraryDirectoryOwnership Ownership,
        string OwnershipKey,
        string DirectoryPath,
        string QuarantinePath,
        string SiblingMarkerPath);

    private sealed class FailFirstContextCreationFactory(
        IDbContextFactory<ListenArrDbContext> inner,
        Action? beforeFailure = null)
        : IDbContextFactory<ListenArrDbContext>
    {
        private int _failed;

        public ListenArrDbContext CreateDbContext()
        {
            FailOnce();
            return inner.CreateDbContext();
        }

        public async Task<ListenArrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
        {
            FailOnce();
            return await inner.CreateDbContextAsync(cancellationToken);
        }

        private void FailOnce()
        {
            if (Interlocked.Exchange(ref _failed, 1) != 0)
            {
                return;
            }

            beforeFailure?.Invoke();
            throw new InvalidOperationException("Injected ownership persistence failure.");
        }
    }

    private sealed class CancelOnFirstContextCreationFactory(
        IDbContextFactory<ListenArrDbContext> inner,
        CancellationTokenSource cancellation)
        : IDbContextFactory<ListenArrDbContext>
    {
        private int _canceled;

        public ListenArrDbContext CreateDbContext()
        {
            CancelRequest();
            return inner.CreateDbContext();
        }

        public async Task<ListenArrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
        {
            CancelRequest();
            return await inner.CreateDbContextAsync(cancellationToken);
        }

        private void CancelRequest()
        {
            if (Interlocked.Exchange(ref _canceled, 1) == 0)
            {
                cancellation.Cancel();
            }
        }
    }

    private sealed class TestDbContextFactory(
        DbContextOptions<ListenArrDbContext> options)
        : IDbContextFactory<ListenArrDbContext>
    {
        public ListenArrDbContext CreateDbContext() => new(options);

        public Task<ListenArrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
