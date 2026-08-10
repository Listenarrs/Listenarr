using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

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

    [Fact]
    public async Task BoundaryAuthorizer_AuthorizedRootWithChangedFilesystemSemantics_IsRejectedUntilRepaired()
    {
        var actual = FileSystemPathSemantics.CurrentHostDefault;
        var persistedSensitivity = actual.CaseSensitivity
            == FileSystemCaseSensitivity.Sensitive
                ? FileSystemCaseSensitivity.Insensitive
                : FileSystemCaseSensitivity.Sensitive;
        var persisted = new FileSystemPathSemantics(actual.Syntax, persistedSensitivity);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = await db.RootFolders.SingleAsync();
            root.CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto;
            root.ResolvedCaseSensitivity = persistedSensitivity;
            root.PathIdentityState = PathIdentityState.Valid;
            root.PathIdentityKey = FileSystemPathIdentity.CreateKey("root", _root, persisted);
            await db.SaveChangesAsync();
        }
        var authorizer = new LibraryDirectoryOwnershipBoundaryAuthorizer(_factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            authorizer.AuthorizeAsync(
                _root,
                persisted,
                CancellationToken.None));
    }

    [Fact]
    public async Task BoundaryAuthorizer_AuthorizedRootReturnsAfterTransientFailure_UsesLiveGeneration()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = await db.RootFolders.SingleAsync();
            root.DirectoryObjectIdentityUnavailableReason =
                "The directory was unavailable during startup.";
            await db.SaveChangesAsync();
        }
        var authorizer = new LibraryDirectoryOwnershipBoundaryAuthorizer(_factory);

        using var authorization = await authorizer.AuthorizeAsync(
            _root,
            FileSystemPathSemantics.CurrentHostDefault,
            CancellationToken.None);

        Assert.True(authorization.RootFolderId > 0);
    }

    [Fact]
    public async Task BoundaryAuthorizer_ActiveRelocationUnavailableReason_RemainsBlocking()
    {
        var targetRoot = Path.Join(
            Path.GetTempPath(),
            "listenarr-tests",
            $"relocation-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(targetRoot);
        var identity = await new DirectoryObjectIdentityResolver().ResolveAsync(targetRoot);
        Assert.True(identity.IsAvailable, identity.UnavailableReason);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            rootId = (await db.RootFolders.SingleAsync()).Id;
            db.RootFolderRelocations.Add(new RootFolderRelocation
            {
                RootFolderId = rootId,
                ActiveRootFolderId = rootId,
                SourcePath = _root,
                TargetPath = targetRoot,
                Mode = RootFolderRelocationMode.MetadataOnly,
                Status = RootFolderRelocationStatus.NeedsAttention,
                TargetCaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                TargetDirectoryObjectIdentityVersion = identity.Version,
                TargetDirectoryObjectIdentity = identity.Value,
                TargetDirectoryObjectIdentityUnavailableReason =
                    "The relocation target requires operator recovery.",
                TargetIdentityEnrollmentState = TargetIdentityEnrollmentState.Unavailable,
                DesiredName = "Test library"
            });
            await db.SaveChangesAsync();
        }
        var authorizer = new LibraryDirectoryOwnershipBoundaryAuthorizer(_factory);
        var childPath = Path.Join(targetRoot, "Author");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            authorizer.AuthorizeContainingRootAsync(
                childPath,
                FileSystemPathSemantics.CurrentHostDefault,
                CancellationToken.None));

        Assert.Contains("authorized physical generation", exception.Message, StringComparison.OrdinalIgnoreCase);
        Directory.Delete(targetRoot, recursive: true);
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
    public async Task RecordCreatedAsync_PersistsDatabaseIdentity()
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
    public async Task RecordCreatedAsync_PathReplacedImmediatelyAfterCommit_DemotesCommittedOwnership()
    {
        var directory = Path.Join(_root, "ReplacedAfterCommit");
        var displaced = directory + ".original";
        Directory.CreateDirectory(directory);
        using var cancellation = new CancellationTokenSource();
        _store.AfterOwnershipCommitForTest = () =>
        {
            Directory.Move(directory, displaced);
            Directory.CreateDirectory(directory);
            cancellation.Cancel();
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.RecordCreatedAsync(
                new LibraryDirectoryOwnershipClaim(
                    directory,
                    FileSystemPathSemantics.CurrentHostDefault,
                    "test",
                    Guid.NewGuid(),
                    AudiobookId: 14),
                cancellation.Token));

        Assert.Contains(
            "physical generation changed",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        await using var db = await _factory.CreateDbContextAsync();
        var persisted = await db.LibraryDirectoryOwnerships.SingleAsync();
        Assert.Equal(LibraryDirectoryOwnershipState.Unavailable, persisted.State);
        Assert.Null(persisted.PathOwnershipKey);
        Assert.NotNull(persisted.DirectoryObjectIdentityUnavailableReason);
        Assert.True(Directory.Exists(displaced));
        Assert.True(Directory.Exists(directory));
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
    public async Task PhysicalPathReplacementFailsNativeGenerationValidation()
    {
        var directory = Path.Join(_root, "Author");
        Directory.CreateDirectory(directory);
        await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test"));

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

    [LinuxFact]
    public async Task ResolveOwnedAsync_DirectoryReplacedAfterPhysicalIdentityPin_FailsClosed()
    {
        var directory = Path.Join(_root, "PinnedGenerationReplacement");
        var displacedDirectory = directory + ".original";
        Directory.CreateDirectory(directory);
        var ownership = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test"));
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
        Assert.Null(resolution.Ownership);
        await using var verification = await _factory.CreateDbContextAsync();
        var persisted = await verification.LibraryDirectoryOwnerships
            .SingleAsync(candidate => candidate.Id == ownership.Id);
        Assert.Equal(ownership.Id, persisted.Id);
    }

    [WindowsFact]
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
    }

    [LinuxFact]
    public async Task EnsureCreatedHierarchyAsync_UnixFinalNameCreationRemainsUnownedWithoutScratchArtifacts()
    {
        var destination = Path.Join(_root, "Author", "Book");

        var ownerships = await _store.EnsureCreatedHierarchyAsync(
            destination,
            _root,
            FileSystemPathSemantics.CurrentHostDefault,
            "test",
            Guid.NewGuid(),
            audiobookId: 7);

        Assert.Empty(ownerships);
        Assert.True(Directory.Exists(destination));
        var resolution = await _store.ResolveOwnedAsync(
            destination,
            FileSystemPathSemantics.CurrentHostDefault);
        Assert.Equal(
            LibraryDirectoryOwnershipResolutionState.Unowned,
            resolution.State);
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(
                _root,
                "*",
                SearchOption.AllDirectories),
            path => Path.GetFileName(path).StartsWith(
                ".listenarr-",
                StringComparison.Ordinal));
    }

    [WindowsFact]
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
        Directory.Delete(destinationDirectory, recursive: true);
        var mover = new FileMover(
            new NullLogger<FileMover>(),
            semanticsResolver: new FileSystemSemanticsResolver(),
            dbContextFactory: _factory,
            timeProvider: TimeProvider.System);

        var copied = await mover.PerformActionOn(
            FileAction.Copy,
            source,
            destination,
            Guid.NewGuid());

        Assert.False(copied);
        Assert.True(File.Exists(source));
        Assert.False(Directory.Exists(destinationDirectory));
        Assert.False(File.Exists(destination));
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

    [WindowsFact]
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

    [WindowsFact]
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

    [WindowsFact]
    public async Task EnsureCreatedHierarchyAsync_PersistenceFailureReplacementDuringCompensationPreservesReplacement()
    {
        var destination = Path.Join(_root, "FailedCompensationReplacement");
        var displacedCreation = destination + ".created-original";
        var factory = new FailFirstThenActOnSecondContextFactory(
            _factory,
            () =>
            {
                Directory.Move(destination, displacedCreation);
                Directory.CreateDirectory(destination);
            });
        var store = new EfLibraryDirectoryOwnershipStore(
            factory,
            TimeProvider.System,
            new LibraryDirectoryOwnershipBoundaryAuthorizer(_factory));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.EnsureCreatedHierarchyAsync(
                destination,
                _root,
                FileSystemPathSemantics.CurrentHostDefault,
                "test-failed-compensation-replacement"));

        Assert.True(Directory.Exists(displacedCreation));
        Assert.True(Directory.Exists(destination));
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
        var resolution = await _store.ResolveOwnedAsync(
            destination,
            FileSystemPathSemantics.CurrentHostDefault);
        Assert.Equal(LibraryDirectoryOwnershipResolutionState.Unowned, resolution.State);
    }

    [WindowsFact]
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
        var resolution = await _store.ResolveOwnedAsync(
            destination,
            FileSystemPathSemantics.CurrentHostDefault);
        Assert.Equal(LibraryDirectoryOwnershipResolutionState.Owned, resolution.State);
        Assert.Equal(ownership.Id, resolution.Ownership?.Id);
    }

    [WindowsFact]
    public async Task EnsureCreatedHierarchyAsync_ExistingDurableClaimResolvesFromDatabaseState()
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

        var repaired = await _store.EnsureCreatedHierarchyAsync(
            destination,
            _root,
            FileSystemPathSemantics.CurrentHostDefault,
            "test-retry");

        Assert.Empty(repaired);
        var resolution = await _store.ResolveOwnedAsync(
            destination,
            FileSystemPathSemantics.CurrentHostDefault);
        Assert.Equal(
            LibraryDirectoryOwnershipResolutionState.Owned,
            resolution.State);
    }

    [WindowsFact]
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
        Directory.Delete(directory, recursive: false);

        var restartedStore = new EfLibraryDirectoryOwnershipStore(_factory, TimeProvider.System);
        var resolution = await restartedStore.ResolveOwnedAsync(
            directory,
            FileSystemPathSemantics.CurrentHostDefault);
        var removing = Assert.IsType<LibraryDirectoryOwnership>(resolution.Ownership);
        Assert.Equal(LibraryDirectoryOwnershipState.Removing, removing.State);
        await restartedStore.MarkRemovedAsync(removing.Id, ownershipKey);
        await using (var verificationDb = await _factory.CreateDbContextAsync())
        {
            var retired = await verificationDb.LibraryDirectoryOwnerships
                .SingleAsync(candidate => candidate.Id == removing.Id);
            Assert.Null(retired.ManagedRootFolderId);
            Assert.Null(retired.PathOwnershipKey);
            Assert.Equal(LibraryDirectoryOwnershipState.Removed, retired.State);
        }

        var removed = await restartedStore.ResolveOwnedAsync(
            directory,
            FileSystemPathSemantics.CurrentHostDefault);
        Assert.Equal(LibraryDirectoryOwnershipResolutionState.Unowned, removed.State);
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
    }

    [Fact]
    public async Task MarkerlessReplacement_PathReplacedImmediatelyAfterCommit_PersistsUnavailableBlocker()
    {
        var directory = Path.Join(_root, "MarkerlessReplacementCommitRace");
        var displacedStale = directory + ".stale";
        var displacedReplacement = directory + ".replacement";
        Directory.CreateDirectory(directory);
        var stale = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test-stale"));
        Directory.Move(directory, displacedStale);
        Directory.CreateDirectory(directory);
        string replacementIdentity;
        using (var replacement = PinnedDirectoryCreation.OpenPinnedBoundary(directory))
        {
            replacementIdentity = replacement.GetDirectoryObjectIdentity();
        }

        Guid moveJobId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var audiobook = new Audiobook
            {
                Title = "Markerless replacement commit race",
                BasePath = displacedStale
            };
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            var move = new MoveJob
            {
                Id = Guid.NewGuid(),
                AudiobookId = audiobook.Id,
                SourcePath = displacedStale,
                RequestedPath = directory,
                ExecutionProtocolVersion = MoveExecutionProtocol.Current,
                Status = MoveJobStatus.Running,
                TargetDirectoryObjectIdentity = replacementIdentity
            };
            move.CreatedDirectories.Add(new MoveJobCreatedDirectory
            {
                Path = directory,
                State = MoveCreatedDirectoryState.Created,
                DirectoryObjectIdentity = replacementIdentity
            });
            db.MoveJobs.Add(move);
            await db.SaveChangesAsync();
            moveJobId = move.Id;
        }

        _store.AfterMarkerlessReplacementCommitForTest = () =>
        {
            Directory.Move(directory, displacedReplacement);
            Directory.Move(displacedStale, directory);
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.TryRetireReplacedByMarkerlessMoveAsync(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                moveJobId,
                replacementIdentity));

        Assert.Contains("changed physical generation", exception.Message, StringComparison.OrdinalIgnoreCase);
        await using var verification = await _factory.CreateDbContextAsync();
        var persisted = await verification.LibraryDirectoryOwnerships
            .SingleAsync(candidate => candidate.Id == stale.Id);
        Assert.Equal(LibraryDirectoryOwnershipState.Unavailable, persisted.State);
        Assert.Null(persisted.PathOwnershipKey);
        Assert.NotNull(persisted.ManagedRootFolderId);
        Assert.False(string.IsNullOrWhiteSpace(persisted.StateReason));
        var resolution = await _store.ResolveOwnedAsync(
            directory,
            FileSystemPathSemantics.CurrentHostDefault);
        Assert.Equal(
            LibraryDirectoryOwnershipResolutionState.Unavailable,
            resolution.State);
        Assert.True(Directory.Exists(directory));
        Assert.True(Directory.Exists(displacedReplacement));
    }

    [Fact]
    public async Task MarkerlessReplacement_UnknownFutureProtocol_CannotRetireOwnership()
    {
        var directory = Path.Join(_root, "MarkerlessReplacementFutureProtocol");
        var displacedStale = directory + ".stale";
        Directory.CreateDirectory(directory);
        var stale = await _store.RecordCreatedAsync(
            new LibraryDirectoryOwnershipClaim(
                directory,
                FileSystemPathSemantics.CurrentHostDefault,
                "test-stale"));
        Directory.Move(directory, displacedStale);
        Directory.CreateDirectory(directory);
        string replacementIdentity;
        using (var replacement = PinnedDirectoryCreation.OpenPinnedBoundary(directory))
        {
            replacementIdentity = replacement.GetDirectoryObjectIdentity();
        }

        Guid moveJobId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var audiobook = new Audiobook
            {
                Title = "Markerless replacement future protocol",
                BasePath = displacedStale
            };
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            var move = new MoveJob
            {
                Id = Guid.NewGuid(),
                AudiobookId = audiobook.Id,
                SourcePath = displacedStale,
                RequestedPath = directory,
                ExecutionProtocolVersion = MoveExecutionProtocol.Current + 1,
                Status = MoveJobStatus.Running,
                TargetDirectoryObjectIdentity = replacementIdentity
            };
            move.CreatedDirectories.Add(new MoveJobCreatedDirectory
            {
                Path = directory,
                State = MoveCreatedDirectoryState.Created,
                DirectoryObjectIdentity = replacementIdentity
            });
            db.MoveJobs.Add(move);
            await db.SaveChangesAsync();
            moveJobId = move.Id;
        }

        var retired = await _store.TryRetireReplacedByMarkerlessMoveAsync(
            directory,
            FileSystemPathSemantics.CurrentHostDefault,
            moveJobId,
            replacementIdentity);

        Assert.False(retired);
        await using var verification = await _factory.CreateDbContextAsync();
        var persisted = await verification.LibraryDirectoryOwnerships
            .SingleAsync(candidate => candidate.Id == stale.Id);
        Assert.Equal(LibraryDirectoryOwnershipState.Owned, persisted.State);
        Assert.False(string.IsNullOrWhiteSpace(persisted.PathOwnershipKey));
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
    }

    private LibraryDirectoryOwnershipReconciler CreateOwnershipReconciler() =>
        new(
            _factory,
            new LibraryDirectoryOwnershipBoundaryAuthorizer(_factory),
            new FilesystemMutationCoordinator(),
            NullLogger<LibraryDirectoryOwnershipReconciler>.Instance);

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

    private sealed class FailFirstThenActOnSecondContextFactory(
        IDbContextFactory<ListenArrDbContext> inner,
        Action beforeSecondContext)
        : IDbContextFactory<ListenArrDbContext>
    {
        private int _calls;

        public ListenArrDbContext CreateDbContext()
        {
            BeforeCreate();
            return inner.CreateDbContext();
        }

        public async Task<ListenArrDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
        {
            BeforeCreate();
            return await inner.CreateDbContextAsync(cancellationToken);
        }

        private void BeforeCreate()
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                throw new InvalidOperationException("Injected ownership persistence failure.");
            }
            if (call == 2)
            {
                beforeSecondContext();
            }
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
