using Listenarr.Application.Common.Exceptions;
using Listenarr.Tests.Mocks;
using Listenarr.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;

using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

[Trait("Name", "RootFolderRelocationServiceTests")]
[Trait("Category", "Infrastructure")]
public sealed class RootFolderRelocationServiceTests : BaseTests
{
    private readonly string _databasePath = Path.Join(
        Path.GetTempPath(),
        "listenarr-tests",
        $"relocation-{Guid.NewGuid():N}.db");
    private string TempRoot => Path.GetDirectoryName(_databasePath)!;
    private TestDbContextFactory _factory = null!;
    private readonly AudiobookOperationCoordinator _operationCoordinator = new();

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite($"Data Source={_databasePath};Pooling=False")
            .Options;
        _factory = new TestDbContextFactory(options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    public override async Task DisposeAsync()
    {
        _operationCoordinator.Dispose();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
        await base.DisposeAsync();
    }

    [Fact]
    public async Task StartRelocation_ExpectedSourceChanged_RejectsBeforeCreatingSaga()
    {
        var source = Path.Join(TempRoot, $"expected-source-{Guid.NewGuid():N}");
        var staleSource = Path.Join(TempRoot, $"stale-source-{Guid.NewGuid():N}");
        var target = Path.Join(TempRoot, $"expected-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var service = CreateService();

        var exception = await Assert.ThrowsAsync<RootFolderPathChangeRejectedException>(() =>
            service.StartAsync(
                rootId,
                new RootFolderPathChangeCommand(
                    target,
                    RootFolderRelocationMode.Relocate,
                    true,
                    "Moved Library",
                    false,
                    FileSystemCaseSensitivityMode.Auto,
                    staleSource)));

        Assert.Equal("root_folder_changed_while_editing", exception.Code);
        Assert.Contains("changed", exception.Message, StringComparison.OrdinalIgnoreCase);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Empty(verification.RootFolderRelocations);
        Assert.Empty(verification.MoveJobs);
        Assert.Equal(source, (await verification.RootFolders.SingleAsync()).Path);
    }

    [Fact]
    public async Task StartRelocation_NoMoveJobs_TargetGenerationReplacedBeforeMetadataCommit_FailsClosed()
    {
        var source = Path.Join(
            TempRoot,
            $"no-jobs-replaced-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            TempRoot,
            $"no-jobs-replaced-target-{Guid.NewGuid():N}");
        var displacedTarget = target + "-displaced";
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var resolver = new DirectoryObjectIdentityResolver();
        var replacingResolver = new Mock<IDirectoryObjectIdentityResolver>();
        replacingResolver
            .Setup(candidate => candidate.ResolveAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (path, cancellationToken) =>
            {
                var identity = await resolver.ResolveAsync(path, cancellationToken);
                if (string.Equals(
                        Path.GetFullPath(path),
                        Path.GetFullPath(target),
                        StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Move(target, displacedTarget);
                    Directory.CreateDirectory(target);
                }
                return identity;
            });
        var service = CreateService(
            directoryObjectIdentityResolver: replacingResolver.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartAsync(rootId, BuildRelocationCommand(target)));

        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(source, (await verification.RootFolders.SingleAsync()).Path);
        Assert.Empty(verification.RootFolderRelocations);
        Assert.Empty(verification.MoveJobs);
        Assert.True(Directory.Exists(displacedTarget));
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public async Task StartRelocation_ExplicitSemanticsTargetIdentityAccessDenied_RejectsBeforeSagaPublication()
    {
        var source = Path.Join(
            TempRoot,
            $"target-identity-denied-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            TempRoot,
            $"target-identity-denied-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var realResolver = new DirectoryObjectIdentityResolver();
        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        identityResolver
            .Setup(candidate => candidate.ResolveAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((path, cancellationToken) =>
                string.Equals(
                    Path.GetFullPath(path),
                    Path.GetFullPath(target),
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal)
                    ? Task.FromResult(DirectoryObjectIdentityResolution.Unavailable(
                        "Injected target identity access denial.",
                        DirectoryObjectIdentityFailureKind.AccessDenied))
                    : realResolver.ResolveAsync(path, cancellationToken));
        var service = CreateService(
            directoryObjectIdentityResolver: identityResolver.Object);

        var exception = await Assert.ThrowsAsync<RootFolderPathChangeRejectedException>(() =>
            service.StartAsync(
                rootId,
                new RootFolderPathChangeCommand(
                    target,
                    RootFolderRelocationMode.Relocate,
                    true,
                    "Moved Library",
                    false,
                    FileSystemCaseSensitivityMode.Sensitive)));

        Assert.Equal("root_folder_target_unavailable", exception.Code);
        Assert.Contains(
            "Injected target identity access denial",
            exception.Message,
            StringComparison.Ordinal);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
        Assert.Empty(await verification.MoveJobs.ToListAsync());
        Assert.Equal(source, (await verification.RootFolders.SingleAsync()).Path);
        Assert.True(Directory.Exists(target));
        identityResolver.VerifyAll();
    }

    [Fact]
    public async Task StartRelocation_TargetAutoSemanticsOnlyBehavioral_RejectsBeforeSagaCreation()
    {
        var source = Path.Join(TempRoot, $"behavioral-target-source-{Guid.NewGuid():N}");
        var target = Path.Join(TempRoot, $"behavioral-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        var semanticsResolver = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        semanticsResolver
            .Setup(resolver => resolver.ResolveAsync(
                Path.GetFullPath(target),
                FileSystemCaseSensitivityMode.Auto,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileSystemSemanticsResolution(
                semantics,
                PathIdentityState.Valid,
                target,
                EvidenceKind: FileSystemSemanticsEvidenceKind.BehavioralObservation));

        var exception = await Assert.ThrowsAsync<RootFolderPathChangeRejectedException>(() =>
            CreateService(semanticsResolver: semanticsResolver.Object).StartAsync(
                rootId,
                BuildRelocationCommand(target)));

        Assert.Equal(
            "root_folder_target_mutation_semantics_unproven",
            exception.Code);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.False(await verification.RootFolderRelocations.AnyAsync());
        Assert.False(await verification.MoveJobs.AnyAsync());
        semanticsResolver.VerifyAll();
    }

    [Fact]
    public async Task StartRelocation_SourceAutoSemanticsOnlyBehavioral_RejectsBeforeSagaCreation()
    {
        var source = Path.Join(TempRoot, $"behavioral-source-{Guid.NewGuid():N}");
        var target = Path.Join(TempRoot, $"behavioral-source-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                ResolvedCaseSensitivity = semantics.CaseSensitivity,
                PathIdentityState = PathIdentityState.Valid,
                PathIdentityKey = FileSystemPathIdentity.CreateKey(
                    "root",
                    source,
                    semantics)
            };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var explicitMode = semantics.CaseSensitivity == FileSystemCaseSensitivity.Sensitive
            ? FileSystemCaseSensitivityMode.Sensitive
            : FileSystemCaseSensitivityMode.Insensitive;
        var semanticsResolver = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        semanticsResolver
            .Setup(resolver => resolver.ResolveAsync(
                Path.GetFullPath(target),
                explicitMode,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileSystemSemanticsResolution(
                semantics,
                PathIdentityState.Valid,
                target));
        semanticsResolver
            .Setup(resolver => resolver.ResolveAsync(
                Path.GetFullPath(source),
                FileSystemCaseSensitivityMode.Auto,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileSystemSemanticsResolution(
                semantics,
                PathIdentityState.Valid,
                source,
                EvidenceKind: FileSystemSemanticsEvidenceKind.BehavioralObservation));

        var exception = await Assert.ThrowsAsync<RootFolderPathChangeRejectedException>(() =>
            CreateService(semanticsResolver: semanticsResolver.Object).StartAsync(
                rootId,
                new RootFolderPathChangeCommand(
                    target,
                    RootFolderRelocationMode.Relocate,
                    true,
                    "Moved Library",
                    false,
                    explicitMode)));

        Assert.Equal(
            "root_folder_source_mutation_semantics_unproven",
            exception.Code);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.False(await verification.RootFolderRelocations.AnyAsync());
        Assert.False(await verification.MoveJobs.AnyAsync());
        semanticsResolver.VerifyAll();
    }

    [Fact]
    public async Task StartRelocation_PersistsSagaAndJobsWithoutChangingRootOrAudiobooks()
    {
        var source = Path.Join(Path.GetTempPath(), $"relocation-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"relocation-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Join(source, "Author", "Title"));
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            var audiobook = new Audiobook
            {
                Title = "Title",
                BasePath = Path.Join(source, "Author", "Title")
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            await AddTrackedFileAsync(
                db,
                audiobook,
                Path.Join(audiobook.BasePath!, "book.m4b"),
                audiobook.BasePath!);
            rootId = root.Id;
        }

        var manifestScopes = CreateMoveSourceManifestService();
        var service = CreateService(manifestScopes);
        Assert.True(FileSystemPathIdentity.IsSameOrInside(
            Path.Join(source, "Author", "Title"),
            source,
            FileSystemPathSemantics.CurrentHostDefault));
        var result = await service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Moved Library",
                true,
                FileSystemCaseSensitivityMode.Auto));

        await using var verification = await _factory.CreateDbContextAsync();
        var rootAfter = await verification.RootFolders.SingleAsync();
        var audiobookAfter = await verification.Audiobooks.SingleAsync();
        var relocation = await verification.RootFolderRelocations.SingleAsync();
        var job = await verification.MoveJobs
            .Include(candidate => candidate.Entries)
            .SingleAsync();
        Assert.Equal(source, rootAfter.Path);
        Assert.Equal(Path.Join(source, "Author", "Title"), audiobookAfter.BasePath);
        Assert.Equal(rootId, relocation.ActiveRootFolderId);
        Assert.Equal(relocation.Id, job.RelocationId);
        Assert.Equal(source, job.SourceCleanupBoundary);
        Assert.True(job.TryGetSourceIdentity(out var sourceIdentity));
        Assert.Equal(Path.Join(source, "Author", "Title"), sourceIdentity.BoundaryPath);
        Assert.Equal(MoveManifestIdentity.Version, job.IdentityKeyVersion);
        Assert.Single(job.Entries, MoveManifestIdentity.IsSourceBoundaryAuthorization);
        Assert.Single(job.Entries, MoveManifestIdentity.IsTargetBoundaryAuthorization);
        var sourceEntry = Assert.Single(
            job.Entries,
            entry => !MoveManifestIdentity.IsBoundaryAuthorization(entry));
        Assert.Equal("book.m4b", sourceEntry.RelativePath);
        Assert.Equal(RootFolderRelocationStatus.Pending, result.Status);
        Assert.True(await service.IsBoundaryProtectedAsync(
            target,
            FileSystemPathSemantics.CurrentHostDefault));
        Assert.True(await service.IsBoundaryProtectedAsync(
            source,
            FileSystemPathSemantics.CurrentHostDefault));
        Assert.Equal(1, manifestScopes.CreatedScopeCount);
        Assert.Equal(1, manifestScopes.DisposedScopeCount);
    }

    [Fact]
    public async Task StartRelocation_NullBasePathWithTrackedFileUnderRoot_IsStillAffected()
    {
        var source = Path.Join(
            TempRoot,
            $"null-base-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            TempRoot,
            $"null-base-target-{Guid.NewGuid():N}");
        var trackedPath = Path.Join(source, "Author", "Book", "book.m4b");
        Directory.CreateDirectory(source);
        int rootId;
        int audiobookId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            var audiobook = new Audiobook
            {
                Title = "Book",
                BasePath = null
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            await AddTrackedFileAsync(
                db,
                audiobook,
                trackedPath,
                source);
            rootId = root.Id;
            audiobookId = audiobook.Id;
        }

        var service = CreateService(CreateMoveSourceManifestService());
        var result = await service.StartAsync(
            rootId,
            BuildRelocationCommand(target));

        Assert.Equal(RootFolderRelocationStatus.Pending, result.Status);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(source, (await verification.RootFolders.SingleAsync()).Path);
        var relocation = await verification.RootFolderRelocations.SingleAsync();
        var job = await verification.MoveJobs.SingleAsync();
        Assert.Equal(audiobookId, job.AudiobookId);
        Assert.Equal(relocation.Id, job.RelocationId);
        Assert.Equal(Path.GetDirectoryName(trackedPath), job.SourcePath);
    }

    [Fact]
    public async Task MetadataOnly_NullBasePathWithTrackedFileUnderRoot_RebasesTrackedEvidence()
    {
        var source = Path.Join(
            TempRoot,
            $"metadata-null-base-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            TempRoot,
            $"metadata-null-base-target-{Guid.NewGuid():N}");
        var trackedPath = Path.Join(source, "Author", "Book", "book.m4b");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        int rootId;
        int audiobookId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            var audiobook = new Audiobook
            {
                Title = "Book",
                BasePath = null
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            await AddTrackedFileAsync(
                db,
                audiobook,
                trackedPath,
                source);
            rootId = root.Id;
            audiobookId = audiobook.Id;
        }

        var result = await CreateService().StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.MetadataOnly,
                false,
                "Moved Library",
                false,
                FileSystemCaseSensitivityMode.Auto));

        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(target, (await verification.RootFolders.SingleAsync()).Path);
        var audiobookAfter = await verification.Audiobooks
            .SingleAsync(candidate => candidate.Id == audiobookId);
        var expectedBasePath = Path.Join(target, "Author", "Book");
        Assert.Equal(expectedBasePath, audiobookAfter.BasePath);
        var fileAfter = await verification.AudiobookFiles
            .SingleAsync(candidate => candidate.AudiobookId == audiobookId);
        Assert.Equal(Path.Join(expectedBasePath, "book.m4b"), fileAfter.Path);
        Assert.Null(fileAfter.PhysicalObjectIdentity);
    }

    [Fact]
    public async Task StartRelocation_TrackedIdentityBoundaryOutsideRoot_RejectsBeforeCreatingSaga()
    {
        var authorityParent = Path.Join(
            TempRoot,
            $"outside-authority-{Guid.NewGuid():N}");
        var source = Path.Join(authorityParent, "library");
        var target = Path.Join(
            TempRoot,
            $"outside-authority-target-{Guid.NewGuid():N}");
        var audiobookPath = Path.Join(source, "Author", "Title");
        Directory.CreateDirectory(audiobookPath);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            var audiobook = new Audiobook
            {
                Title = "Title",
                BasePath = audiobookPath
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            await AddTrackedFileAsync(
                db,
                audiobook,
                Path.Join(audiobookPath, "book.m4b"),
                authorityParent);
            rootId = root.Id;
        }

        var service = CreateService(CreateMoveSourceManifestService());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartAsync(rootId, BuildRelocationCommand(target)));

        Assert.Contains(
            "not authorized by the relocating root folder boundary",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Empty(verification.RootFolderRelocations);
        Assert.Empty(verification.MoveJobs);
        Assert.Equal(source, (await verification.RootFolders.SingleAsync()).Path);
    }

    [LinuxFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task StartRelocation_TargetBecomesInaccessibleBeforeReservationPlan_DoesNotClaimItAsMissing()
    {
        var source = Path.Join(
            TempRoot,
            $"reservation-inaccessible-source-{Guid.NewGuid():N}");
        var targetParent = Path.Join(
            TempRoot,
            $"reservation-inaccessible-parent-{Guid.NewGuid():N}");
        var target = Path.Join(targetParent, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(targetParent);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var service = CreateService();
        var originalMode = File.GetUnixFileMode(targetParent);
        var hookRan = false;
        service.BeforeTargetReservationPlanForTest = path =>
        {
            if (hookRan || !string.Equals(path, target, StringComparison.Ordinal))
            {
                return;
            }

            hookRan = true;
            Directory.CreateDirectory(target);
            File.SetUnixFileMode(targetParent, UnixFileMode.None);
        };

        try
        {
            var exception = await Record.ExceptionAsync(() =>
                service.StartAsync(rootId, BuildRelocationCommand(target)));

            // Root can bypass Unix permission checks. The unprivileged Linux
            // validation environment exercises the access-denied race.
            if (!Directory.Exists(target))
            {
                Assert.True(hookRan);
                Assert.NotNull(exception);
                await using var verification = await _factory.CreateDbContextAsync();
                var relocation = Assert.Single(
                    await verification.RootFolderRelocations.ToListAsync());
                Assert.Equal(RootFolderRelocationStatus.NeedsAttention, relocation.Status);
                Assert.Equal(
                    TargetIdentityEnrollmentState.Unavailable,
                    relocation.TargetIdentityEnrollmentState);
                Assert.Empty(await verification
                    .RootFolderRelocationCreatedDirectories
                    .ToListAsync());
                Assert.Equal(source, (await verification.RootFolders.SingleAsync()).Path);
            }
        }
        finally
        {
            File.SetUnixFileMode(targetParent, originalMode);
        }
    }

    [LinuxFact]
    public async Task StartRelocation_EmptyExistingTargetReplacedAfterSave_DoesNotCommitRootMetadata()
    {
        var source = Path.Join(
            TempRoot,
            $"empty-commit-race-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            TempRoot,
            $"empty-commit-race-target-{Guid.NewGuid():N}");
        var displacedTarget = target + ".original";
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive
            };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var service = CreateService();
        service.BeforeEmptyRelocationAtomicCommitForTest = () =>
        {
            Directory.Move(target, displacedTarget);
            Directory.CreateDirectory(target);
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartAsync(
                rootId,
                new RootFolderPathChangeCommand(
                    target,
                    RootFolderRelocationMode.Relocate,
                    true,
                    "Moved Library",
                    false,
                    FileSystemCaseSensitivityMode.Sensitive)));

        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(
            source,
            (await verification.RootFolders
                .SingleAsync(candidate => candidate.Id == rootId)).Path);
        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
        Assert.True(Directory.Exists(displacedTarget));
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public async Task StartRelocation_AnonymousRegistrationPublicationUnderSourceRoot_BlocksBeforeSagaCreation()
    {
        var source = Path.Join(
            TempRoot,
            $"anonymous-registration-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            TempRoot,
            $"anonymous-registration-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Join(source, "Author", "Book"));
        Directory.CreateDirectory(target);
        var publishedPath = Path.Join(source, "Author", "Book", "book.m4b");
        await File.WriteAllTextAsync(publishedPath, "audio");

        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto
            };
            db.RootFolders.Add(root);
            db.FileMutationJournals.Add(new FileMutationJournal
            {
                OperationId = Guid.NewGuid(),
                ProtocolVersion = FileMutationProtocol.Current,
                Action = FileAction.Copy,
                SourcePath = Path.Join(TempRoot, "incoming", "book.m4b"),
                DestinationPath = publishedPath,
                SourceParentDirectoryObjectIdentity = "source-parent",
                DestinationParentDirectoryObjectIdentity = "destination-parent",
                SourcePhysicalObjectIdentity = "source-generation",
                TargetPhysicalObjectIdentity = "target-generation",
                SourceLength = 5,
                State = FileMutationJournalState.TargetVerified,
                AudiobookId = null,
                AudiobookFileId = null
            });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var service = CreateService(
            fileRegistrationRecoveryProbe: new FileRegistrationRecoveryProbe(_factory));

        var exception = await Assert.ThrowsAsync<RootFolderPathChangeRejectedException>(() =>
            service.StartAsync(rootId, BuildRelocationCommand(target)));

        Assert.Equal("registration_recovery_pending", exception.Code);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
        Assert.Empty(await verification.MoveJobs.ToListAsync());
        Assert.Equal(
            source,
            (await verification.RootFolders.SingleAsync(candidate => candidate.Id == rootId)).Path);
        Assert.True(File.Exists(publishedPath));
    }

    [WindowsFact]
    public async Task StartRelocation_DeviceAliasLegacyFilePathWithoutBasePath_DoesNotCompleteAsEmptyRoot()
    {
        var source = Path.Join(
            TempRoot,
            $"device-alias-legacy-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            TempRoot,
            $"device-alias-legacy-target-{Guid.NewGuid():N}");
        var physicalBookPath = Path.Join(source, "Author", "Title");
        Directory.CreateDirectory(physicalBookPath);
        var physicalFilePath = Path.Join(physicalBookPath, "book.m4b");
        await File.WriteAllTextAsync(physicalFilePath, "audio");
        Directory.CreateDirectory(target);
        var deviceAliasFilePath = @"\\?\" + physicalFilePath;

        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(new Audiobook
            {
                Title = "Device alias legacy file",
                BasePath = null,
                FilePath = deviceAliasFilePath
            });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var exception = await Assert.ThrowsAsync<RootFolderPathChangeRejectedException>(() =>
            CreateService().StartAsync(
                rootId,
                new RootFolderPathChangeCommand(
                    target,
                    RootFolderRelocationMode.Relocate,
                    true,
                    "Moved Library",
                    false,
                    FileSystemCaseSensitivityMode.Insensitive)));

        Assert.Equal("root_folder_metadata_repair_required", exception.Code);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(
            source,
            (await verification.RootFolders
                .SingleAsync(candidate => candidate.Id == rootId)).Path);
        var persistedAudiobook = await verification.Audiobooks.SingleAsync();
        Assert.Null(persistedAudiobook.BasePath);
        Assert.Equal(deviceAliasFilePath, persistedAudiobook.FilePath);
        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
        Assert.Equal("audio", await File.ReadAllTextAsync(physicalFilePath));
    }

    [WindowsFact]
    public async Task StartRelocation_DeviceAliasAudiobookBasePathWithoutTrackedFiles_DoesNotCompleteAsEmptyRoot()
    {
        var source = Path.Join(
            TempRoot,
            $"device-alias-untracked-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            TempRoot,
            $"device-alias-untracked-target-{Guid.NewGuid():N}");
        var physicalBookPath = Path.Join(source, "Author", "Title");
        Directory.CreateDirectory(physicalBookPath);
        await File.WriteAllTextAsync(
            Path.Join(physicalBookPath, "book.m4b"),
            "audio");
        Directory.CreateDirectory(target);
        var deviceAliasBookPath = @"\\?\" + physicalBookPath;

        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(new Audiobook
            {
                Title = "Device alias untracked book",
                BasePath = deviceAliasBookPath
            });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var exception = await Assert.ThrowsAsync<RootFolderPathChangeRejectedException>(() =>
            CreateService().StartAsync(
                rootId,
                new RootFolderPathChangeCommand(
                    target,
                    RootFolderRelocationMode.Relocate,
                    true,
                    "Moved Library",
                    false,
                    FileSystemCaseSensitivityMode.Insensitive)));

        Assert.Equal("root_folder_metadata_repair_required", exception.Code);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(
            source,
            (await verification.RootFolders
                .SingleAsync(candidate => candidate.Id == rootId)).Path);
        Assert.Equal(
            deviceAliasBookPath,
            (await verification.Audiobooks.SingleAsync()).BasePath);
        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
        Assert.True(File.Exists(Path.Join(physicalBookPath, "book.m4b")));
    }

    [LinuxFact]
    public async Task StartRelocation_AmbiguousAudiobookBasePathWithoutTrackedFiles_DoesNotCompleteAsEmptyRoot()
    {
        var source = Path.Join(
            TempRoot,
            $"ambiguous-untracked-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            TempRoot,
            $"ambiguous-untracked-target-{Guid.NewGuid():N}");
        var physicalBookPath = Path.Join(source, "Author", "Title");
        Directory.CreateDirectory(physicalBookPath);
        Directory.CreateDirectory(target);
        var ambiguousBookPath = "/" + physicalBookPath;
        Assert.StartsWith("//", ambiguousBookPath, StringComparison.Ordinal);
        Assert.False(FileSystemPathIdentity.TryDetectAbsoluteSyntax(
            ambiguousBookPath,
            out _));

        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(new Audiobook
            {
                Title = "Ambiguous untracked book",
                BasePath = ambiguousBookPath
            });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var exception = await Assert.ThrowsAsync<RootFolderPathChangeRejectedException>(() =>
            CreateService().StartAsync(
                rootId,
                new RootFolderPathChangeCommand(
                    target,
                    RootFolderRelocationMode.Relocate,
                    true,
                    "Moved Library",
                    false,
                    FileSystemCaseSensitivityMode.Sensitive),
                timeout.Token));

        Assert.Equal("root_folder_metadata_repair_required", exception.Code);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(
            source,
            (await verification.RootFolders
                .SingleAsync(candidate => candidate.Id == rootId)).Path);
        Assert.Equal(
            ambiguousBookPath,
            (await verification.Audiobooks.SingleAsync()).BasePath);
        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
        Assert.True(Directory.Exists(physicalBookPath));
    }

    [Fact]
    public async Task StartRelocation_EmptyNestedTarget_RetainsDirectoriesWithoutArtifacts()
    {
        var source = Path.Join(
            TempRoot,
            $"reservation-success-source-{Guid.NewGuid():N}");
        var targetRoot = Path.Join(
            TempRoot,
            $"reservation-success-target-{Guid.NewGuid():N}");
        var target = Path.Join(targetRoot, "level-one", "level-two");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source
            };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var flushOrder = new List<string>();
        var statesObservedAtFlush =
            new List<RootFolderRelocationCreatedDirectoryState>();
        var service = CreateService();
        service.TargetReservationDirectoryFlushedForTest = path =>
        {
            var reservationIndex = flushOrder.Count;
            using var observation = _factory.CreateDbContext();
            statesObservedAtFlush.Add(observation
                .RootFolderRelocationCreatedDirectories
                .OrderBy(candidate => candidate.CanonicalPath.Length)
                .Skip(reservationIndex)
                .Select(candidate => candidate.State)
                .First());
            flushOrder.Add(path);
        };
        var result = await service.StartAsync(
            rootId,
            BuildRelocationCommand(target));

        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
        await using var verification =
            await _factory.CreateDbContextAsync();
        var reservations = await verification
            .RootFolderRelocationCreatedDirectories
            .OrderBy(candidate => candidate.CanonicalPath.Length)
            .ToListAsync();
        Assert.True(reservations.Count >= 3);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(
                reservations.Select(reservation => reservation.CanonicalPath),
                flushOrder);
            Assert.All(statesObservedAtFlush, state =>
                Assert.Equal(
                    RootFolderRelocationCreatedDirectoryState.Planned,
                    state));
        }
        else
        {
            Assert.Empty(flushOrder);
            Assert.Empty(statesObservedAtFlush);
        }
        Assert.All(reservations, reservation =>
        {
            Assert.Equal(
                RootFolderRelocationCreatedDirectoryState.Retained,
                reservation.State);
            Assert.False(string.IsNullOrWhiteSpace(
                reservation.DirectoryObjectIdentity));
            Assert.True(Directory.Exists(reservation.CanonicalPath));
            Assert.DoesNotContain(
                Directory.EnumerateFileSystemEntries(
                    reservation.CanonicalPath,
                    "*",
                    SearchOption.AllDirectories),
                path => Path.GetFileName(path).Contains(
                    ".listenarr-",
                    StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task ReconcileActive_PrecommittedMissingTargetWithoutReservationRows_RebuildsReservationPlan()
    {
        var source = Path.Join(
            TempRoot,
            $"reservation-preplan-recovery-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            TempRoot,
            $"reservation-preplan-recovery-target-{Guid.NewGuid():N}",
            "nested",
            "library");
        Directory.CreateDirectory(source);
        Guid relocationId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source
            };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            var relocation = new RootFolderRelocation
            {
                RootFolderId = root.Id,
                ActiveRootFolderId = root.Id,
                SourcePath = source,
                SourceCaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                TargetPath = target,
                TargetCaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                TargetIdentityEnrollmentState = TargetIdentityEnrollmentState.Unavailable,
                TargetDirectoryObjectIdentityUnavailableReason =
                    "Target reservation planning was interrupted before persistence.",
                Mode = RootFolderRelocationMode.Relocate,
                Status = RootFolderRelocationStatus.NeedsAttention,
                DesiredName = root.Name,
                TotalJobs = 0,
                Error = "Target reservation recovery is pending."
            };
            db.RootFolderRelocations.Add(relocation);
            await db.SaveChangesAsync();
            relocationId = relocation.Id;
        }

        await CreateService().ReconcileActiveAsync();

        await using var verification = await _factory.CreateDbContextAsync();
        var persisted = await verification.RootFolderRelocations
            .Include(candidate => candidate.CreatedDirectories)
            .SingleAsync(candidate => candidate.Id == relocationId);
        Assert.Equal(
            TargetIdentityEnrollmentState.Authorized,
            persisted.TargetIdentityEnrollmentState);
        Assert.False(string.IsNullOrWhiteSpace(
            persisted.TargetDirectoryObjectIdentity));
        Assert.NotEmpty(persisted.CreatedDirectories);
        Assert.All(persisted.CreatedDirectories, reservation =>
            Assert.True(reservation.State is
                RootFolderRelocationCreatedDirectoryState.Created
                    or RootFolderRelocationCreatedDirectoryState.Retained));
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public async Task ReconcileActive_RetainedReservationsRemainArtifactFreeAndIdempotent()
    {
        var source = Path.Join(
            TempRoot,
            $"reservation-markerless-recovery-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            TempRoot,
            $"reservation-markerless-recovery-target-{Guid.NewGuid():N}",
            "nested");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source
            };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var result = await CreateService().StartAsync(
            rootId,
            BuildRelocationCommand(target));
        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);

        await CreateService().ReconcileActiveAsync();
        await CreateService().ReconcileActiveAsync();

        await using var completed =
            await _factory.CreateDbContextAsync();
        var completedReservations = await completed
            .RootFolderRelocationCreatedDirectories
            .ToListAsync();
        Assert.NotEmpty(completedReservations);
        Assert.All(completedReservations, reservation =>
        {
            Assert.Equal(
                RootFolderRelocationCreatedDirectoryState.Retained,
                reservation.State);
            Assert.True(Directory.Exists(reservation.CanonicalPath));
        });
    }

    [WindowsFact]
    public async Task ReconcileActive_ForeignPersistedReservationPath_DoesNotTouchWindowsAlias()
    {
        var source = Path.Join(
            TempRoot,
            $"reservation-foreign-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            TempRoot,
            $"reservation-foreign-target-{Guid.NewGuid():N}",
            "nested");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source
            };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var result = await CreateService().StartAsync(
            rootId,
            BuildRelocationCommand(target));
        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);

        List<string> nativeSentinelPaths;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var reservations = await db.RootFolderRelocationCreatedDirectories
                .ToListAsync();
            nativeSentinelPaths = reservations
                .Select(reservation => Path.Join(
                    reservation.CanonicalPath,
                    "user-content.txt"))
                .ToList();
            foreach (var sentinel in nativeSentinelPaths)
            {
                await File.WriteAllTextAsync(sentinel, "preserve");
            }

            foreach (var reservation in reservations)
            {
                reservation.CanonicalPath = "/" + Path.GetRelativePath(
                        Path.GetPathRoot(reservation.CanonicalPath)!,
                        reservation.CanonicalPath)
                    .Replace('\\', '/');
            }
            await db.SaveChangesAsync();
        }

        await CreateService().ReconcileActiveAsync();

        Assert.All(nativeSentinelPaths, path => Assert.True(
            File.Exists(path),
            $"Foreign persisted reservation path touched Windows alias: {path}"));
    }

    [LinuxFact]
    public async Task ReconcileActive_AmbiguousPersistedReservationPath_PreservesUserContent()
    {
        var source = Path.Join(
            TempRoot,
            $"reservation-ambiguous-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            TempRoot,
            $"reservation-ambiguous-target-{Guid.NewGuid():N}",
            "nested");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source
            };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var result = await CreateService().StartAsync(
            rootId,
            BuildRelocationCommand(target));
        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);

        List<string> nativeSentinelPaths;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var reservations = await db.RootFolderRelocationCreatedDirectories
                .ToListAsync();
            nativeSentinelPaths = reservations
                .Select(reservation => Path.Join(
                    reservation.CanonicalPath,
                    "user-content.txt"))
                .ToList();
            foreach (var sentinel in nativeSentinelPaths)
            {
                await File.WriteAllTextAsync(sentinel, "preserve");
            }

            foreach (var reservation in reservations)
            {
                var ambiguousPath = "/" + reservation.CanonicalPath;
                Assert.False(FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                    ambiguousPath,
                    out _));
                reservation.CanonicalPath = ambiguousPath;
            }
            await db.SaveChangesAsync();
        }

        await CreateService().ReconcileActiveAsync();

        Assert.All(nativeSentinelPaths, path => Assert.True(
            File.Exists(path),
            $"Ambiguous persisted reservation path touched user content: {path}"));
    }

    [Fact]
    public async Task ReconcileActive_FailedNestedTarget_CleansOnlyProvablyCreatedDirectories()
    {
        var source = Path.Join(
            TempRoot,
            $"reservation-failed-source-{Guid.NewGuid():N}");
        var bookPath = Path.Join(source, "Author", "Book");
        var targetRoot = Path.Join(
            TempRoot,
            $"reservation-failed-target-{Guid.NewGuid():N}");
        var target = Path.Join(targetRoot, "level-one", "level-two");
        Directory.CreateDirectory(bookPath);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source
            };
            var audiobook = new Audiobook
            {
                Title = "Book",
                BasePath = bookPath
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            await AddTrackedFileAsync(
                db,
                audiobook,
                Path.Join(bookPath, "book.m4b"),
                source);
            rootId = root.Id;
        }

        var service = CreateService();
        service.AfterTargetReservationStatePersistedForTest = path =>
        {
            if (string.Equals(
                    path,
                    target,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                throw new IOException(
                    "Injected crash after the final reserved directory state was persisted.");
            }
        };
        await Assert.ThrowsAsync<IOException>(() =>
            service.StartAsync(
                rootId,
                BuildRelocationCommand(target)));
        Guid relocationId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var relocation = await db.RootFolderRelocations.SingleAsync();
            relocationId = relocation.Id;
            relocation.Status = RootFolderRelocationStatus.Failed;
            relocation.ActiveRootFolderId = null;
            relocation.TargetIdentityEnrollmentState =
                TargetIdentityEnrollmentState.NotRequired;
            await db.SaveChangesAsync();
        }

        string? retainedSentinel = null;
        if (!OperatingSystem.IsWindows())
        {
            retainedSentinel = Path.Join(target, "user-content.txt");
            await File.WriteAllTextAsync(retainedSentinel, "preserve");
        }

        await CreateService().ReconcileActiveAsync();

        await using var verification =
            await _factory.CreateDbContextAsync();
        var persistedStates = await verification
            .RootFolderRelocationCreatedDirectories
            .Where(candidate => candidate.RelocationId == relocationId)
            .OrderBy(candidate => candidate.CanonicalPath.Length)
            .Select(candidate => new
            {
                candidate.CanonicalPath,
                candidate.State
            })
            .ToListAsync();
        var remainingEntries = Directory.Exists(targetRoot)
            ? Directory.EnumerateFileSystemEntries(
                    targetRoot,
                    "*",
                    SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .ToList()
            : [];
        Assert.True(Directory.Exists(source));
        var reservations = await verification
            .RootFolderRelocationCreatedDirectories
            .Where(candidate =>
                candidate.RelocationId == relocationId)
            .ToListAsync();
        Assert.NotEmpty(reservations);
        if (OperatingSystem.IsWindows())
        {
            Assert.False(
                Directory.Exists(targetRoot),
                $"Remaining entries: {string.Join(", ", remainingEntries)}; states: {string.Join(", ", persistedStates.Select(item => $"{item.CanonicalPath}={item.State}"))}");
            Assert.All(reservations, reservation =>
                Assert.Equal(
                    RootFolderRelocationCreatedDirectoryState.Removed,
                    reservation.State));
        }
        else
        {
            Assert.True(Directory.Exists(targetRoot));
            Assert.NotNull(retainedSentinel);
            Assert.True(File.Exists(retainedSentinel));
            Assert.Equal("preserve", await File.ReadAllTextAsync(retainedSentinel));
            Assert.All(reservations, reservation =>
            {
                Assert.Equal(
                    RootFolderRelocationCreatedDirectoryState.Retained,
                    reservation.State);
                Assert.True(Directory.Exists(reservation.CanonicalPath));
            });
        }
    }

    [Fact]
    public async Task StartRelocation_PostCommitReservationFailure_PersistsNeedsAttention()
    {
        var source = Path.Join(
            TempRoot,
            $"reservation-attention-source-{Guid.NewGuid():N}");
        var targetParent = Path.Join(
            TempRoot,
            $"reservation-attention-parent-{Guid.NewGuid():N}");
        var target = Path.Join(targetParent, "child");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(targetParent);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source
            };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var service = CreateService();
        var injected = false;
        service.BeforeTargetReservationPlanForTest = path =>
        {
            if (injected || !string.Equals(path, target, StringComparison.Ordinal))
            {
                return;
            }

            injected = true;
            Directory.Delete(targetParent);
            File.WriteAllText(targetParent, "occupied");
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartAsync(
                rootId,
                BuildRelocationCommand(target)));

        Assert.True(injected);

        await using var verification =
            await _factory.CreateDbContextAsync();
        var relocation = await verification.RootFolderRelocations
            .SingleAsync();
        Assert.Equal(
            RootFolderRelocationStatus.NeedsAttention,
            relocation.Status);
        Assert.Equal(rootId, relocation.ActiveRootFolderId);
        Assert.Equal(
            TargetIdentityEnrollmentState.Unavailable,
            relocation.TargetIdentityEnrollmentState);
        Assert.Contains(
            "target reservation",
            relocation.Error,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReconcileActive_PlannedPublishedReservation_AuthorizesAndRetries()
    {
        var source = Path.Join(
            TempRoot,
            $"reservation-crash-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            TempRoot,
            $"reservation-crash-target-{Guid.NewGuid():N}",
            "nested");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source
            };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var interrupted = CreateService();
        if (OperatingSystem.IsWindows())
        {
            var flushCount = 0;
            interrupted.TargetReservationDirectoryFlushedForTest = _ =>
            {
                flushCount++;
                if (flushCount == 2)
                {
                    throw new IOException(
                        "Injected crash after the Windows directory durability barrier.");
                }
            };
            await Assert.ThrowsAsync<IOException>(() =>
                interrupted.StartAsync(
                    rootId,
                    BuildRelocationCommand(target)));
            Assert.Equal(2, flushCount);
        }
        else
        {
            var createCount = 0;
            using var hook =
                PinnedFilesystemMutationHooks.PushAfterUnixDirectoryCreateBeforeOpen(_ =>
                {
                    createCount++;
                    if (createCount == 2)
                    {
                        throw new IOException(
                            "Injected crash after Unix final-name creation before reopen.");
                    }
                });
            await Assert.ThrowsAsync<IOException>(() =>
                interrupted.StartAsync(
                    rootId,
                    BuildRelocationCommand(target)));
            Assert.Equal(2, createCount);
        }

        Guid relocationId;
        await using (var verification =
            await _factory.CreateDbContextAsync())
        {
            var relocation = await verification.RootFolderRelocations
                .SingleAsync();
            relocationId = relocation.Id;
            Assert.Equal(
                RootFolderRelocationStatus.NeedsAttention,
                relocation.Status);
            Assert.Equal(
                TargetIdentityEnrollmentState.Unavailable,
                relocation.TargetIdentityEnrollmentState);
            Assert.Contains(
                await verification
                    .RootFolderRelocationCreatedDirectories
                    .Select(candidate => candidate.State)
                    .ToListAsync(),
                state => state ==
                    RootFolderRelocationCreatedDirectoryState.Planned);
        }

        var restarted = CreateService();
        await restarted.ReconcileActiveAsync();
        await using (var recovered =
            await _factory.CreateDbContextAsync())
        {
            var relocation = await recovered.RootFolderRelocations
                .SingleAsync();
            Assert.Equal(
                TargetIdentityEnrollmentState.Authorized,
                relocation.TargetIdentityEnrollmentState);
            var recoveredReservations = await recovered
                .RootFolderRelocationCreatedDirectories
                .ToListAsync();
            if (OperatingSystem.IsWindows())
            {
                Assert.Single(
                    recoveredReservations,
                    reservation => reservation.State ==
                        RootFolderRelocationCreatedDirectoryState.Retained);
                Assert.All(
                    recoveredReservations,
                    reservation => Assert.Contains(
                        reservation.State,
                        new[]
                        {
                            RootFolderRelocationCreatedDirectoryState.Created,
                            RootFolderRelocationCreatedDirectoryState.Retained
                        }));
            }
            else
            {
                Assert.All(
                    recoveredReservations,
                    reservation => Assert.Equal(
                        RootFolderRelocationCreatedDirectoryState.Retained,
                        reservation.State));
            }
        }

        var result = await restarted.RetryAsync(relocationId);

        Assert.Equal(
            RootFolderRelocationStatus.Completed,
            result.Status);
        await using var completed =
            await _factory.CreateDbContextAsync();
        Assert.All(
            await completed.RootFolderRelocationCreatedDirectories
                .ToListAsync(),
            reservation => Assert.Equal(
                RootFolderRelocationCreatedDirectoryState.Retained,
                reservation.State));
    }

    [Fact]
    public async Task ReconcileActive_ParentIntentPublishedBeforeChildCreation_ResumesSameReservation()
    {
        var source = Path.Join(
            TempRoot,
            $"reservation-parent-intent-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            TempRoot,
            $"reservation-parent-intent-target-{Guid.NewGuid():N}",
            "nested");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source
            };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var injected = false;
        var interrupted = CreateService();
        interrupted.AfterReservationParentIntentPersistedForTest = _ =>
        {
            if (!injected)
            {
                injected = true;
                throw new IOException(
                    "Injected crash after durable parent reservation intent.");
            }
        };

        await Assert.ThrowsAsync<IOException>(() =>
            interrupted.StartAsync(
                rootId,
                BuildRelocationCommand(target)));

        Guid relocationId;
        await using (var verification =
            await _factory.CreateDbContextAsync())
        {
            var relocation = await verification.RootFolderRelocations
                .SingleAsync();
            relocationId = relocation.Id;
            Assert.Equal(
                RootFolderRelocationStatus.NeedsAttention,
                relocation.Status);
            var plannedReservation = await verification
                .RootFolderRelocationCreatedDirectories
                .SingleAsync(candidate => candidate.State ==
                    RootFolderRelocationCreatedDirectoryState.Planned);
            Assert.False(Directory.Exists(plannedReservation.CanonicalPath));
            Assert.Equal(
                ManagedDirectoryIdentity.CurrentVersion,
                plannedReservation.DirectoryObjectIdentityVersion);
            Assert.False(string.IsNullOrWhiteSpace(
                plannedReservation.DirectoryObjectIdentity));
        }

        var restarted = CreateService();
        await restarted.ReconcileActiveAsync();

        await using (var recovered =
            await _factory.CreateDbContextAsync())
        {
            var relocation = await recovered.RootFolderRelocations
                .SingleAsync(candidate => candidate.Id == relocationId);
            Assert.Equal(
                TargetIdentityEnrollmentState.Authorized,
                relocation.TargetIdentityEnrollmentState);
            var recoveredReservations = await recovered
                .RootFolderRelocationCreatedDirectories
                .Where(candidate => candidate.RelocationId == relocationId)
                .ToListAsync();
            var expectedState = OperatingSystem.IsWindows()
                ? RootFolderRelocationCreatedDirectoryState.Created
                : RootFolderRelocationCreatedDirectoryState.Retained;
            Assert.All(
                recoveredReservations,
                reservation => Assert.Equal(
                    expectedState,
                    reservation.State));
        }
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(
                Path.GetDirectoryName(target)!,
                "*",
                SearchOption.AllDirectories),
            path => Path.GetFileName(path).Contains(
                ".listenarr-",
                StringComparison.Ordinal));

        var result = await restarted.RetryAsync(relocationId);
        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
    }

    [Fact]
    public async Task ReconcileActive_PublishedChildWithoutParentIntent_RemainsUntrusted()
    {
        var source = Path.Join(
            TempRoot,
            $"reservation-missing-intent-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            TempRoot,
            $"reservation-missing-intent-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source
            };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var interrupted = CreateService();
        if (OperatingSystem.IsWindows())
        {
            interrupted.TargetReservationDirectoryFlushedForTest = path =>
            {
                if (string.Equals(
                        path,
                        target,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException(
                        "Injected crash after Windows child publication before enrollment.");
                }
            };
            await Assert.ThrowsAsync<IOException>(() =>
                interrupted.StartAsync(
                    rootId,
                    BuildRelocationCommand(target)));
        }
        else
        {
            using var hook =
                PinnedFilesystemMutationHooks.PushAfterUnixDirectoryCreateBeforeOpen(path =>
                {
                    if (string.Equals(path, target, StringComparison.Ordinal))
                    {
                        throw new IOException(
                            "Injected crash after Unix final-name creation before reopen.");
                    }
                });
            await Assert.ThrowsAsync<IOException>(() =>
                interrupted.StartAsync(
                    rootId,
                    BuildRelocationCommand(target)));
        }

        Guid relocationId;
        RootFolderRelocationCreatedDirectory reservation;
        await using (var verification =
            await _factory.CreateDbContextAsync())
        {
            var relocation = await verification.RootFolderRelocations
                .SingleAsync();
            relocationId = relocation.Id;
            reservation = await verification
                .RootFolderRelocationCreatedDirectories
                .SingleAsync();
            Assert.Equal(
                RootFolderRelocationCreatedDirectoryState.Planned,
                reservation.State);
            reservation.DirectoryObjectIdentityVersion = null;
            reservation.DirectoryObjectIdentity = null;
            await verification.SaveChangesAsync();
        }

        await CreateService().ReconcileActiveAsync();

        await using var blocked = await _factory.CreateDbContextAsync();
        var blockedRelocation = await blocked.RootFolderRelocations
            .SingleAsync(candidate => candidate.Id == relocationId);
        var blockedReservation = await blocked
            .RootFolderRelocationCreatedDirectories
            .SingleAsync(candidate => candidate.RelocationId == relocationId);
        Assert.Equal(
            RootFolderRelocationStatus.NeedsAttention,
            blockedRelocation.Status);
        Assert.Equal(
            TargetIdentityEnrollmentState.Unavailable,
            blockedRelocation.TargetIdentityEnrollmentState);
        Assert.Equal(
            RootFolderRelocationCreatedDirectoryState.Planned,
            blockedReservation.State);
        Assert.True(Directory.Exists(target));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [Fact]
    public async Task ReconcileActive_FailedReservationWithMissingParent_RetainsAndContinues()
    {
        var source = Path.Join(
            TempRoot,
            $"reservation-missing-parent-source-{Guid.NewGuid():N}");
        var missingParent = Path.Join(
            TempRoot,
            $"reservation-missing-parent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source
            };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            var relocation = new RootFolderRelocation
            {
                RootFolderId = root.Id,
                SourcePath = source,
                TargetPath = Path.Join(missingParent, "child"),
                Mode = RootFolderRelocationMode.Relocate,
                Status = RootFolderRelocationStatus.Failed,
                DesiredName = root.Name,
                TargetIdentityEnrollmentState =
                    TargetIdentityEnrollmentState.NotRequired
            };
            relocation.CreatedDirectories.Add(
                new RootFolderRelocationCreatedDirectory
                {
                    CanonicalPath = relocation.TargetPath,
                    OwnershipToken =
                        Guid.NewGuid().ToString("N"),
                    State =
                        RootFolderRelocationCreatedDirectoryState.Created,
                    DirectoryObjectIdentityVersion = 1,
                    DirectoryObjectIdentity = "missing"
                });
            db.RootFolderRelocations.Add(relocation);
            await db.SaveChangesAsync();
        }

        await CreateService().ReconcileActiveAsync();

        await using var verification =
            await _factory.CreateDbContextAsync();
        var persisted = await verification.RootFolderRelocations
            .Include(candidate => candidate.CreatedDirectories)
            .SingleAsync();
        Assert.Equal(
            RootFolderRelocationCreatedDirectoryState.Retained,
            Assert.Single(persisted.CreatedDirectories).State);
        Assert.Contains(
            "retained for safety",
            persisted.Error,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartRelocation_BroadBasePath_UsesTrackedFileSourceRoot()
    {
        var source = Path.Join(Path.GetTempPath(), $"relocation-broad-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"relocation-broad-target-{Guid.NewGuid():N}");
        var authorPath = Path.Join(source, "Shared Author");
        var bookPath = Path.Join(authorPath, "Book One");
        var siblingPath = Path.Join(authorPath, "Book Two");
        Directory.CreateDirectory(bookPath);
        Directory.CreateDirectory(siblingPath);
        await File.WriteAllTextAsync(Path.Join(siblingPath, "Book Two.m4b"), "foreign audio");
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            var audiobook = new Audiobook
            {
                Title = "Book One",
                BasePath = authorPath
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            await AddTrackedFileAsync(
                db,
                audiobook,
                Path.Join(bookPath, "Book One.m4b"),
                source);
            rootId = root.Id;
        }

        var manifestScopes = CreateMoveSourceManifestService();
        await CreateService(manifestScopes).StartAsync(
            rootId,
            BuildRelocationCommand(target));

        await using var verification = await _factory.CreateDbContextAsync();
        var job = await verification.MoveJobs
            .Include(candidate => candidate.Entries)
            .SingleAsync();
        Assert.Equal(bookPath, job.SourcePath);
        Assert.Equal(Path.Join(target, "Shared Author", "Book One"), job.RequestedPath);
        Assert.Equal(source, job.SourceCleanupBoundary);
        Assert.Single(job.Entries, MoveManifestIdentity.IsSourceBoundaryAuthorization);
        Assert.Single(job.Entries, MoveManifestIdentity.IsTargetBoundaryAuthorization);
        var entry = Assert.Single(
            job.Entries,
            candidate => !MoveManifestIdentity.IsBoundaryAuthorization(candidate));
        Assert.Equal("Book One.m4b", entry.RelativePath);
        Assert.Equal(authorPath, (await verification.Audiobooks.SingleAsync()).BasePath);
    }

    [Fact]
    public async Task StartRelocation_SharedFlatFolder_PublishesDisjointManifestJobs()
    {
        var source = Path.Join(Path.GetTempPath(), $"relocation-flat-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"relocation-flat-target-{Guid.NewGuid():N}");
        var sharedPath = Path.Join(source, "Shared");
        Directory.CreateDirectory(sharedPath);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            var first = new Audiobook { Title = "First", BasePath = sharedPath };
            var second = new Audiobook { Title = "Second", BasePath = sharedPath };
            db.RootFolders.Add(root);
            db.Audiobooks.AddRange(first, second);
            await db.SaveChangesAsync();
            await AddTrackedFileAsync(
                db,
                first,
                Path.Join(sharedPath, "First.m4b"),
                source);
            await AddTrackedFileAsync(
                db,
                second,
                Path.Join(sharedPath, "Second.m4b"),
                source);
            rootId = root.Id;
        }

        var manifestScopes = CreateMoveSourceManifestService();
        await CreateService(manifestScopes).StartAsync(
            rootId,
            BuildRelocationCommand(target));

        await using var verification = await _factory.CreateDbContextAsync();
        var jobs = await verification.MoveJobs
            .Include(candidate => candidate.Entries)
            .OrderBy(candidate => candidate.AudiobookId)
            .ToListAsync();
        Assert.Equal(2, jobs.Count);
        Assert.All(jobs, job =>
        {
            Assert.Equal(sharedPath, job.SourcePath);
            Assert.Equal(Path.Join(target, "Shared"), job.RequestedPath);
            Assert.Equal(MoveManifestIdentity.Version, job.IdentityKeyVersion);
            Assert.Single(job.Entries, MoveManifestIdentity.IsSourceBoundaryAuthorization);
            Assert.Single(job.Entries, MoveManifestIdentity.IsTargetBoundaryAuthorization);
            Assert.Single(
                job.Entries,
                entry => !MoveManifestIdentity.IsBoundaryAuthorization(entry));
        });
        Assert.Equal(
            new[] { "First.m4b", "Second.m4b" },
            jobs.Select(job => job.Entries.Single(entry =>
                    !MoveManifestIdentity.IsBoundaryAuthorization(entry)).RelativePath)
                .OrderBy(path => path));
        Assert.NotEqual(jobs[0].ActiveDeduplicationKey, jobs[1].ActiveDeduplicationKey);
        Assert.Equal(1, manifestScopes.CreatedScopeCount);
        Assert.Equal(1, manifestScopes.DisposedScopeCount);
        Assert.Equal(2, manifestScopes.BuildCount);
    }

    [Fact]
    public async Task SharedFlatFolder_CompletedJobs_FinalizeRelocation()
    {
        var source = Path.Join(Path.GetTempPath(), $"relocation-flat-finalize-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"relocation-flat-finalize-target-{Guid.NewGuid():N}");
        var sharedPath = Path.Join(source, "Shared");
        Directory.CreateDirectory(sharedPath);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            var first = new Audiobook { Title = "First", BasePath = sharedPath };
            var second = new Audiobook { Title = "Second", BasePath = sharedPath };
            db.RootFolders.Add(root);
            db.Audiobooks.AddRange(first, second);
            await db.SaveChangesAsync();
            await AddTrackedFileAsync(
                db,
                first,
                Path.Join(sharedPath, "First.m4b"),
                source);
            await AddTrackedFileAsync(
                db,
                second,
                Path.Join(sharedPath, "Second.m4b"),
                source);
            rootId = root.Id;
        }

        var service = CreateService();
        await service.StartAsync(rootId, BuildRelocationCommand(target));
        Guid completedJobId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var jobs = await db.MoveJobs.ToListAsync();
            Assert.Equal(2, jobs.Count);
            var audiobookIds = jobs.Select(job => job.AudiobookId).ToList();
            var audiobooks = await db.Audiobooks
                .Where(audiobook => audiobookIds.Contains(audiobook.Id))
                .ToDictionaryAsync(audiobook => audiobook.Id);
            foreach (var job in jobs)
            {
                job.Status = MoveJobStatus.Completed;
                job.ActiveDeduplicationKey = null;
                audiobooks[job.AudiobookId].BasePath = job.RequestedPath;
            }

            completedJobId = jobs[0].Id;
            await db.SaveChangesAsync();
        }

        await service.OnMoveJobStateChangedAsync(completedJobId);

        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(target, (await verification.RootFolders.SingleAsync()).Path);
        Assert.All(
            await verification.Audiobooks.ToListAsync(),
            audiobook => Assert.Equal(Path.Join(target, "Shared"), audiobook.BasePath));
        var relocation = await verification.RootFolderRelocations.SingleAsync();
        Assert.Equal(RootFolderRelocationStatus.Completed, relocation.Status);
        Assert.Null(relocation.ActiveRootFolderId);
    }

    [Fact]
    public async Task CompletedRelocation_TargetSemanticsChangedBeforeFinalization_NeedsAttention()
    {
        var source = Path.Join(
            Path.GetTempPath(),
            $"relocation-finalize-semantics-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            Path.GetTempPath(),
            $"relocation-finalize-semantics-target-{Guid.NewGuid():N}");
        var bookPath = Path.Join(source, "Book");
        Directory.CreateDirectory(bookPath);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            var audiobook = new Audiobook { Title = "Book", BasePath = bookPath };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            await AddTrackedFileAsync(
                db,
                audiobook,
                Path.Join(bookPath, "book.m4b"),
                source);
            rootId = root.Id;
        }

        var semanticsResolver = new SwitchableTargetSemanticsResolver(target);
        var service = CreateService(semanticsResolver: semanticsResolver);
        await service.StartAsync(rootId, BuildRelocationCommand(target));
        Guid completedJobId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var job = await db.MoveJobs.SingleAsync();
            var audiobook = await db.Audiobooks.SingleAsync();
            job.Status = MoveJobStatus.Completed;
            job.ActiveDeduplicationKey = null;
            audiobook.BasePath = job.RequestedPath;
            completedJobId = job.Id;
            await db.SaveChangesAsync();
        }
        semanticsResolver.ReportOppositeTargetSemantics = true;

        await service.OnMoveJobStateChangedAsync(completedJobId);

        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(source, (await verification.RootFolders.SingleAsync()).Path);
        var relocation = await verification.RootFolderRelocations.SingleAsync();
        Assert.Equal(RootFolderRelocationStatus.NeedsAttention, relocation.Status);
        Assert.NotNull(relocation.ActiveRootFolderId);
        Assert.Contains(
            "semantics changed",
            relocation.Error ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartRelocation_WithoutTrackedFiles_RejectsBeforeSagaPublication()
    {
        var source = Path.Join(Path.GetTempPath(), $"relocation-untracked-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"relocation-untracked-target-{Guid.NewGuid():N}");
        var bookPath = Path.Join(source, "Book");
        Directory.CreateDirectory(bookPath);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(new Audiobook { Title = "Book", BasePath = bookPath });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var manifestScopes = CreateMoveSourceManifestService();
        var service = CreateService(manifestScopes);
        var exception = await Assert.ThrowsAsync<
            Listenarr.Application.Common.Exceptions.ApplicationConflictException>(() =>
            service.StartAsync(rootId, BuildRelocationCommand(target)));
        await Assert.ThrowsAsync<
            Listenarr.Application.Common.Exceptions.ApplicationConflictException>(() =>
            service.StartAsync(rootId, BuildRelocationCommand(target)));

        Assert.Equal("move_source_unverified", exception.Code);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
        Assert.Empty(await verification.MoveJobs.ToListAsync());
        Assert.Equal(2, manifestScopes.CreatedScopeCount);
        Assert.Equal(2, manifestScopes.DisposedScopeCount);
        Assert.Equal(2, manifestScopes.ResolvedServices.Distinct().Count());
    }

    [Fact]
    public async Task StartRelocation_CancellationDuringManifestBuild_DisposesOperationScope()
    {
        var (rootId, _, _, target) = await SeedRelocationScenarioAsync();
        using var cancellation = new CancellationTokenSource();
        var manifestService = new Mock<IMoveSourceManifestService>(MockBehavior.Strict);
        manifestService.Setup(service => service.BuildAsync(
                It.IsAny<Audiobook>(),
                It.IsAny<CancellationToken>()))
            .Returns<Audiobook, CancellationToken>((_, _) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<MoveSourceManifest>(cancellation.Token);
            });
        var manifestScopes = new ManifestServiceScopeFactory(
            () => manifestService.Object);
        var service = CreateService(manifestScopes);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.StartAsync(
                rootId,
                BuildRelocationCommand(target),
                cancellation.Token));

        Assert.Equal(1, manifestScopes.CreatedScopeCount);
        Assert.Equal(1, manifestScopes.DisposedScopeCount);
    }

    [Fact]
    public async Task StartRelocation_IdenticalSourceAndTarget_RejectsBeforePersistingChildJobs()
    {
        var source = Path.Join(Path.GetTempPath(), $"relocation-identical-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Join(source, "Author", "Title"));
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(new Audiobook
            {
                Title = "Title",
                BasePath = Path.Join(source, "Author", "Title")
            });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            CreateService().StartAsync(
                rootId,
                new RootFolderPathChangeCommand(
                    source,
                    RootFolderRelocationMode.Relocate,
                    true,
                    "Library",
                    false,
                    FileSystemCaseSensitivityMode.Auto)));

        Assert.Contains("distinct", exception.Message, StringComparison.OrdinalIgnoreCase);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
        Assert.Empty(await verification.MoveJobs.ToListAsync());
    }

    [Fact]
    public async Task StartAsync_NullCommand_ThrowsArgumentNullBeforeReadinessInspection()
    {
        var readiness = new TestLibraryFilesystemReadiness();
        readiness.SetFailed("Injected startup recovery failure.");
        var service = CreateService(filesystemReadiness: readiness);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.StartAsync(1, null!));
    }

    [Fact]
    public async Task PhysicalPathChange_StartupRecoveryFailed_BlocksBeforePersistence()
    {
        var (rootId, _, _, target) = await SeedRelocationScenarioAsync();
        var readiness = new TestLibraryFilesystemReadiness();
        readiness.SetFailed("Injected startup recovery failure.");
        var service = CreateService(filesystemReadiness: readiness);

        var exception = await Assert.ThrowsAsync<ApplicationUnavailableException>(() =>
            service.StartAsync(rootId, BuildRelocationCommand(target)));

        Assert.Equal("filesystem_initialization_failed", exception.Code);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
        Assert.Empty(await verification.MoveJobs.ToListAsync());
    }

    [Fact]
    public async Task PhysicalRecovery_StartupRecoveryFailed_BlocksRetryAndAbandonBeforeMutation()
    {
        var source = Path.Join(
            Path.GetTempPath(),
            $"physical-recovery-readiness-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            Path.GetTempPath(),
            $"physical-recovery-readiness-target-{Guid.NewGuid():N}");
        Guid relocationId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            var relocation = new RootFolderRelocation
            {
                RootFolderId = root.Id,
                ActiveRootFolderId = root.Id,
                SourcePath = source,
                SourceCaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
                TargetPath = target,
                TargetCaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
                TargetIdentityEnrollmentState = TargetIdentityEnrollmentState.Unavailable,
                Mode = RootFolderRelocationMode.Relocate,
                Status = RootFolderRelocationStatus.NeedsAttention,
                DesiredName = root.Name,
                TotalJobs = 1,
                Error = "Target reservation recovery is pending."
            };
            db.RootFolderRelocations.Add(relocation);
            await db.SaveChangesAsync();
            relocationId = relocation.Id;
        }
        var readiness = new TestLibraryFilesystemReadiness();
        readiness.SetFailed("Injected startup recovery failure.");
        var service = CreateService(filesystemReadiness: readiness);

        var retryException = await Assert.ThrowsAsync<ApplicationUnavailableException>(() =>
            service.RetryAsync(relocationId));
        var abandonException = await Assert.ThrowsAsync<ApplicationUnavailableException>(() =>
            service.AbandonUnpublishedAsync(relocationId));

        Assert.Equal("filesystem_initialization_failed", retryException.Code);
        Assert.Equal("filesystem_initialization_failed", abandonException.Code);
        await using var verification = await _factory.CreateDbContextAsync();
        var relocationAfter = await verification.RootFolderRelocations.SingleAsync();
        Assert.Equal(RootFolderRelocationStatus.NeedsAttention, relocationAfter.Status);
        Assert.Equal(0, relocationAfter.CompletedJobs);
    }

    [Fact]
    public async Task MetadataOnlyPathChange_StartupReconciliationRunning_BlocksBeforePersistence()
    {
        var source = Path.Join(
            Path.GetTempPath(),
            $"metadata-readiness-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            Path.GetTempPath(),
            $"metadata-readiness-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }
        var readiness = new TestLibraryFilesystemReadiness();
        readiness.SetRunning("AudiobookFileIdentities");
        var service = CreateService(filesystemReadiness: readiness);

        var exception = await Assert.ThrowsAsync<ApplicationUnavailableException>(() =>
            service.StartAsync(
                rootId,
                new RootFolderPathChangeCommand(
                    target,
                    RootFolderRelocationMode.MetadataOnly,
                    false,
                    "Library",
                    false,
                    FileSystemCaseSensitivityMode.Auto)));

        Assert.Equal("metadata_repair_initializing", exception.Code);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(source, (await verification.RootFolders.SingleAsync()).Path);
        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
    }

    [Fact]
    public async Task MetadataOnlyRetry_StartupRecoveryFailed_BlocksBeforeRelocationMutation()
    {
        var source = Path.Join(
            Path.GetTempPath(),
            $"metadata-retry-readiness-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            Path.GetTempPath(),
            $"metadata-retry-readiness-target-{Guid.NewGuid():N}");
        Guid relocationId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = target };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            var relocation = new RootFolderRelocation
            {
                RootFolderId = root.Id,
                ActiveRootFolderId = root.Id,
                SourcePath = source,
                SourceCaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
                TargetPath = target,
                TargetCaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive,
                Mode = RootFolderRelocationMode.MetadataOnly,
                Status = RootFolderRelocationStatus.NeedsAttention,
                DesiredName = "Library",
                TotalJobs = 1,
                CompletedJobs = 0
            };
            db.RootFolderRelocations.Add(relocation);
            await db.SaveChangesAsync();
            relocationId = relocation.Id;
        }
        var readiness = new TestLibraryFilesystemReadiness();
        readiness.SetFailed("Injected file-identity recovery failure.");
        var service = CreateService(filesystemReadiness: readiness);

        var exception = await Assert.ThrowsAsync<ApplicationUnavailableException>(() =>
            service.RetryAsync(relocationId));

        Assert.Equal("metadata_repair_initialization_failed", exception.Code);
        await using var verification = await _factory.CreateDbContextAsync();
        var relocationAfter = await verification.RootFolderRelocations.SingleAsync();
        Assert.Equal(RootFolderRelocationStatus.NeedsAttention, relocationAfter.Status);
        Assert.Equal(0, relocationAfter.CompletedJobs);
    }

    [Fact]
    public async Task MetadataOnlyRetry_FailedCompletionWithoutOwnershipJournal_ActiveDeletionBlocksRediscoveredAudiobookMutation()
    {
        var source = Path.Join(
            Path.GetTempPath(),
            $"metadata-retry-owner-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            Path.GetTempPath(),
            $"metadata-retry-owner-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(target);
        Guid relocationId;
        int audiobookId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = target,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
                ResolvedCaseSensitivity = FileSystemCaseSensitivity.Sensitive
            };
            var audiobook = new Audiobook
            {
                Title = "Owned During Recovery",
                BasePath = Path.Join(source, "Author", "Book")
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            audiobookId = audiobook.Id;

            var relocation = new RootFolderRelocation
            {
                RootFolderId = root.Id,
                ActiveRootFolderId = root.Id,
                SourcePath = source,
                SourceCaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
                TargetPath = target,
                TargetCaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
                Mode = RootFolderRelocationMode.MetadataOnly,
                Status = RootFolderRelocationStatus.Failed,
                DesiredName = root.Name,
                TotalJobs = 1,
                CompletedJobs = 0,
                Error = "Injected metadata completion failure."
            };
            db.RootFolderRelocations.Add(relocation);
            db.AudiobookDeletionIntents.Add(new AudiobookDeletionIntent
            {
                AudiobookId = audiobook.Id,
                DeleteFolder = false,
                State = AudiobookDeletionIntentState.Planned
            });
            await db.SaveChangesAsync();
            relocationId = relocation.Id;
        }

        var exception = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
            CreateService().RetryAsync(relocationId));

        Assert.Equal("delete_recovery_pending", exception.Code);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(
            Path.Join(source, "Author", "Book"),
            (await verification.Audiobooks.SingleAsync(candidate => candidate.Id == audiobookId)).BasePath);
        var persisted = await verification.RootFolderRelocations.SingleAsync();
        Assert.Equal(RootFolderRelocationStatus.Failed, persisted.Status);
        Assert.Empty(await verification.LibraryDirectoryOwnershipPathMigrations.ToListAsync());
    }

    [Fact]
    public async Task MetadataOnlyPathChange_RepairsInvalidStoredRootPath()
    {
        var target = Path.Join(Path.GetTempPath(), $"repair-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(target);
        var unrelatedBasePath = Path.Join(Path.GetTempPath(), $"unrelated-repair-book-{Guid.NewGuid():N}");
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Stale",
                Path = "relative-root",
                PathIdentityState = PathIdentityState.Unavailable,
                PathIdentityKey = null
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(new Audiobook
            {
                Title = "Unrelated",
                BasePath = unrelatedBasePath
            });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var result = await CreateService().StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.MetadataOnly,
                false,
                "Repaired",
                true,
                FileSystemCaseSensitivityMode.Auto));

        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
        await using var verification = await _factory.CreateDbContextAsync();
        var repaired = await verification.RootFolders.SingleAsync();
        Assert.Equal(target, repaired.Path);
        Assert.Equal("Repaired", repaired.Name);
        Assert.Equal(PathIdentityState.Valid, repaired.PathIdentityState);
        Assert.NotNull(repaired.PathIdentityKey);
        Assert.Equal(unrelatedBasePath, (await verification.Audiobooks.SingleAsync()).BasePath);
    }

    [Fact]
    public async Task EmptyRelocation_SetDefault_ClearsPreviousDefault()
    {
        var source = Path.Join(Path.GetTempPath(), $"empty-default-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"empty-default-target-{Guid.NewGuid():N}");
        var otherPath = Path.Join(Path.GetTempPath(), $"empty-default-other-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(otherPath);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Empty", Path = source };
            db.RootFolders.Add(root);
            db.RootFolders.Add(new RootFolder
            {
                Name = "Previous Default",
                Path = otherPath,
                IsDefault = true
            });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var result = await CreateService().StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                false,
                "Empty Default",
                true,
                FileSystemCaseSensitivityMode.Auto));

        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
        Assert.Equal(0, result.TotalJobs);
        await using var verification = await _factory.CreateDbContextAsync();
        var roots = await verification.RootFolders.OrderBy(root => root.Id).ToListAsync();
        Assert.True(roots.Single(root => root.Id == rootId).IsDefault);
        Assert.False(roots.Single(root => root.Id != rootId).IsDefault);
        Assert.Single(roots, root => root.IsDefault);
        var relocation = await verification.RootFolderRelocations.SingleAsync();
        Assert.Equal(RootFolderRelocationStatus.Completed, relocation.Status);
        Assert.Null(relocation.ActiveRootFolderId);
    }

    [Fact]
    public async Task MetadataOnlyPathChange_ForeignSyntaxRoot_RewritesRawStoredAudiobookPaths()
    {
        var target = Path.Join(Path.GetTempPath(), $"repair-foreign-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(target);
        var sourceRoot = OperatingSystem.IsWindows() ? "/legacy/library" : @"Z:\legacy\library";
        var sourceBook = OperatingSystem.IsWindows()
            ? sourceRoot + "/Author/Title"
            : sourceRoot + @"\Author\Title";
        var sourceFile = OperatingSystem.IsWindows()
            ? sourceBook + "/book.m4b"
            : sourceBook + @"\book.m4b";
        var unrelatedBasePath = Path.Join(Path.GetTempPath(), $"unrelated-book-{Guid.NewGuid():N}");
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Foreign",
                Path = sourceRoot,
                PathIdentityState = PathIdentityState.Unavailable
            };
            db.RootFolders.Add(root);
            db.Audiobooks.AddRange(
                new Audiobook
                {
                    Title = "Affected",
                    BasePath = sourceBook,
                    FilePath = sourceFile,
                    Files = [new AudiobookFile { Path = sourceFile }]
                },
                new Audiobook
                {
                    Title = "Unrelated",
                    BasePath = unrelatedBasePath
                });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var result = await CreateService().StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.MetadataOnly,
                false,
                "Repaired Foreign Root",
                false,
                FileSystemCaseSensitivityMode.Auto));

        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
        Assert.Equal(1, result.TotalJobs);
        Assert.Equal(1, result.CompletedJobs);
        await using var verification = await _factory.CreateDbContextAsync();
        var repairedRoot = await verification.RootFolders.SingleAsync();
        Assert.Equal(target, repairedRoot.Path);
        Assert.Equal(ManagedDirectoryIdentity.CurrentVersion, repairedRoot.DirectoryObjectIdentityVersion);
        Assert.False(string.IsNullOrWhiteSpace(repairedRoot.DirectoryObjectIdentity));
        Assert.Null(repairedRoot.DirectoryObjectIdentityUnavailableReason);
        var storage = await new RootFolderStorageHealthResolver(
            new DirectoryObjectIdentityResolver()).ResolveAsync(repairedRoot);
        Assert.Equal(RootFolderStorageState.Healthy, storage.State);
        var affected = await verification.Audiobooks
            .Include(audiobook => audiobook.Files)
            .SingleAsync(audiobook => audiobook.Title == "Affected");
        var expectedBasePath = Path.Join(target, "Author", "Title");
        Assert.Equal(expectedBasePath, affected.BasePath);
        Assert.Equal(Path.Join(expectedBasePath, "book.m4b"), affected.FilePath);
        Assert.Equal(Path.Join(expectedBasePath, "book.m4b"), Assert.Single(affected.Files!).Path);
        Assert.Equal(
            unrelatedBasePath,
            (await verification.Audiobooks.SingleAsync(audiobook => audiobook.Title == "Unrelated")).BasePath);
        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
    }

    [WindowsFact]
    public async Task MetadataOnlyPathChange_ForeignSyntaxCrash_StartupRecoversFromPersistedSemantics()
    {
        const string sourceRoot = "/server/mnt/drive/Audiobooks";
        const string sourceBook = "/server/mnt/drive/Audiobooks/Author/Title";
        const string sourceFile = "/server/mnt/drive/Audiobooks/Author/Title/book.m4b";
        var target = Path.Join(Path.GetTempPath(), $"repair-foreign-crash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(target);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Foreign",
                Path = sourceRoot,
                PathIdentityState = PathIdentityState.Unavailable
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(new Audiobook
            {
                Title = "Affected",
                BasePath = sourceBook,
                FilePath = sourceFile,
                Files = [new AudiobookFile { Path = sourceFile }]
            });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var interrupted = CreateService();
        interrupted.AfterMetadataOnlyJournalCommitForTest = () =>
            throw new IOException("Injected process loss after foreign-source journal commit.");

        await Assert.ThrowsAsync<IOException>(() =>
            interrupted.StartAsync(
                rootId,
                new RootFolderPathChangeCommand(
                    target,
                    RootFolderRelocationMode.MetadataOnly,
                    false,
                    "Recovered Foreign Root",
                    false,
                    FileSystemCaseSensitivityMode.Auto)));

        await using (var persisted = await _factory.CreateDbContextAsync())
        {
            var relocation = await persisted.RootFolderRelocations.SingleAsync();
            Assert.Equal(RootFolderRelocationStatus.Pending, relocation.Status);
            Assert.Equal(FileSystemCaseSensitivityMode.Sensitive, relocation.SourceCaseSensitivityMode);
            Assert.Equal(sourceRoot, (await persisted.RootFolders.SingleAsync()).Path);
        }

        await CreateService().ReconcileActiveAsync();

        await using var verification = await _factory.CreateDbContextAsync();
        var repairedRoot = await verification.RootFolders.SingleAsync();
        var audiobook = await verification.Audiobooks
            .Include(candidate => candidate.Files)
            .SingleAsync();
        var relocationAfter = await verification.RootFolderRelocations.SingleAsync();
        var expectedBasePath = Path.Join(target, "Author", "Title");
        Assert.Equal(target, repairedRoot.Path);
        Assert.Equal("Recovered Foreign Root", repairedRoot.Name);
        Assert.Equal(expectedBasePath, audiobook.BasePath);
        Assert.Equal(Path.Join(expectedBasePath, "book.m4b"), audiobook.FilePath);
        Assert.Equal(Path.Join(expectedBasePath, "book.m4b"), Assert.Single(audiobook.Files!).Path);
        Assert.Equal(RootFolderRelocationStatus.Completed, relocationAfter.Status);
        Assert.Null(relocationAfter.ActiveRootFolderId);
    }

    [WindowsFact]
    public async Task MetadataOnlyPathChange_ForeignSyntaxCollision_RetryUsesPersistedSourceSemantics()
    {
        const string sourceRoot = "/server/mnt/drive/Audiobooks";
        const string sourceBook = "/server/mnt/drive/Audiobooks/Author/Collision";
        var target = Path.Join(Path.GetTempPath(), $"repair-foreign-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(target);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Foreign",
                Path = sourceRoot,
                PathIdentityState = PathIdentityState.Unavailable
            };
            var audiobook = new Audiobook
            {
                Title = "Collision",
                BasePath = sourceBook,
                FilePath = sourceBook + "/book.MP3",
                Files =
                [
                    new AudiobookFile { Path = sourceBook + "/book.mp3" },
                    new AudiobookFile { Path = sourceBook + "/book.MP3" }
                ]
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var service = CreateService();
        var initial = await service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.MetadataOnly,
                false,
                "Repaired Foreign Root",
                false,
                FileSystemCaseSensitivityMode.Insensitive));

        Assert.Equal(RootFolderRelocationStatus.NeedsAttention, initial.Status);
        Assert.NotNull(initial.RelocationId);
        Assert.True(await service.IsAudiobookPathStateProtectedAsync(
            initial.SkippedAudiobookIds!.Single()));
        var persistedStatus = await CreateService().GetAsync(initial.RelocationId.Value);
        var persistedSkip = Assert.Single(
            Assert.IsType<RootFolderPathChangeResult>(persistedStatus).SkippedItems!);
        Assert.Equal(
            RootFolderRelocationSkipReasonCode.TargetIdentityCollision,
            persistedSkip.ReasonCode);
        var repair = await service.GetSkippedMetadataRepairDetailsAsync(
            initial.RelocationId!.Value,
            initial.SkippedAudiobookIds!.Single());
        Assert.NotNull(repair);
        Assert.Equal(
            RootFolderRelocationSkipReasonCode.TargetIdentityCollision,
            repair!.ReasonCode);
        var collision = Assert.Single(repair.CollisionGroups);
        var duplicate = collision.Files.Single(file =>
            file.RelativePath.EndsWith("book.MP3", StringComparison.Ordinal));

        Guid organizeOperationId;
        await using (var recovery = await _factory.CreateDbContextAsync())
        {
            var journal = new FileMutationJournal
            {
                Action = FileAction.Move,
                SourcePath = sourceBook + "/book.MP3",
                DestinationPath = Path.Join(target, "Author", "Collision", "book.MP3"),
                SourcePhysicalObjectIdentity = "test-source-generation",
                SourceLength = 1,
                State = FileMutationJournalState.Planned,
                AudiobookId = repair.AudiobookId,
                AudiobookFileId = duplicate.AudiobookFileId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            recovery.FileMutationJournals.Add(journal);
            await recovery.SaveChangesAsync();
            organizeOperationId = journal.OperationId;
        }
        var organizeBlocked = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
            service.RemoveSkippedMetadataRepairFileAsync(
                initial.RelocationId.Value,
                repair.AudiobookId,
                duplicate.AudiobookFileId));
        Assert.Equal("rename_recovery_pending", organizeBlocked.Code);
        await using (var recovery = await _factory.CreateDbContextAsync())
        {
            var journal = await recovery.FileMutationJournals.SingleAsync(candidate =>
                candidate.OperationId == organizeOperationId);
            journal.State = FileMutationJournalState.OwnerMetadataReconciled;
            await recovery.SaveChangesAsync();
        }

        Guid deletionIntentId;
        await using (var recovery = await _factory.CreateDbContextAsync())
        {
            var intent = new AudiobookDeletionIntent
            {
                AudiobookId = repair.AudiobookId,
                DeleteFolder = false,
                State = AudiobookDeletionIntentState.Planned,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            recovery.AudiobookDeletionIntents.Add(intent);
            await recovery.SaveChangesAsync();
            deletionIntentId = intent.Id;
        }
        var deleteBlocked = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
            service.RemoveSkippedMetadataRepairFileAsync(
                initial.RelocationId.Value,
                repair.AudiobookId,
                duplicate.AudiobookFileId));
        Assert.Equal("delete_recovery_pending", deleteBlocked.Code);
        await using (var recovery = await _factory.CreateDbContextAsync())
        {
            var intent = await recovery.AudiobookDeletionIntents.SingleAsync(candidate =>
                candidate.Id == deletionIntentId);
            intent.State = AudiobookDeletionIntentState.Completed;
            intent.UpdatedAt = DateTime.UtcNow;
            await recovery.SaveChangesAsync();
        }

        Guid moveJobId;
        await using (var recovery = await _factory.CreateDbContextAsync())
        {
            var moveJob = new MoveJob
            {
                AudiobookId = repair.AudiobookId,
                SourcePath = sourceBook,
                RequestedPath = Path.Join(target, "Author", "Collision"),
                Status = MoveJobStatus.Running,
                Phase = MoveJobPhase.Copying,
                UpdatedAt = DateTime.UtcNow
            };
            recovery.MoveJobs.Add(moveJob);
            await recovery.SaveChangesAsync();
            moveJobId = moveJob.Id;
        }
        var moveBlocked = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
            service.RemoveSkippedMetadataRepairFileAsync(
                initial.RelocationId.Value,
                repair.AudiobookId,
                duplicate.AudiobookFileId));
        Assert.Equal("move_recovery_required", moveBlocked.Code);
        await using (var recovery = await _factory.CreateDbContextAsync())
        {
            var moveJob = await recovery.MoveJobs.SingleAsync(candidate => candidate.Id == moveJobId);
            moveJob.Status = MoveJobStatus.Superseded;
            moveJob.UpdatedAt = DateTime.UtcNow;
            await recovery.SaveChangesAsync();
        }

        var afterRemoval = await service.RemoveSkippedMetadataRepairFileAsync(
            initial.RelocationId.Value,
            repair.AudiobookId,
            duplicate.AudiobookFileId);
        Assert.Empty(afterRemoval.CollisionGroups);

        var retried = await service.RetryAsync(initial.RelocationId.Value);

        Assert.Equal(RootFolderRelocationStatus.Completed, retried.Status);
        Assert.Equal(1, retried.TotalJobs);
        Assert.Equal(1, retried.CompletedJobs);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(target, (await verification.RootFolders.SingleAsync()).Path);
        var audiobookAfter = await verification.Audiobooks
            .Include(audiobook => audiobook.Files)
            .SingleAsync();
        Assert.Equal(Path.Join(target, "Author", "Collision"), audiobookAfter.BasePath);
        Assert.Null(audiobookAfter.FilePath);
        Assert.Equal(
            Path.Join(target, "Author", "Collision", "book.mp3"),
            Assert.Single(audiobookAfter.Files!).Path);
        var relocation = await verification.RootFolderRelocations.SingleAsync();
        Assert.Equal(RootFolderRelocationStatus.Completed, relocation.Status);
        Assert.Null(relocation.ActiveRootFolderId);
        Assert.Empty(await verification.RootFolderRelocationSkippedItems.ToListAsync());
    }

    [WindowsFact]
    public async Task MetadataOnlyPathChange_MissingTargetWithCollision_RemainsRepairableWithoutPhysicalIdentity()
    {
        const string sourceRoot = "/server/mnt/drive/Audiobooks";
        const string sourceBook = "/server/mnt/drive/Audiobooks/Author/Collision";
        var target = Path.Join(
            Path.GetTempPath(),
            $"repair-missing-target-collision-{Guid.NewGuid():N}");
        Assert.False(Directory.Exists(target));
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Foreign",
                Path = sourceRoot,
                PathIdentityState = PathIdentityState.Unavailable
            };
            var audiobook = new Audiobook
            {
                Title = "Collision",
                BasePath = sourceBook,
                Files =
                [
                    new AudiobookFile { Path = sourceBook + "/book.mp3" },
                    new AudiobookFile { Path = sourceBook + "/book.MP3" }
                ]
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var service = CreateService();
        var initial = await service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.MetadataOnly,
                false,
                "Missing Target",
                false,
                FileSystemCaseSensitivityMode.Insensitive));

        Assert.Equal(RootFolderRelocationStatus.NeedsAttention, initial.Status);
        Assert.Equal(TargetIdentityEnrollmentState.Unavailable, initial.TargetIdentityEnrollmentState);
        Assert.NotNull(initial.RelocationId);
        Assert.False(Directory.Exists(target));
        var repair = await service.GetSkippedMetadataRepairDetailsAsync(
            initial.RelocationId!.Value,
            initial.SkippedAudiobookIds!.Single());
        var collision = Assert.Single(Assert.IsType<RootFolderMetadataRepairDetails>(repair).CollisionGroups);
        var duplicate = collision.Files.Single(file =>
            file.RelativePath.EndsWith("book.MP3", StringComparison.Ordinal));
        await service.RemoveSkippedMetadataRepairFileAsync(
            initial.RelocationId.Value,
            initial.SkippedAudiobookIds.Single(),
            duplicate.AudiobookFileId);

        var retried = await service.RetryAsync(initial.RelocationId.Value);

        Assert.Equal(RootFolderRelocationStatus.Completed, retried.Status);
        Assert.Equal(TargetIdentityEnrollmentState.NotRequired, retried.TargetIdentityEnrollmentState);
        Assert.False(Directory.Exists(target));
        await using var verification = await _factory.CreateDbContextAsync();
        var repairedRoot = await verification.RootFolders.SingleAsync();
        Assert.Equal(target, repairedRoot.Path);
        Assert.Null(repairedRoot.DirectoryObjectIdentity);
        Assert.False(string.IsNullOrWhiteSpace(repairedRoot.DirectoryObjectIdentityUnavailableReason));
        var audiobookAfter = await verification.Audiobooks
            .Include(audiobook => audiobook.Files)
            .SingleAsync();
        Assert.Equal(Path.Join(target, "Author", "Collision"), audiobookAfter.BasePath);
        Assert.Equal(
            Path.Join(target, "Author", "Collision", "book.mp3"),
            Assert.Single(audiobookAfter.Files!).Path);
        var relocation = await verification.RootFolderRelocations.SingleAsync();
        Assert.Null(relocation.ActiveRootFolderId);
    }

    [Fact]
    public async Task MetadataOnlyPathChange_SamePathRepairsUnresolvedRootSemanticsAndIdentity()
    {
        var rootPath = FileService.GetTempDirectory(
            $"metadata-same-path-repair-{Guid.NewGuid():N}");
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = rootPath,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                ResolvedCaseSensitivity = FileSystemCaseSensitivity.Unknown,
                PathIdentityState = PathIdentityState.Unavailable,
                DirectoryObjectIdentityUnavailableReason =
                    "Persisted filesystem semantics are incomplete."
            };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var result = await CreateService().StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                rootPath,
                RootFolderRelocationMode.MetadataOnly,
                false,
                "Library",
                false,
                FileSystemCaseSensitivityMode.Auto,
                rootPath));

        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
        await using var verification = await _factory.CreateDbContextAsync();
        var repaired = await verification.RootFolders.AsNoTracking().SingleAsync();
        Assert.Equal(rootPath, repaired.Path);
        Assert.Equal(PathIdentityState.Valid, repaired.PathIdentityState);
        Assert.NotEqual(FileSystemCaseSensitivity.Unknown, repaired.ResolvedCaseSensitivity);
        Assert.Equal(ManagedDirectoryIdentity.CurrentVersion, repaired.DirectoryObjectIdentityVersion);
        Assert.False(string.IsNullOrWhiteSpace(repaired.DirectoryObjectIdentity));
        Assert.Null(repaired.DirectoryObjectIdentityUnavailableReason);
        var health = await new RootFolderStorageHealthResolver(
            new DirectoryObjectIdentityResolver()).ResolveAsync(repaired);
        Assert.Equal(RootFolderStorageState.Healthy, health.State);
        Assert.True(health.CanMutateFilesystem);
    }

    [Fact]
    public async Task MetadataOnlyPathChange_MissingTarget_RemainsMissingThenBecomesUnconfirmedWhenItAppears()
    {
        var source = Path.Join(TempRoot, $"metadata-missing-source-{Guid.NewGuid():N}");
        var target = Path.Join(TempRoot, $"metadata-missing-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        Assert.False(Directory.Exists(target));
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source
            };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var result = await CreateService().StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.MetadataOnly,
                false,
                "Missing Target",
                false,
                FileSystemCaseSensitivityMode.Auto));

        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
        await using var verification = await _factory.CreateDbContextAsync();
        var rootAfter = await verification.RootFolders.AsNoTracking().SingleAsync();
        Assert.Equal(target, rootAfter.Path);
        Assert.Null(rootAfter.DirectoryObjectIdentityVersion);
        Assert.Null(rootAfter.DirectoryObjectIdentity);
        Assert.False(string.IsNullOrWhiteSpace(rootAfter.DirectoryObjectIdentityUnavailableReason));
        var healthResolver = new RootFolderStorageHealthResolver(
            new DirectoryObjectIdentityResolver());
        var missing = await healthResolver.ResolveAsync(rootAfter);
        Assert.Equal(RootFolderStorageState.Missing, missing.State);
        Assert.False(missing.CanConfirmCurrentFolder);

        Directory.CreateDirectory(target);
        var appeared = await healthResolver.ResolveAsync(rootAfter);
        Assert.Equal(RootFolderStorageState.Unconfirmed, appeared.State);
        Assert.True(appeared.CanConfirmCurrentFolder);
        Assert.False(string.IsNullOrWhiteSpace(appeared.ConfirmationToken));
    }

    [Fact]
    public async Task MetadataOnlyPathChange_AmbiguousStoredRoot_RewritesAffectedAudiobookPathsUsingConfirmedTargetSyntax()
    {
        var target = Path.Join(Path.GetTempPath(), $"repair-ambiguous-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(target);
        var sourceRoot = $"//legacy/library-{Guid.NewGuid():N}";
        var sourceBook = sourceRoot + "/Author/Title";
        var sourceFile = sourceBook + "/book.m4b";
        Assert.False(FileSystemPathIdentity.TryDetectAbsoluteSyntax(sourceRoot, out _));
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Ambiguous",
                Path = sourceRoot,
                PathIdentityState = PathIdentityState.Unavailable
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(new Audiobook
            {
                Title = "Affected",
                BasePath = sourceBook,
                FilePath = sourceFile,
                Files = [new AudiobookFile { Path = sourceFile }]
            });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var result = await CreateService().StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.MetadataOnly,
                false,
                "Repaired Ambiguous Root",
                false,
                FileSystemCaseSensitivityMode.Auto));

        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
        Assert.Equal(1, result.TotalJobs);
        Assert.Equal(1, result.CompletedJobs);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(target, (await verification.RootFolders.SingleAsync()).Path);
        var affected = await verification.Audiobooks
            .Include(audiobook => audiobook.Files)
            .SingleAsync();
        var expectedBasePath = Path.Join(target, "Author", "Title");
        Assert.Equal(expectedBasePath, affected.BasePath);
        Assert.Equal(Path.Join(expectedBasePath, "book.m4b"), affected.FilePath);
        Assert.Equal(Path.Join(expectedBasePath, "book.m4b"), Assert.Single(affected.Files!).Path);
        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
    }

    [Fact]
    public async Task MetadataOnlyPathChange_AmbiguousStoredRoot_CrashRecoveryReusesConfirmedTargetSyntax()
    {
        var target = Path.Join(Path.GetTempPath(), $"repair-ambiguous-crash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(target);
        var sourceRoot = $"//legacy/library-{Guid.NewGuid():N}";
        var sourceBook = sourceRoot + "/Author/Title";
        Assert.False(FileSystemPathIdentity.TryDetectAbsoluteSyntax(sourceRoot, out _));
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Ambiguous",
                Path = sourceRoot,
                PathIdentityState = PathIdentityState.Unavailable
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(new Audiobook
            {
                Title = "Affected",
                BasePath = sourceBook
            });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var interrupted = CreateService();
        interrupted.AfterMetadataOnlyJournalCommitForTest = () =>
            throw new IOException("Injected ambiguous-source process loss.");
        await Assert.ThrowsAsync<IOException>(() =>
            interrupted.StartAsync(
                rootId,
                new RootFolderPathChangeCommand(
                    target,
                    RootFolderRelocationMode.MetadataOnly,
                    false,
                    "Recovered Ambiguous Root",
                    false,
                    FileSystemCaseSensitivityMode.Auto)));

        await using (var persisted = await _factory.CreateDbContextAsync())
        {
            var relocation = await persisted.RootFolderRelocations.SingleAsync();
            Assert.Equal(RootFolderRelocationStatus.Pending, relocation.Status);
            Assert.Equal(FileSystemCaseSensitivityMode.Sensitive, relocation.SourceCaseSensitivityMode);
        }

        await CreateService().ReconcileActiveAsync();

        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(target, (await verification.RootFolders.SingleAsync()).Path);
        Assert.Equal(
            Path.Join(target, "Author", "Title"),
            (await verification.Audiobooks.SingleAsync()).BasePath);
        var completed = await verification.RootFolderRelocations.SingleAsync();
        Assert.Equal(RootFolderRelocationStatus.Completed, completed.Status);
        Assert.Null(completed.ActiveRootFolderId);
    }

    [Fact]
    public async Task MetadataOnlyPathChange_AmbiguousStoredRootWithLiveOwnership_RetiresUnprovableCleanupAuthority()
    {
        var nativeSource = Path.Join(TempRoot, $"ambiguous-owned-source-{Guid.NewGuid():N}");
        var target = Path.Join(TempRoot, $"ambiguous-owned-target-{Guid.NewGuid():N}");
        var ownedDirectory = Path.Join(nativeSource, "Author");
        Directory.CreateDirectory(ownedDirectory);
        Directory.CreateDirectory(target);
        var ambiguousSource = OperatingSystem.IsWindows()
            ? "//?/" + Path.GetFullPath(nativeSource).Replace('\\', '/')
            : "/" + Path.GetFullPath(nativeSource);
        Assert.False(FileSystemPathIdentity.TryDetectAbsoluteSyntax(ambiguousSource, out _));
        var ambiguousBook = ambiguousSource.TrimEnd('/') + "/Author/Title";
        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Ambiguous Owned",
                Path = ambiguousSource,
                PathIdentityState = PathIdentityState.Unavailable
            };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
            db.Audiobooks.Add(new Audiobook
            {
                Title = "Affected",
                BasePath = ambiguousBook
            });
            db.LibraryDirectoryOwnerships.Add(new LibraryDirectoryOwnership
            {
                Path = ownedDirectory,
                CanonicalPath = ownedDirectory,
                PathSyntax = semantics.Syntax,
                PathCaseSensitivity = semantics.CaseSensitivity,
                PathCaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                PathIdentityBoundary = nativeSource,
                PathIdentityLookupKey = FileSystemPathIdentity.CreateLookupKey(
                    "library-directory",
                    ownedDirectory,
                    semantics.Syntax),
                PathOwnershipKey = FileSystemPathIdentity.CreateKey(
                    "library-directory",
                    ownedDirectory,
                    semantics),
                OwnershipToken = Guid.NewGuid().ToString("N"),
                CreationWorkflow = "test-fixture",
                ManagedRootFolderId = rootId,
                State = LibraryDirectoryOwnershipState.Owned
            });
            await db.SaveChangesAsync();
        }

        var result = await CreateService().StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.MetadataOnly,
                false,
                "Repaired Ambiguous Owned Root",
                false,
                FileSystemCaseSensitivityMode.Auto));

        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(target, (await verification.RootFolders.SingleAsync()).Path);
        Assert.Equal(
            Path.Join(target, "Author", "Title"),
            (await verification.Audiobooks.SingleAsync()).BasePath);
        var retired = await verification.LibraryDirectoryOwnerships.SingleAsync();
        Assert.Equal(LibraryDirectoryOwnershipState.Removed, retired.State);
        Assert.Null(retired.PathOwnershipKey);
        Assert.Null(retired.ManagedRootFolderId);
        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
        Assert.Empty(await verification.LibraryDirectoryOwnershipPathMigrations.ToListAsync());
        Assert.True(Directory.Exists(ownedDirectory));
    }

    [Fact]
    public async Task MetadataOnlyPathChange_SourceResolutionThrowsIoException_RepairsRoot()
    {
        var source = Path.Join(Path.GetTempPath(), $"unavailable-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"unavailable-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Unavailable",
                Path = source,
                PathIdentityState = PathIdentityState.Unavailable
            };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var result = await CreateService(
            semanticsResolver: new SourceThrowingSemanticsResolver(source)).StartAsync(
                rootId,
                new RootFolderPathChangeCommand(
                    target,
                    RootFolderRelocationMode.MetadataOnly,
                    false,
                    "Repaired",
                    false,
                    FileSystemCaseSensitivityMode.Auto));

        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
        await using var verification = await _factory.CreateDbContextAsync();
        var repairedRoot = await verification.RootFolders.SingleAsync();
        Assert.Equal(target, repairedRoot.Path);
        Assert.Equal("Repaired", repairedRoot.Name);
    }

    [Theory]
    [InlineData(LibraryDirectoryOwnershipState.Owned)]
    [InlineData(LibraryDirectoryOwnershipState.Unavailable)]
    public async Task MetadataOnlyPathChange_UnavailableOwnedSource_RetiresCleanupAuthorityAndRepairsRoot(
        LibraryDirectoryOwnershipState ownershipState)
    {
        var source = Path.Join(TempRoot, $"unavailable-owned-source-{Guid.NewGuid():N}");
        var target = Path.Join(TempRoot, $"unavailable-owned-target-{Guid.NewGuid():N}");
        var sourceOwned = Path.Join(source, "Author", "Book");
        Directory.CreateDirectory(sourceOwned);
        Directory.CreateDirectory(target);
        var sourceResolution = await new FileSystemSemanticsResolver().ResolveAsync(source);
        Assert.Equal(PathIdentityState.Valid, sourceResolution.State);
        var rootIdentity = await new DirectoryObjectIdentityResolver().ResolveAsync(source);
        Assert.True(rootIdentity.IsAvailable, rootIdentity.UnavailableReason);
        var ownershipToken = Guid.NewGuid().ToString("N");
        string ownershipIdentity;
        using (var ownedAnchor = PinnedDirectoryCreation.OpenPinnedBoundary(sourceOwned))
        {
            ownershipIdentity = ManagedDirectoryIdentity.Create(
                ownershipToken,
                ownedAnchor.GetDirectoryObjectIdentity());
        }

        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Unavailable Library",
                Path = source,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                ResolvedCaseSensitivity = sourceResolution.Semantics.CaseSensitivity,
                PathIdentityState = PathIdentityState.Valid,
                PathIdentityKey = FileSystemPathIdentity.CreateKey(
                    "root",
                    source,
                    sourceResolution.Semantics),
                DirectoryObjectIdentityVersion = rootIdentity.Version,
                DirectoryObjectIdentity = rootIdentity.Value
            };
            var audiobook = new Audiobook
            {
                Title = "Book",
                BasePath = sourceOwned
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            rootId = root.Id;
            db.LibraryDirectoryOwnerships.Add(new LibraryDirectoryOwnership
            {
                Path = sourceOwned,
                CanonicalPath = sourceOwned,
                PathSyntax = sourceResolution.Semantics.Syntax,
                PathCaseSensitivity = sourceResolution.Semantics.CaseSensitivity,
                PathCaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                PathIdentityBoundary = sourceOwned,
                PathIdentityLookupKey = FileSystemPathIdentity.CreateLookupKey(
                    "library-directory",
                    sourceOwned,
                    sourceResolution.Semantics.Syntax),
                PathOwnershipKey = ownershipState == LibraryDirectoryOwnershipState.Unavailable
                    ? null
                    : FileSystemPathIdentity.CreateKey(
                        "library-directory",
                        sourceOwned,
                        sourceResolution.Semantics),
                OwnershipToken = ownershipToken,
                State = ownershipState,
                CreationWorkflow = "Test",
                AudiobookId = audiobook.Id,
                ManagedRootFolderId = root.Id,
                DirectoryObjectIdentityVersion = ManagedDirectoryIdentity.CurrentVersion,
                DirectoryObjectIdentity = ownershipIdentity,
                DirectoryObjectIdentityUnavailableReason =
                    ownershipState == LibraryDirectoryOwnershipState.Unavailable
                        ? "The source directory is unavailable."
                        : null,
                StateReason = ownershipState == LibraryDirectoryOwnershipState.Unavailable
                    ? "Physical directory ownership could not be reconciled safely."
                    : null
            });
            await db.SaveChangesAsync();
        }

        Directory.Delete(source, recursive: true);
        Assert.False(Directory.Exists(source));
        Assert.False(Directory.Exists(Path.Join(target, "Author", "Book")));

        var result = await CreateService().StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.MetadataOnly,
                false,
                "Repaired Library",
                false,
                FileSystemCaseSensitivityMode.Auto,
                source));

        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
        await using var verification = await _factory.CreateDbContextAsync();
        var repairedRoot = await verification.RootFolders.SingleAsync();
        var repairedBook = await verification.Audiobooks.SingleAsync();
        var retired = await verification.LibraryDirectoryOwnerships.SingleAsync();
        Assert.Equal(target, repairedRoot.Path);
        Assert.Equal(Path.Join(target, "Author", "Book"), repairedBook.BasePath);
        Assert.Equal(LibraryDirectoryOwnershipState.Removed, retired.State);
        Assert.Null(retired.PathOwnershipKey);
        Assert.Null(retired.ManagedRootFolderId);
        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
        Assert.Empty(await verification.LibraryDirectoryOwnershipPathMigrations.ToListAsync());
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public async Task RelocatePathChange_RejectsInvalidStoredRootPath()
    {
        var target = Path.Join(Path.GetTempPath(), $"repair-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(target);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Stale", Path = "relative-root" };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var exception = await Assert.ThrowsAsync<RootFolderPathChangeRejectedException>(() =>
            CreateService().StartAsync(
                rootId,
                new RootFolderPathChangeCommand(
                    target,
                    RootFolderRelocationMode.Relocate,
                    false,
                    "Still Stale",
                    false,
                    FileSystemCaseSensitivityMode.Auto)));

        Assert.Equal("root_folder_source_unavailable", exception.Code);
        Assert.Contains("metadata-only", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReconcileActive_DoesNotAdoptLiveSemanticsForAuthorizedRoot()
    {
        var rootPath = FileService.GetTempDirectory(
            $"reconcile-preserve-semantics-{Guid.NewGuid():N}");
        var actual = FileSystemPathSemantics.CurrentHostDefault;
        var persistedSensitivity = actual.CaseSensitivity
            == FileSystemCaseSensitivity.Sensitive
                ? FileSystemCaseSensitivity.Insensitive
                : FileSystemCaseSensitivity.Sensitive;
        var persistedSemantics = new FileSystemPathSemantics(
            actual.Syntax,
            persistedSensitivity);
        var identity = await new DirectoryObjectIdentityResolver()
            .ResolveAsync(rootPath);
        Assert.True(identity.IsAvailable, identity.UnavailableReason);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.RootFolders.Add(new RootFolder
            {
                Name = "Authorized",
                Path = rootPath,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                ResolvedCaseSensitivity = persistedSensitivity,
                PathIdentityState = PathIdentityState.Valid,
                PathIdentityKey = FileSystemPathIdentity.CreateKey(
                    "root",
                    rootPath,
                    persistedSemantics),
                DirectoryObjectIdentityVersion = identity.Version,
                DirectoryObjectIdentity = identity.Value
            });
            await db.SaveChangesAsync();
        }
        var semanticsResolver = new Mock<IFileSystemSemanticsResolver>(
            MockBehavior.Strict);

        await CreateService(semanticsResolver: semanticsResolver.Object)
            .ReconcileActiveAsync();

        await using var verification = await _factory.CreateDbContextAsync();
        var root = await verification.RootFolders.AsNoTracking().SingleAsync();
        Assert.Equal(persistedSensitivity, root.ResolvedCaseSensitivity);
        Assert.Equal(PathIdentityState.Valid, root.PathIdentityState);
        Assert.Equal(
            FileSystemPathIdentity.CreateKey("root", rootPath, persistedSemantics),
            root.PathIdentityKey);
        semanticsResolver.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReconcileActive_MarksInvalidStoredRootUnavailableInsteadOfThrowing()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.RootFolders.Add(new RootFolder
            {
                Name = "Stale",
                Path = "relative-root",
                PathIdentityState = PathIdentityState.Valid,
                PathIdentityKey = "stale"
            });
            await db.SaveChangesAsync();
        }

        await CreateService().ReconcileActiveAsync();

        await using var verification = await _factory.CreateDbContextAsync();
        var root = await verification.RootFolders.SingleAsync();
        Assert.Equal(PathIdentityState.Unavailable, root.PathIdentityState);
        Assert.Null(root.PathIdentityKey);
        Assert.Equal(FileSystemCaseSensitivity.Unknown, root.ResolvedCaseSensitivity);
    }

    private static async Task<MoveEnqueueCommand> CreateMoveCommandAsync(
        int audiobookId,
        string sourcePath,
        string targetPath)
    {
        var resolver = new FileSystemSemanticsResolver();
        var sourceResolution = await resolver.ResolveAsync(sourcePath);
        var targetResolution = await resolver.ResolveAsync(targetPath);
        Assert.Equal(PathIdentityState.Valid, sourceResolution.State);
        Assert.Equal(PathIdentityState.Valid, targetResolution.State);
        var targetBoundary = FindExistingMoveTargetBoundary(targetPath);
        var directoryIdentityResolver = new DirectoryObjectIdentityResolver();
        var sourceAuthorizationBoundary = Path.GetDirectoryName(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePath)))
            ?? throw new InvalidOperationException(
                "Move test source has no parent authorization boundary.");
        var sourceDirectoryIdentity = await directoryIdentityResolver.ResolveAsync(
            sourceAuthorizationBoundary);
        Assert.True(
            sourceDirectoryIdentity.IsAvailable,
            sourceDirectoryIdentity.UnavailableReason);
        var targetDirectoryIdentity = await directoryIdentityResolver
            .ResolveAsync(targetBoundary);
        Assert.True(
            targetDirectoryIdentity.IsAvailable,
            targetDirectoryIdentity.UnavailableReason);
        return new MoveEnqueueCommand(
            audiobookId,
            sourcePath,
            PathIdentitySnapshot.FromResolution(
                sourceResolution.Semantics,
                FileSystemCaseSensitivityMode.Auto,
                sourceResolution.BoundaryPath,
                sourcePath),
            [
                new MoveSourceManifestEntry(
                    "book.m4b",
                    MoveJobEntryType.File,
                    1,
                    DateTime.UnixEpoch,
                    new string('A', 64))
            ],
            targetPath,
            PathIdentitySnapshot.FromResolution(
                targetResolution.Semantics,
                FileSystemCaseSensitivityMode.Auto,
                targetBoundary,
                targetPath),
            sourceDirectoryIdentity.Version!.Value,
            sourceDirectoryIdentity.Value!,
            targetDirectoryIdentity.Version!.Value,
            targetDirectoryIdentity.Value!,
            DeleteEmptySource: true,
            SourceCleanupBoundary: sourceAuthorizationBoundary);
    }

    private static string FindExistingMoveTargetBoundary(string targetPath)
    {
        var current = Directory.Exists(targetPath)
            ? Path.GetFullPath(targetPath)
            : Path.GetDirectoryName(Path.GetFullPath(targetPath));
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (Directory.Exists(current))
            {
                return current;
            }
            current = Path.GetDirectoryName(current);
        }

        throw new InvalidOperationException(
            "Move test target has no existing authorization boundary.");
    }

    [Fact]
    public async Task ConcurrentMoveFirst_BlocksWaitingRelocationAfterMoveIsPersisted()
    {
        var (rootId, audiobookId, source, target) = await SeedRelocationScenarioAsync();
        var coordinator = new FirstEntryPausingCoordinator();
        var relocationService = new RootFolderRelocationService(
            _factory,
            new FileSystemSemanticsResolver(),
            new NoopHubBroadcaster(),
            TimeProvider.System,
            coordinator,
            _operationCoordinator,
            CreateMoveSourceManifestService(),
            TestLibraryFilesystemReadiness.Ready());
        var moveService = new MoveQueueService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MoveQueueService>.Instance,
            new EfMoveQueuePersistence(_factory, new FileSystemSemanticsResolver()),
            new NoopHubBroadcaster(),
            TimeProvider.System,
            new FileSystemSemanticsResolver(),
            relocationService,
            coordinator);
        var standaloneTarget = Path.Join(Path.GetTempPath(), $"standalone-{Guid.NewGuid():N}");
        Directory.CreateDirectory(standaloneTarget);

        var moveTask = moveService.EnqueueMoveAsync(
            await CreateMoveCommandAsync(
                audiobookId,
                Path.Join(source, "Author", "Title"),
                standaloneTarget));
        await coordinator.FirstEntered;
        var relocationTask = relocationService.StartAsync(
            rootId,
            BuildRelocationCommand(target));
        await Task.Delay(50);
        Assert.False(relocationTask.IsCompleted);

        coordinator.ReleaseFirst();
        await moveTask;
        var exception = await Assert.ThrowsAsync<RootFolderPathChangeRejectedException>(() => relocationTask);
        Assert.Equal("root_folder_move_recovery_blocked", exception.Code);
        Assert.Contains("unresolved move job", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RelocationStart_ActiveDeletionRecovery_BlocksBeforeChildMovePublication()
    {
        var (rootId, audiobookId, _, target) = await SeedRelocationScenarioAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.AudiobookDeletionIntents.Add(new AudiobookDeletionIntent
            {
                AudiobookId = audiobookId,
                DeleteFolder = true,
                State = AudiobookDeletionIntentState.NeedsAttention
            });
            await db.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<RootFolderPathChangeRejectedException>(() =>
            CreateService().StartAsync(rootId, BuildRelocationCommand(target)));

        Assert.Equal("delete_recovery_pending", exception.Code);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
        Assert.Empty(await verification.MoveJobs.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentRelocationFirst_BlocksWaitingMoveAfterRelocationIsPersisted()
    {
        var (rootId, audiobookId, source, target) = await SeedRelocationScenarioAsync();
        var coordinator = new FirstEntryPausingCoordinator();
        var relocationService = new RootFolderRelocationService(
            _factory,
            new FileSystemSemanticsResolver(),
            new NoopHubBroadcaster(),
            TimeProvider.System,
            coordinator,
            _operationCoordinator,
            CreateMoveSourceManifestService(),
            TestLibraryFilesystemReadiness.Ready());
        var moveService = new MoveQueueService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MoveQueueService>.Instance,
            new EfMoveQueuePersistence(_factory, new FileSystemSemanticsResolver()),
            new NoopHubBroadcaster(),
            TimeProvider.System,
            new FileSystemSemanticsResolver(),
            relocationService,
            coordinator);

        var relocationTask = relocationService.StartAsync(
            rootId,
            BuildRelocationCommand(target));
        await coordinator.FirstEntered;
        var moveTask = moveService.EnqueueMoveAsync(
            await CreateMoveCommandAsync(
                audiobookId,
                Path.Join(source, "Author", "Title"),
                Path.Join(target, "Author", "Title")));
        await Task.Delay(50);
        Assert.False(moveTask.IsCompleted);

        coordinator.ReleaseFirst();
        await relocationTask;
        await Assert.ThrowsAsync<MoveRelocationConflictException>(() => moveTask);
    }

    [Fact]
    public async Task RetryAsync_ActiveRenameRecovery_BlocksBeforeReactivatingChildMove()
    {
        var (_, audiobookId, _, target) = await SeedRelocationScenarioAsync();
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            rootId = await db.RootFolders.Select(root => root.Id).SingleAsync();
        }
        var service = CreateService();
        var started = await service.StartAsync(rootId, BuildRelocationCommand(target));
        Assert.NotNull(started.RelocationId);

        Guid jobId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var relocation = await db.RootFolderRelocations.SingleAsync();
            relocation.Status = RootFolderRelocationStatus.NeedsAttention;
            relocation.Error = "Retry required.";
            var job = await db.MoveJobs.SingleAsync();
            job.Status = MoveJobStatus.NeedsAttention;
            job.ActiveDeduplicationKey = null;
            job.Error = "Interrupted move.";
            jobId = job.Id;
            var audiobookFileId = await db.AudiobookFiles
                .Where(file => file.AudiobookId == audiobookId)
                .Select(file => file.Id)
                .SingleAsync();
            db.FileMutationJournals.Add(new FileMutationJournal
            {
                Action = FileAction.Move,
                SourcePath = Path.Join(relocation.SourcePath, "Author", "Title", "book.m4b"),
                DestinationPath = Path.Join(relocation.TargetPath, "Author", "Title", "book.m4b"),
                SourcePhysicalObjectIdentity = "test-source-generation",
                SourceLength = 5,
                State = FileMutationJournalState.Planned,
                AudiobookId = audiobookId,
                AudiobookFileId = audiobookFileId
            });
            await db.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
            service.RetryAsync(started.RelocationId.Value));

        Assert.Equal("rename_recovery_pending", exception.Code);
        await using var verification = await _factory.CreateDbContextAsync();
        var unchangedJob = await verification.MoveJobs.SingleAsync(job => job.Id == jobId);
        Assert.Equal(MoveJobStatus.NeedsAttention, unchangedJob.Status);
        Assert.Null(unchangedJob.ActiveDeduplicationKey);
    }

    [Fact]
    public async Task RelocationStart_WaitsForActiveAudiobookOperationBeforeLoadingTransactionState()
    {
        var (rootId, audiobookId, _, target) = await SeedRelocationScenarioAsync();
        var operationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = _operationCoordinator.ExecuteExclusiveAsync(
            audiobookId,
            async _ =>
            {
                operationEntered.SetResult();
                await releaseOperation.Task;
            });
        await operationEntered.Task;

        var relocationTask = CreateService().StartAsync(
            rootId,
            BuildRelocationCommand(target));

        await Task.Delay(50);
        Assert.False(relocationTask.IsCompleted);
        releaseOperation.SetResult();
        await blocker;

        var result = await relocationTask;
        Assert.Equal(RootFolderRelocationStatus.Pending, result.Status);
    }

    [Fact]
    public async Task MetadataOnly_UpdatesRootAndAudiobooksInOneTransaction()
    {
        var source = Path.Join(Path.GetTempPath(), $"metadata-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"metadata-target-{Guid.NewGuid():N}");
        var unrelated = Path.Join(Path.GetTempPath(), $"metadata-unrelated-{Guid.NewGuid():N}", "bonus.mp3");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            var localBasePath = Path.Join(source, "Title");
            db.Audiobooks.AddRange(
                new Audiobook
                {
                    Title = "Title",
                    BasePath = localBasePath,
                    FilePath = Path.Join(localBasePath, "book.m4b"),
                    ImageUrl = Path.Join(localBasePath, "cover.jpg"),
                    Files =
                    [
                        new AudiobookFile { Path = Path.Join(localBasePath, "book.m4b") },
                        new AudiobookFile { Path = Path.Join("disc-1", "chapter.mp3") },
                        new AudiobookFile { Path = unrelated }
                    ]
                },
                new Audiobook
                {
                    Title = "Remote Image",
                    BasePath = Path.Join(source, "Remote Image"),
                    ImageUrl = "https://example.test/cover.jpg"
                });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var result = await CreateService().StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.MetadataOnly,
                false,
                "Metadata Library",
                false,
                FileSystemCaseSensitivityMode.Auto));

        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(target, (await verification.RootFolders.SingleAsync()).Path);
        var audiobooks = await verification.Audiobooks
            .Include(audiobook => audiobook.Files)
            .OrderBy(audiobook => audiobook.Title)
            .ToListAsync();
        var remoteImageAudiobook = audiobooks[0];
        var localAudiobook = audiobooks[1];
        var expectedBasePath = Path.Join(target, "Title");
        Assert.Equal(expectedBasePath, localAudiobook.BasePath);
        Assert.Equal(Path.Join(expectedBasePath, "book.m4b"), localAudiobook.FilePath);
        Assert.Equal(Path.Join(expectedBasePath, "cover.jpg"), localAudiobook.ImageUrl);
        Assert.Contains(localAudiobook.Files!, file => file.Path == Path.Join(expectedBasePath, "book.m4b"));
        Assert.Contains(localAudiobook.Files!, file => file.Path == Path.Join("disc-1", "chapter.mp3"));
        Assert.Contains(localAudiobook.Files!, file => file.Path == unrelated);
        Assert.Equal(Path.Join(target, "Remote Image"), remoteImageAudiobook.BasePath);
        Assert.Equal("https://example.test/cover.jpg", remoteImageAudiobook.ImageUrl);
        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
    }

    [Fact]
    public async Task MetadataOnly_SamePathCaseSensitivityChange_MigratesPersistedIdentityKeys()
    {
        var rootPath = Path.Join(
            TempRoot,
            $"metadata-semantics-{Guid.NewGuid():N}");
        var audiobookPath = Path.Join(rootPath, "Title");
        var audioPath = Path.Join(audiobookPath, "book.m4b");
        Directory.CreateDirectory(audiobookPath);
        var sourceSemantics = new FileSystemPathSemantics(
            FileSystemPathSemantics.CurrentHostDefault.Syntax,
            FileSystemCaseSensitivity.Sensitive);
        var targetSemantics = new FileSystemPathSemantics(
            FileSystemPathSemantics.CurrentHostDefault.Syntax,
            FileSystemCaseSensitivity.Insensitive);
        int rootId;
        string originalFileOwnershipKey;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = rootPath,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
                ResolvedCaseSensitivity = FileSystemCaseSensitivity.Sensitive,
                PathIdentityState = PathIdentityState.Valid,
                PathIdentityKey = FileSystemPathIdentity.CreateKey(
                    "root",
                    rootPath,
                    sourceSemantics)
            };
            var audiobook = new Audiobook
            {
                Title = "Title",
                BasePath = audiobookPath
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            await AddTrackedFileAsync(
                db,
                audiobook,
                audioPath,
                rootPath,
                sourceSemantics,
                FileSystemCaseSensitivityMode.Sensitive);
            rootId = root.Id;
            originalFileOwnershipKey = (await db.AudiobookFiles.SingleAsync()).PathOwnershipKey!;
        }

        var result = await CreateService().StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                rootPath,
                RootFolderRelocationMode.MetadataOnly,
                false,
                "Library",
                false,
                FileSystemCaseSensitivityMode.Insensitive,
                rootPath));

        await using var verification = await _factory.CreateDbContextAsync();
        var rootAfter = await verification.RootFolders.SingleAsync();
        var fileAfter = await verification.AudiobookFiles.SingleAsync();
        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
        Assert.Equal(rootPath, rootAfter.Path);
        Assert.Equal(FileSystemCaseSensitivityMode.Insensitive, rootAfter.CaseSensitivityMode);
        Assert.Equal(FileSystemCaseSensitivity.Insensitive, rootAfter.ResolvedCaseSensitivity);
        Assert.Equal(
            FileSystemPathIdentity.CreateKey("root", rootPath, targetSemantics),
            rootAfter.PathIdentityKey);
        Assert.Equal(audioPath, fileAfter.Path);
        Assert.NotEqual(originalFileOwnershipKey, fileAfter.PathOwnershipKey);
        Assert.Equal(
            AudiobookFilePathIdentity.CreateValid(
                audioPath,
                targetSemantics,
                FileSystemCaseSensitivityMode.Insensitive,
                rootPath).OwnershipKey,
            fileAfter.PathOwnershipKey);
        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
    }

    [Fact]
    public async Task MetadataOnly_TargetRootReplacedAtAtomicCommit_DoesNotCommitStaleGeneration()
    {
        var source = Path.Join(
            TempRoot,
            $"metadata-target-generation-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            TempRoot,
            $"metadata-target-generation-target-{Guid.NewGuid():N}");
        var displacedTarget = target + ".original";
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source
            };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var service = CreateService();
        service.BeforeMetadataOnlyAtomicCommitForTest = () =>
        {
            Directory.Move(target, displacedTarget);
            Directory.CreateDirectory(target);
            File.WriteAllText(
                Path.Join(target, "foreign.txt"),
                "replacement generation");
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartAsync(
                rootId,
                new RootFolderPathChangeCommand(
                    target,
                    RootFolderRelocationMode.MetadataOnly,
                    false,
                    "Metadata Library",
                    false,
                    FileSystemCaseSensitivityMode.Auto)));

        await using var verification = await _factory.CreateDbContextAsync();
        var rootAfter = await verification.RootFolders.SingleAsync();
        var relocation = await verification.RootFolderRelocations.SingleAsync();
        Assert.Equal(source, rootAfter.Path);
        Assert.Equal(RootFolderRelocationStatus.Failed, relocation.Status);
        Assert.Equal(rootId, relocation.ActiveRootFolderId);
        Assert.True(Directory.Exists(displacedTarget));
        Assert.Equal(
            "replacement generation",
            await File.ReadAllTextAsync(Path.Join(target, "foreign.txt")));
    }

    [Fact]
    public async Task MetadataOnly_TargetRootReplacedImmediatelyAfterAtomicCommit_MarksFailedRecovery()
    {
        var source = Path.Join(
            TempRoot,
            $"metadata-post-commit-generation-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            TempRoot,
            $"metadata-post-commit-generation-target-{Guid.NewGuid():N}");
        var displacedTarget = target + ".original";
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source
            };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var service = CreateService();
        service.AfterMetadataOnlyAtomicCommitForTest = () =>
        {
            Directory.Move(target, displacedTarget);
            Directory.CreateDirectory(target);
            File.WriteAllText(
                Path.Join(target, "foreign.txt"),
                "replacement generation");
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartAsync(
                rootId,
                new RootFolderPathChangeCommand(
                    target,
                    RootFolderRelocationMode.MetadataOnly,
                    false,
                    "Metadata Library",
                    false,
                    FileSystemCaseSensitivityMode.Auto)));

        await using var verification = await _factory.CreateDbContextAsync();
        var rootAfter = await verification.RootFolders.SingleAsync();
        var relocation = await verification.RootFolderRelocations.SingleAsync();
        Assert.Equal(target, rootAfter.Path);
        Assert.Equal(RootFolderRelocationStatus.Failed, relocation.Status);
        Assert.Equal(rootId, relocation.ActiveRootFolderId);
        Assert.Contains(
            "completion requires attention",
            relocation.Error,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(displacedTarget));
        Assert.Equal(
            "replacement generation",
            await File.ReadAllTextAsync(Path.Join(target, "foreign.txt")));

        var retried = await CreateService().RetryAsync(relocation.Id);

        Assert.Equal(RootFolderRelocationStatus.Completed, retried.Status);
        await using var recoveredVerification = await _factory.CreateDbContextAsync();
        var recoveredRoot = await recoveredVerification.RootFolders.AsNoTracking().SingleAsync();
        Assert.Equal(target, recoveredRoot.Path);
        Assert.Null(recoveredRoot.DirectoryObjectIdentityVersion);
        Assert.Null(recoveredRoot.DirectoryObjectIdentity);
        Assert.False(string.IsNullOrWhiteSpace(
            recoveredRoot.DirectoryObjectIdentityUnavailableReason));
        var health = await new RootFolderStorageHealthResolver(
            new DirectoryObjectIdentityResolver()).ResolveAsync(recoveredRoot);
        Assert.Equal(RootFolderStorageState.Unconfirmed, health.State);
        Assert.True(health.CanConfirmCurrentFolder);
        Assert.Equal(
            "replacement generation",
            await File.ReadAllTextAsync(Path.Join(target, "foreign.txt")));
    }

    [Fact]
    public async Task MetadataOnly_CrashAfterJournalCommit_StartupReconcilesWithoutOwnershipJournal()
    {
        var source = Path.Join(
            Path.GetTempPath(),
            $"metadata-crash-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            Path.GetTempPath(),
            $"metadata-crash-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(new Audiobook
            {
                Title = "Title",
                BasePath = Path.Join(source, "Title")
            });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var interrupted = CreateService();
        interrupted.AfterMetadataOnlyJournalCommitForTest = () =>
            throw new IOException("Injected process loss after metadata journal commit.");

        await Assert.ThrowsAsync<IOException>(() =>
            interrupted.StartAsync(
                rootId,
                new RootFolderPathChangeCommand(
                    target,
                    RootFolderRelocationMode.MetadataOnly,
                    false,
                    "Recovered Library",
                    false,
                    FileSystemCaseSensitivityMode.Auto)));

        await using (var persisted = await _factory.CreateDbContextAsync())
        {
            var pending = await persisted.RootFolderRelocations
                .Include(relocation => relocation.OwnershipPathMigrations)
                .SingleAsync();
            Assert.Equal(RootFolderRelocationStatus.Pending, pending.Status);
            Assert.Equal(rootId, pending.ActiveRootFolderId);
            Assert.Empty(pending.OwnershipPathMigrations);
            Assert.Equal(source, (await persisted.RootFolders.SingleAsync()).Path);
            Assert.Equal(source + Path.DirectorySeparatorChar + "Title",
                (await persisted.Audiobooks.SingleAsync()).BasePath);
        }

        await CreateService().ReconcileActiveAsync();

        await using var verification = await _factory.CreateDbContextAsync();
        var rootAfter = await verification.RootFolders.SingleAsync();
        var audiobookAfter = await verification.Audiobooks.SingleAsync();
        var relocationAfter = await verification.RootFolderRelocations.SingleAsync();
        Assert.Equal(target, rootAfter.Path);
        Assert.Equal("Recovered Library", rootAfter.Name);
        Assert.Equal(Path.Join(target, "Title"), audiobookAfter.BasePath);
        Assert.Equal(RootFolderRelocationStatus.Completed, relocationAfter.Status);
        Assert.Null(relocationAfter.ActiveRootFolderId);
        Assert.Equal(1, relocationAfter.CompletedJobs);
    }

    [Fact]
    public async Task MetadataOnly_CrashAfterJournalCommit_AnonymousRegistrationBoundaryBlocksStartupRewrite()
    {
        var source = Path.Join(
            Path.GetTempPath(),
            $"metadata-crash-anonymous-registration-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            Path.GetTempPath(),
            $"metadata-crash-anonymous-registration-target-{Guid.NewGuid():N}");
        var bookPath = Path.Join(source, "Title");
        Directory.CreateDirectory(bookPath);
        Directory.CreateDirectory(target);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(new Audiobook
            {
                Title = "Title",
                BasePath = bookPath
            });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var interrupted = CreateService();
        interrupted.AfterMetadataOnlyJournalCommitForTest = () =>
            throw new IOException("Injected process loss after metadata journal commit.");
        await Assert.ThrowsAsync<IOException>(() =>
            interrupted.StartAsync(
                rootId,
                new RootFolderPathChangeCommand(
                    target,
                    RootFolderRelocationMode.MetadataOnly,
                    false,
                    "Recovered Library",
                    false,
                    FileSystemCaseSensitivityMode.Auto)));

        var anonymousPublishedPath = Path.Join(bookPath, "unregistered.m4b");
        await File.WriteAllTextAsync(anonymousPublishedPath, "anonymous-audio");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.FileMutationJournals.Add(new FileMutationJournal
            {
                OperationId = Guid.NewGuid(),
                ProtocolVersion = FileMutationProtocol.Current,
                Action = FileAction.Copy,
                SourcePath = Path.Join(
                    Path.GetTempPath(),
                    $"metadata-anonymous-download-{Guid.NewGuid():N}.m4b"),
                DestinationPath = anonymousPublishedPath,
                SourceParentDirectoryObjectIdentity = "source-parent",
                DestinationParentDirectoryObjectIdentity = "destination-parent",
                SourcePhysicalObjectIdentity = "anonymous-source-generation",
                TargetPhysicalObjectIdentity = "anonymous-target-generation",
                SourceLength = new FileInfo(anonymousPublishedPath).Length,
                State = FileMutationJournalState.TargetVerified,
                AudiobookId = null,
                AudiobookFileId = null
            });
            await db.SaveChangesAsync();
        }

        var recovered = CreateService(
            fileRegistrationRecoveryProbe: new FileRegistrationRecoveryProbe(_factory));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            recovered.ReconcileActiveAsync());

        await using var verification = await _factory.CreateDbContextAsync();
        var rootAfter = await verification.RootFolders.SingleAsync();
        var audiobookAfter = await verification.Audiobooks.SingleAsync();
        var relocationAfter = await verification.RootFolderRelocations.SingleAsync();
        Assert.Equal(source, rootAfter.Path);
        Assert.Equal(bookPath, audiobookAfter.BasePath);
        Assert.Equal(RootFolderRelocationStatus.Failed, relocationAfter.Status);
        Assert.Equal(rootId, relocationAfter.ActiveRootFolderId);
        Assert.Contains(
            "file publication",
            relocationAfter.Error,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(anonymousPublishedPath));
    }

    [Fact]
    public async Task MetadataOnly_CrashAfterJournalCommit_StartupDeletionOwnerBlocksBeforeAudiobookRewrite()
    {
        var source = Path.Join(
            Path.GetTempPath(),
            $"metadata-crash-deletion-owner-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            Path.GetTempPath(),
            $"metadata-crash-deletion-owner-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        int rootId;
        int audiobookId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            var audiobook = new Audiobook
            {
                Title = "Title",
                BasePath = Path.Join(source, "Title")
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            rootId = root.Id;
            audiobookId = audiobook.Id;
        }

        var interrupted = CreateService();
        interrupted.AfterMetadataOnlyJournalCommitForTest = () =>
            throw new IOException("Injected process loss after metadata journal commit.");
        await Assert.ThrowsAsync<IOException>(() =>
            interrupted.StartAsync(
                rootId,
                new RootFolderPathChangeCommand(
                    target,
                    RootFolderRelocationMode.MetadataOnly,
                    false,
                    "Recovered Library",
                    false,
                    FileSystemCaseSensitivityMode.Auto)));

        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.AudiobookDeletionIntents.Add(new AudiobookDeletionIntent
            {
                AudiobookId = audiobookId,
                DeleteFolder = false,
                State = AudiobookDeletionIntentState.Planned
            });
            await db.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService().ReconcileActiveAsync());
        Assert.Contains("remains failed", exception.Message, StringComparison.OrdinalIgnoreCase);

        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(source, (await verification.RootFolders.SingleAsync()).Path);
        Assert.Equal(
            Path.Join(source, "Title"),
            (await verification.Audiobooks.SingleAsync()).BasePath);
        var relocation = await verification.RootFolderRelocations.SingleAsync();
        Assert.Equal(RootFolderRelocationStatus.Failed, relocation.Status);
        Assert.Contains("deletion", relocation.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            AudiobookDeletionIntentState.Planned,
            (await verification.AudiobookDeletionIntents.SingleAsync()).State);
    }

    [Fact]
    public async Task MetadataOnly_OwnershipMigrationStartup_RenameOwnerBlocksBeforeMetadataRewrite()
    {
        var scenario = await SeedPublishedOwnershipMigrationAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var audiobook = await db.Audiobooks.SingleAsync();
            await AddTrackedFileAsync(
                db,
                audiobook,
                Path.Join(scenario.OwnedPath, "book.m4b"),
                scenario.RootPath);
            var file = await db.AudiobookFiles.SingleAsync();
            db.FileMutationJournals.Add(new FileMutationJournal
            {
                Action = FileAction.Move,
                SourcePath = file.Path!,
                DestinationPath = Path.Join(scenario.OwnedPath, "renamed.m4b"),
                SourcePhysicalObjectIdentity = "test-source-generation",
                SourceLength = 5,
                State = FileMutationJournalState.Planned,
                AudiobookId = audiobook.Id,
                AudiobookFileId = file.Id
            });
            await db.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService().ReconcileActiveAsync());
        Assert.Contains("remains failed", exception.Message, StringComparison.OrdinalIgnoreCase);

        await using var verification = await _factory.CreateDbContextAsync();
        var ownership = await verification.LibraryDirectoryOwnerships.SingleAsync();
        Assert.Equal(scenario.OwnedPath, ownership.CanonicalPath);
        Assert.Equal(scenario.SourceOwnershipKey, ownership.PathOwnershipKey);
        Assert.Equal(
            scenario.OwnedPath,
            (await verification.Audiobooks.SingleAsync()).BasePath);
        var relocation = await verification.RootFolderRelocations.SingleAsync();
        Assert.Equal(RootFolderRelocationStatus.Failed, relocation.Status);
        Assert.Contains("organize", relocation.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            FileMutationJournalState.Planned,
            (await verification.FileMutationJournals.SingleAsync()).State);
    }

    [Fact]
    public async Task MetadataOnly_OwnershipMigrationRetry_RenameOwnerReturnsConflictBeforeMetadataRewrite()
    {
        var scenario = await SeedPublishedOwnershipMigrationAsync();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var audiobook = await db.Audiobooks.SingleAsync();
            await AddTrackedFileAsync(
                db,
                audiobook,
                Path.Join(scenario.OwnedPath, "book.m4b"),
                scenario.RootPath);
            var file = await db.AudiobookFiles.SingleAsync();
            db.FileMutationJournals.Add(new FileMutationJournal
            {
                Action = FileAction.Move,
                SourcePath = file.Path!,
                DestinationPath = Path.Join(scenario.OwnedPath, "renamed.m4b"),
                SourcePhysicalObjectIdentity = "test-source-generation",
                SourceLength = 5,
                State = FileMutationJournalState.Planned,
                AudiobookId = audiobook.Id,
                AudiobookFileId = file.Id
            });
            await db.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
            CreateService().RetryAsync(scenario.RelocationId));

        Assert.Equal("rename_recovery_pending", exception.Code);
        await using var verification = await _factory.CreateDbContextAsync();
        var ownership = await verification.LibraryDirectoryOwnerships.SingleAsync();
        Assert.Equal(scenario.OwnedPath, ownership.CanonicalPath);
        Assert.Equal(scenario.SourceOwnershipKey, ownership.PathOwnershipKey);
        Assert.Equal(
            scenario.OwnedPath,
            (await verification.Audiobooks.SingleAsync()).BasePath);
        var relocation = await verification.RootFolderRelocations.SingleAsync();
        Assert.Equal(RootFolderRelocationStatus.NeedsAttention, relocation.Status);
        Assert.True(await verification.LibraryDirectoryOwnershipPathMigrations.AnyAsync());
        Assert.Equal(
            FileMutationJournalState.Planned,
            (await verification.FileMutationJournals.SingleAsync()).State);
    }

    [Fact]
    public async Task MetadataOnly_CrashRecoveryTransientFilesystemFailure_RemainsPendingUntilRetry()
    {
        var source = Path.Join(
            Path.GetTempPath(),
            $"metadata-crash-recovery-failure-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            Path.GetTempPath(),
            $"metadata-crash-recovery-failure-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(new Audiobook
            {
                Title = "Title",
                BasePath = Path.Join(source, "Title")
            });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var interrupted = CreateService();
        interrupted.AfterMetadataOnlyJournalCommitForTest = () =>
            throw new IOException("Injected process loss after metadata journal commit.");
        await Assert.ThrowsAsync<IOException>(() =>
            interrupted.StartAsync(
                rootId,
                new RootFolderPathChangeCommand(
                    target,
                    RootFolderRelocationMode.MetadataOnly,
                    false,
                    "Recovered Library",
                    false,
                    FileSystemCaseSensitivityMode.Auto)));

        var recovering = CreateService();
        recovering.BeforeOwnershipMigrationMetadataSaveForTest = () =>
            throw new IOException("Injected startup metadata recovery failure.");

        await recovering.ReconcileActiveAsync();

        await using (var verification = await _factory.CreateDbContextAsync())
        {
            var relocation = await verification.RootFolderRelocations.SingleAsync();
            Assert.Equal(RootFolderRelocationStatus.Pending, relocation.Status);
            Assert.Equal(rootId, relocation.ActiveRootFolderId);
            Assert.Contains("retried", relocation.Error!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(source, (await verification.RootFolders.SingleAsync()).Path);
            Assert.Equal(
                Path.Join(source, "Title"),
                (await verification.Audiobooks.SingleAsync()).BasePath);
        }

        await CreateService().ReconcileActiveAsync();

        await using var recovered = await _factory.CreateDbContextAsync();
        Assert.Equal(target, (await recovered.RootFolders.SingleAsync()).Path);
        Assert.Equal(
            Path.Join(target, "Title"),
            (await recovered.Audiobooks.SingleAsync()).BasePath);
        Assert.Equal(
            RootFolderRelocationStatus.Completed,
            (await recovered.RootFolderRelocations.SingleAsync()).Status);
    }

    [Fact]
    public async Task MetadataOnly_FailureBeforeAtomicCommit_RetryCompletesFullMetadataRepair()
    {
        var source = Path.Join(
            Path.GetTempPath(),
            $"metadata-retry-completion-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            Path.GetTempPath(),
            $"metadata-retry-completion-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(new Audiobook
            {
                Title = "Title",
                BasePath = Path.Join(source, "Title")
            });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var interrupted = CreateService();
        interrupted.BeforeMetadataOnlyAtomicCommitForTest = () =>
            throw new IOException("Injected failure before metadata atomic commit.");

        await Assert.ThrowsAsync<IOException>(() =>
            interrupted.StartAsync(
                rootId,
                new RootFolderPathChangeCommand(
                    target,
                    RootFolderRelocationMode.MetadataOnly,
                    false,
                    "Recovered Library",
                    false,
                    FileSystemCaseSensitivityMode.Auto)));

        Guid relocationId;
        await using (var persisted = await _factory.CreateDbContextAsync())
        {
            var relocation = await persisted.RootFolderRelocations.SingleAsync();
            relocationId = relocation.Id;
            Assert.Equal(RootFolderRelocationStatus.Pending, relocation.Status);
            Assert.Contains(
                "will be retried",
                relocation.Error,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(source, (await persisted.RootFolders.SingleAsync()).Path);
        }

        var result = await CreateService().RetryAsync(relocationId);

        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(target, (await verification.RootFolders.SingleAsync()).Path);
        Assert.Equal(
            Path.Join(target, "Title"),
            (await verification.Audiobooks.SingleAsync()).BasePath);
        var completed = await verification.RootFolderRelocations.SingleAsync();
        Assert.Equal(RootFolderRelocationStatus.Completed, completed.Status);
        Assert.Null(completed.ActiveRootFolderId);
        Assert.Equal(1, completed.CompletedJobs);
    }

    [Fact]
    public async Task MetadataOnly_RequestCancelledAfterJournalCommit_CompletesAuthoritatively()
    {
        var source = Path.Join(
            Path.GetTempPath(),
            $"metadata-cancel-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            Path.GetTempPath(),
            $"metadata-cancel-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(new Audiobook
            {
                Title = "Title",
                BasePath = Path.Join(source, "Title")
            });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        using var cancellation = new CancellationTokenSource();
        var service = CreateService();
        service.AfterMetadataOnlyJournalCommitForTest = cancellation.Cancel;

        var result = await service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.MetadataOnly,
                false,
                "Metadata Library",
                false,
                FileSystemCaseSensitivityMode.Auto),
            cancellation.Token);

        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
        await using var verification = await _factory.CreateDbContextAsync();
        var rootAfter = await verification.RootFolders.SingleAsync();
        var audiobookAfter = await verification.Audiobooks.SingleAsync();
        Assert.Equal(target, rootAfter.Path);
        Assert.Equal("Metadata Library", rootAfter.Name);
        Assert.Equal(Path.Join(target, "Title"), audiobookAfter.BasePath);
        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
    }

    [Fact]
    public async Task MetadataOnly_ExternallyRenamedOwnedTree_DoesNotRequireOldSourcePathForFreshMarkerlessCleanup()
    {
        var source = Path.Join(
            TempRoot,
            $"metadata-markerless-renamed-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            TempRoot,
            $"metadata-markerless-renamed-target-{Guid.NewGuid():N}");
        var sourceOwned = Path.Join(source, "Author", "Book B012345678");
        var targetOwned = Path.Join(target, "Author", "Book B012345678");
        Directory.CreateDirectory(sourceOwned);
        await File.WriteAllTextAsync(Path.Join(sourceOwned, "01.m4b"), "audio");
        var semantics = await new FileSystemSemanticsResolver()
            .ResolveAsync(source);
        Assert.Equal(PathIdentityState.Valid, semantics.State);
        var rootObjectIdentity = await new DirectoryObjectIdentityResolver()
            .ResolveAsync(source);
        Assert.True(rootObjectIdentity.IsAvailable);
        var ownershipToken = Guid.NewGuid().ToString("N");
        string ownershipIdentity;
        using (var ownedAnchor = PinnedDirectoryCreation.OpenPinnedBoundary(sourceOwned))
        {
            ownershipIdentity = ManagedDirectoryIdentity.Create(
                ownershipToken,
                ownedAnchor.GetDirectoryObjectIdentity());
        }

        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                ResolvedCaseSensitivity = semantics.Semantics.CaseSensitivity,
                PathIdentityState = PathIdentityState.Valid,
                PathIdentityKey = FileSystemPathIdentity.CreateKey(
                    "root",
                    source,
                    semantics.Semantics),
                DirectoryObjectIdentityVersion = rootObjectIdentity.Version,
                DirectoryObjectIdentity = rootObjectIdentity.Value
            };
            var audiobook = new Audiobook
            {
                Title = "Book",
                BasePath = sourceOwned
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            rootId = root.Id;
            db.LibraryDirectoryOwnerships.Add(new LibraryDirectoryOwnership
            {
                Path = sourceOwned,
                CanonicalPath = sourceOwned,
                PathSyntax = semantics.Semantics.Syntax,
                PathCaseSensitivity = semantics.Semantics.CaseSensitivity,
                PathCaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                PathIdentityBoundary = sourceOwned,
                PathIdentityLookupKey = FileSystemPathIdentity.CreateLookupKey(
                    "library-directory",
                    sourceOwned,
                    semantics.Semantics.Syntax),
                PathOwnershipKey = FileSystemPathIdentity.CreateKey(
                    "library-directory",
                    sourceOwned,
                    semantics.Semantics),
                OwnershipToken = ownershipToken,
                State = LibraryDirectoryOwnershipState.Owned,
                CreationWorkflow = "Test",
                AudiobookId = audiobook.Id,
                ManagedRootFolderId = root.Id,
                DirectoryObjectIdentityVersion = ManagedDirectoryIdentity.CurrentVersion,
                DirectoryObjectIdentity = ownershipIdentity
            });
            await db.SaveChangesAsync();
        }

        Directory.Move(source, target);
        Assert.False(Directory.Exists(source));
        Assert.True(Directory.Exists(targetOwned));

        var result = await CreateService().StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.MetadataOnly,
                false,
                "Renamed Library",
                false,
                FileSystemCaseSensitivityMode.Auto));

        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(target, (await verification.RootFolders.SingleAsync()).Path);
        Assert.Equal(targetOwned, (await verification.Audiobooks.SingleAsync()).BasePath);
        Assert.Equal(
            targetOwned,
            (await verification.LibraryDirectoryOwnerships.SingleAsync()).CanonicalPath);
        Assert.False(await verification
            .LibraryDirectoryOwnershipPathMigrations.AnyAsync());
        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(
                target,
                "*",
                SearchOption.AllDirectories),
            path => Path.GetFileName(path).StartsWith(
                ".listenarr",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MetadataOnly_SkippedAudiobook_RetiresOwnershipInsteadOfTransferringAuthority()
    {
        var source = Path.Join(TempRoot, $"metadata-skipped-ownership-source-{Guid.NewGuid():N}");
        var target = Path.Join(TempRoot, $"metadata-skipped-ownership-target-{Guid.NewGuid():N}");
        var sourceBook = Path.Join(source, "Collision");
        Directory.CreateDirectory(sourceBook);
        var sourceSemantics = new FileSystemPathSemantics(
            FileSystemPathSemantics.CurrentHostDefault.Syntax,
            FileSystemCaseSensitivity.Sensitive);
        var ownershipToken = Guid.NewGuid().ToString("N");
        string ownershipIdentity;
        using (var ownedAnchor = PinnedDirectoryCreation.OpenPinnedBoundary(sourceBook))
        {
            ownershipIdentity = ManagedDirectoryIdentity.Create(
                ownershipToken,
                ownedAnchor.GetDirectoryObjectIdentity());
        }

        int rootId;
        int audiobookId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
                ResolvedCaseSensitivity = FileSystemCaseSensitivity.Sensitive,
                PathIdentityState = PathIdentityState.Valid,
                PathIdentityKey = FileSystemPathIdentity.CreateKey(
                    "root",
                    source,
                    sourceSemantics)
            };
            var audiobook = new Audiobook
            {
                Title = "Collision",
                BasePath = sourceBook
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            await AddTrackedFileAsync(
                db,
                audiobook,
                Path.Join(sourceBook, "book.mp3"),
                source,
                sourceSemantics,
                FileSystemCaseSensitivityMode.Sensitive);
            await AddTrackedFileAsync(
                db,
                audiobook,
                Path.Join(sourceBook, "book.MP3"),
                source,
                sourceSemantics,
                FileSystemCaseSensitivityMode.Sensitive);
            db.LibraryDirectoryOwnerships.Add(new LibraryDirectoryOwnership
            {
                Path = sourceBook,
                CanonicalPath = sourceBook,
                PathSyntax = sourceSemantics.Syntax,
                PathCaseSensitivity = sourceSemantics.CaseSensitivity,
                PathCaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
                PathIdentityBoundary = sourceBook,
                PathIdentityLookupKey = FileSystemPathIdentity.CreateLookupKey(
                    "library-directory",
                    sourceBook,
                    sourceSemantics.Syntax),
                PathOwnershipKey = FileSystemPathIdentity.CreateKey(
                    "library-directory",
                    sourceBook,
                    sourceSemantics),
                OwnershipToken = ownershipToken,
                State = LibraryDirectoryOwnershipState.Owned,
                CreationWorkflow = "Test",
                AudiobookId = audiobook.Id,
                ManagedRootFolderId = root.Id,
                DirectoryObjectIdentityVersion = ManagedDirectoryIdentity.CurrentVersion,
                DirectoryObjectIdentity = ownershipIdentity
            });
            await db.SaveChangesAsync();
            rootId = root.Id;
            audiobookId = audiobook.Id;
        }

        Directory.Move(source, target);
        var result = await CreateService().StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.MetadataOnly,
                false,
                "Moved Library",
                false,
                FileSystemCaseSensitivityMode.Insensitive));

        Assert.Equal(RootFolderRelocationStatus.NeedsAttention, result.Status);
        await using var verification = await _factory.CreateDbContextAsync();
        var audiobookAfter = await verification.Audiobooks.SingleAsync();
        Assert.Equal(audiobookId, audiobookAfter.Id);
        Assert.Equal(sourceBook, audiobookAfter.BasePath);
        var ownership = await verification.LibraryDirectoryOwnerships.SingleAsync();
        Assert.Equal(LibraryDirectoryOwnershipState.Removed, ownership.State);
        Assert.Null(ownership.PathOwnershipKey);
        Assert.Null(ownership.ManagedRootFolderId);
        Assert.Empty(await verification.LibraryDirectoryOwnershipPathMigrations.ToListAsync());
        Assert.True(Directory.Exists(Path.Join(target, "Collision")));
    }

    [ReadOnlyBindMountFact]
    public async Task MetadataOnly_RealReadOnlyBindMount_UsesDatabaseOnlyOwnershipMigration()
    {
        var rootPath = Path.GetFullPath(
            Environment.GetEnvironmentVariable(
                ReadOnlyBindMountFactAttribute.LibraryPathEnvironmentVariable)
            ?? throw new InvalidOperationException(
                "The read-only library bind mount was not provided."));
        var ownedPath = Path.Join(rootPath, "Author", "Book B012345678");
        Assert.True(Directory.Exists(ownedPath));
        var semantics = await new FileSystemSemanticsResolver()
            .ResolveAsync(rootPath);
        Assert.Equal(PathIdentityState.Valid, semantics.State);
        var rootObjectIdentity = await new DirectoryObjectIdentityResolver()
            .ResolveAsync(rootPath);
        var ownedObjectIdentity = await new DirectoryObjectIdentityResolver()
            .ResolveAsync(ownedPath);
        Assert.True(rootObjectIdentity.IsAvailable);
        Assert.True(ownedObjectIdentity.IsAvailable);
        var ownershipToken = Guid.NewGuid().ToString("N");
        using var ownedAnchor = PinnedDirectoryCreation.OpenPinnedBoundary(ownedPath);
        var ownershipIdentity = ManagedDirectoryIdentity.Create(
            ownershipToken,
            ownedAnchor.GetDirectoryObjectIdentity());

        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Read Only Library",
                Path = rootPath,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                ResolvedCaseSensitivity = semantics.Semantics.CaseSensitivity,
                PathIdentityState = PathIdentityState.Valid,
                PathIdentityKey = FileSystemPathIdentity.CreateKey(
                    "root",
                    rootPath,
                    semantics.Semantics),
                DirectoryObjectIdentityVersion = rootObjectIdentity.Version,
                DirectoryObjectIdentity = rootObjectIdentity.Value
            };
            var audiobook = new Audiobook
            {
                Title = "Book",
                BasePath = ownedPath
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            rootId = root.Id;
            db.LibraryDirectoryOwnerships.Add(new LibraryDirectoryOwnership
            {
                Path = ownedPath,
                CanonicalPath = ownedPath,
                PathSyntax = semantics.Semantics.Syntax,
                PathCaseSensitivity = semantics.Semantics.CaseSensitivity,
                PathCaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                PathIdentityBoundary = ownedPath,
                PathIdentityLookupKey = FileSystemPathIdentity.CreateLookupKey(
                    "library-directory",
                    ownedPath,
                    semantics.Semantics.Syntax),
                PathOwnershipKey = FileSystemPathIdentity.CreateKey(
                    "library-directory",
                    ownedPath,
                    semantics.Semantics),
                OwnershipToken = ownershipToken,
                State = LibraryDirectoryOwnershipState.Owned,
                CreationWorkflow = "Test",
                AudiobookId = audiobook.Id,
                ManagedRootFolderId = root.Id,
                DirectoryObjectIdentityVersion = ManagedDirectoryIdentity.CurrentVersion,
                DirectoryObjectIdentity = ownershipIdentity
            });
            await db.SaveChangesAsync();
        }

        var result = await CreateService().StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                rootPath,
                RootFolderRelocationMode.MetadataOnly,
                false,
                "Renamed Read Only Library",
                false,
                FileSystemCaseSensitivityMode.Auto));

        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(
            "Renamed Read Only Library",
            (await verification.RootFolders.SingleAsync()).Name);
        Assert.False(await verification
            .LibraryDirectoryOwnershipPathMigrations.AnyAsync());
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(
                rootPath,
                "*",
                SearchOption.AllDirectories),
            path => Path.GetFileName(path).StartsWith(
                ".listenarr",
                StringComparison.OrdinalIgnoreCase));
    }

    [ReadOnlyBindMountFact]
    public async Task Relocate_RealReadOnlySource_BlocksBeforeSagaCreation()
    {
        var source = Path.GetFullPath(
            Environment.GetEnvironmentVariable(
                ReadOnlyBindMountFactAttribute.LibraryPathEnvironmentVariable)
            ?? throw new InvalidOperationException(
                "The read-only library bind mount was not provided."));
        var target = FileService.GetTempDirectory("relocate-readonly-source-target");
        var semantics = await new FileSystemSemanticsResolver().ResolveAsync(source);
        Assert.Equal(PathIdentityState.Valid, semantics.State);
        var identity = await new DirectoryObjectIdentityResolver().ResolveAsync(source);
        Assert.True(identity.IsAvailable, identity.UnavailableReason);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Read-only Source",
                Path = source,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                ResolvedCaseSensitivity = semantics.Semantics.CaseSensitivity,
                PathIdentityState = PathIdentityState.Valid,
                PathIdentityKey = FileSystemPathIdentity.CreateKey(
                    "root",
                    source,
                    semantics.Semantics),
                DirectoryObjectIdentityVersion = identity.Version,
                DirectoryObjectIdentity = identity.Value
            };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var exception = await Assert.ThrowsAsync<RootFolderPathChangeRejectedException>(() =>
            CreateService().StartAsync(
                rootId,
                new RootFolderPathChangeCommand(
                    target,
                    RootFolderRelocationMode.Relocate,
                    true,
                    "Moved",
                    false,
                    FileSystemCaseSensitivityMode.Sensitive)));

        Assert.Equal(
            "root_folder_source_filesystem_mutation_unavailable",
            exception.Code);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.False(await verification.RootFolderRelocations.AnyAsync());
    }

    [ReadOnlyBindMountFact]
    public async Task Relocate_RealReadOnlyTarget_BlocksBeforeSagaCreation()
    {
        var target = Path.GetFullPath(
            Environment.GetEnvironmentVariable(
                ReadOnlyBindMountFactAttribute.LibraryPathEnvironmentVariable)
            ?? throw new InvalidOperationException(
                "The read-only library bind mount was not provided."));
        var source = FileService.GetTempDirectory("relocate-readonly-target-source");
        var semantics = await new FileSystemSemanticsResolver().ResolveAsync(source);
        Assert.Equal(PathIdentityState.Valid, semantics.State);
        var identity = await new DirectoryObjectIdentityResolver().ResolveAsync(source);
        Assert.True(identity.IsAvailable, identity.UnavailableReason);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Writable Source",
                Path = source,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                ResolvedCaseSensitivity = semantics.Semantics.CaseSensitivity,
                PathIdentityState = PathIdentityState.Valid,
                PathIdentityKey = FileSystemPathIdentity.CreateKey(
                    "root",
                    source,
                    semantics.Semantics),
                DirectoryObjectIdentityVersion = identity.Version,
                DirectoryObjectIdentity = identity.Value
            };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var exception = await Assert.ThrowsAsync<RootFolderPathChangeRejectedException>(() =>
            CreateService().StartAsync(
                rootId,
                new RootFolderPathChangeCommand(
                    target,
                    RootFolderRelocationMode.Relocate,
                    true,
                    "Moved",
                    false,
                    FileSystemCaseSensitivityMode.Sensitive)));

        Assert.Equal(
            "root_folder_target_filesystem_mutation_unavailable",
            exception.Code);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.False(await verification.RootFolderRelocations.AnyAsync());
    }

    [Fact]
    public async Task ReconcileOwnershipMigration_MetadataRollback_DoesNotLeakTrackedChanges()
    {
        var scenario = await SeedPublishedOwnershipMigrationAsync();

        var service = CreateService();
        service.BeforeOwnershipMigrationMetadataSaveForTest = () =>
            throw new IOException(
                "Injected failure before transactional ownership metadata save.");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReconcileActiveAsync());

        await using var verification = await _factory.CreateDbContextAsync();
        var rootAfter = await verification.RootFolders.SingleAsync();
        var ownershipAfter = await verification
            .LibraryDirectoryOwnerships.SingleAsync();
        var relocationAfter = await verification
            .RootFolderRelocations.SingleAsync();
        Assert.True(await verification
            .LibraryDirectoryOwnershipPathMigrations.AnyAsync());
        Assert.Equal("Library", rootAfter.Name);
        Assert.Equal(
            scenario.SourceOwnershipKey,
            ownershipAfter.PathOwnershipKey);
        Assert.Equal(
            FileSystemCaseSensitivityMode.Auto,
            ownershipAfter.PathCaseSensitivityMode);
        Assert.Equal(
            RootFolderRelocationStatus.Failed,
            relocationAfter.Status);
    }

    [Fact]
    public async Task RetryOwnershipMigration_MissingTargetDirectory_RetiresCleanupAuthorityAndCompletes()
    {
        var scenario = await SeedPublishedOwnershipMigrationAsync();
        Directory.Delete(scenario.OwnedPath, recursive: true);
        Assert.False(Directory.Exists(scenario.OwnedPath));

        var result = await CreateService().RetryAsync(scenario.RelocationId);

        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
        await using var verification = await _factory.CreateDbContextAsync();
        var rootAfter = await verification.RootFolders.SingleAsync();
        var ownershipAfter = await verification.LibraryDirectoryOwnerships.SingleAsync();
        var relocationAfter = await verification.RootFolderRelocations.SingleAsync();
        Assert.Equal("Renamed Library", rootAfter.Name);
        Assert.Equal(LibraryDirectoryOwnershipState.Removed, ownershipAfter.State);
        Assert.Null(ownershipAfter.PathOwnershipKey);
        Assert.Null(ownershipAfter.ManagedRootFolderId);
        Assert.Equal(RootFolderRelocationStatus.Completed, relocationAfter.Status);
        Assert.Null(relocationAfter.ActiveRootFolderId);
        Assert.False(await verification.LibraryDirectoryOwnershipPathMigrations.AnyAsync());
    }

    [Fact]
    public async Task ReconcileOwnershipMigration_TargetGenerationReplacedAtAtomicCommit_BlocksCommit()
    {
        var scenario = await SeedPublishedOwnershipMigrationAsync();
        var displacedPath = scenario.OwnedPath + ".original";
        var service = CreateService();
        service.BeforeOwnershipMigrationAtomicCommitForTest = () =>
        {
            Directory.Move(scenario.OwnedPath, displacedPath);
            Directory.CreateDirectory(scenario.OwnedPath);
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReconcileActiveAsync());

        await using var verification = await _factory.CreateDbContextAsync();
        var ownershipAfter = await verification
            .LibraryDirectoryOwnerships.SingleAsync();
        var relocationAfter = await verification
            .RootFolderRelocations.SingleAsync();
        Assert.True(await verification
            .LibraryDirectoryOwnershipPathMigrations.AnyAsync());
        Assert.Equal(
            scenario.SourceOwnershipKey,
            ownershipAfter.PathOwnershipKey);
        Assert.Equal(
            FileSystemCaseSensitivityMode.Auto,
            ownershipAfter.PathCaseSensitivityMode);
        Assert.Equal(
            RootFolderRelocationStatus.Failed,
            relocationAfter.Status);
        Assert.True(Directory.Exists(displacedPath));
        Assert.True(Directory.Exists(scenario.OwnedPath));
    }

    [Fact]
    public async Task ReconcileOwnershipMigration_TargetGenerationReplacedImmediatelyAfterCommit_MarksFailedRecovery()
    {
        var scenario = await SeedPublishedOwnershipMigrationAsync();
        var displacedPath = scenario.OwnedPath + ".post-commit-original";
        var service = CreateService();
        service.AfterOwnershipMigrationAtomicCommitForTest = () =>
        {
            Directory.Move(scenario.OwnedPath, displacedPath);
            Directory.CreateDirectory(scenario.OwnedPath);
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReconcileActiveAsync());

        await using var verification = await _factory.CreateDbContextAsync();
        var ownershipAfter = await verification
            .LibraryDirectoryOwnerships.SingleAsync();
        var relocationAfter = await verification
            .RootFolderRelocations.SingleAsync();
        Assert.False(await verification
            .LibraryDirectoryOwnershipPathMigrations.AnyAsync());
        Assert.Equal(
            scenario.TargetOwnershipKey,
            ownershipAfter.PathOwnershipKey);
        Assert.Equal(
            RootFolderRelocationStatus.Failed,
            relocationAfter.Status);
        Assert.Contains(
            "metadata-only root repair recovery is blocked",
            relocationAfter.Error,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(displacedPath));
        Assert.True(Directory.Exists(scenario.OwnedPath));
    }

    [Fact]
    public async Task ReconcileOwnershipMigration_FirstFailure_DoesNotPoisonLaterSaga()
    {
        var first = await SeedPublishedOwnershipMigrationAsync();
        var second = await SeedPublishedOwnershipMigrationAsync();
        var faultCount = 0;
        var service = CreateService();
        service.BeforeOwnershipMigrationMetadataSaveForTest = () =>
        {
            if (Interlocked.Increment(ref faultCount) == 1)
            {
                throw new IOException(
                    "Injected failure in the first ownership migration saga.");
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReconcileActiveAsync());

        await using var verification = await _factory.CreateDbContextAsync();
        var firstOwnership = await verification.LibraryDirectoryOwnerships
            .SingleAsync(candidate => candidate.Id == first.OwnershipId);
        var secondOwnership = await verification.LibraryDirectoryOwnerships
            .SingleAsync(candidate => candidate.Id == second.OwnershipId);
        var firstRelocation = await verification.RootFolderRelocations
            .SingleAsync(candidate => candidate.Id == first.RelocationId);
        var secondRelocation = await verification.RootFolderRelocations
            .SingleAsync(candidate => candidate.Id == second.RelocationId);
        Assert.Equal(
            first.SourceOwnershipKey,
            firstOwnership.PathOwnershipKey);
        Assert.Equal(
            RootFolderRelocationStatus.Failed,
            firstRelocation.Status);
        Assert.Equal(
            second.TargetOwnershipKey,
            secondOwnership.PathOwnershipKey);
        Assert.Equal(
            RootFolderRelocationStatus.Completed,
            secondRelocation.Status);
        Assert.True(await verification
            .LibraryDirectoryOwnershipPathMigrations
            .AnyAsync(candidate =>
                candidate.RelocationId == first.RelocationId));
        Assert.False(await verification
            .LibraryDirectoryOwnershipPathMigrations
            .AnyAsync(candidate =>
                candidate.RelocationId == second.RelocationId));
    }

    [DirectoryLinkFact]
    public async Task MetadataOnly_LinkedSourceAndPhysicalTarget_PreservesPhysicalIdentityWithoutSidecars()
    {
        var root = Path.Join(
            TempRoot,
            $"ownership-link-alias-{Guid.NewGuid():N}");
        var physicalRoot = Path.Join(root, "physical");
        var linkedRoot = Path.Join(root, "linked");
        var physicalOwnedPath = Path.Join(physicalRoot, "Book");
        var linkedOwnedPath = Path.Join(linkedRoot, "Book");
        Directory.CreateDirectory(physicalOwnedPath);
        Directory.CreateSymbolicLink(linkedRoot, physicalRoot);

        try
        {
            var sourceResolution = await new FileSystemSemanticsResolver()
                .ResolveAsync(linkedRoot);
            var targetResolution = await new FileSystemSemanticsResolver()
                .ResolveAsync(physicalRoot);
            Assert.Equal(PathIdentityState.Valid, sourceResolution.State);
            Assert.Equal(PathIdentityState.Valid, targetResolution.State);
            var rootIdentity = await new DirectoryObjectIdentityResolver()
                .ResolveAsync(linkedRoot);
            var ownedIdentity = await new DirectoryObjectIdentityResolver()
                .ResolveAsync(linkedOwnedPath);
            Assert.True(rootIdentity.IsAvailable);
            Assert.True(ownedIdentity.IsAvailable);
            string ownedNativeIdentity;
            using (var ownedAnchor =
                PinnedDirectoryCreation.OpenPinnedBoundary(linkedOwnedPath))
            {
                ownedNativeIdentity = ownedAnchor.GetDirectoryObjectIdentity();
            }
            var ownershipToken = Guid.NewGuid().ToString("N");

            int rootId;
            LibraryDirectoryOwnership ownership;
            await using (var db = await _factory.CreateDbContextAsync())
            {
                var rootFolder = new RootFolder
                {
                    Name = "Linked Library",
                    Path = linkedRoot,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                    ResolvedCaseSensitivity =
                        sourceResolution.Semantics.CaseSensitivity,
                    PathIdentityState = PathIdentityState.Valid,
                    PathIdentityKey = FileSystemPathIdentity.CreateKey(
                        "root",
                        linkedRoot,
                        sourceResolution.Semantics),
                    DirectoryObjectIdentityVersion = rootIdentity.Version,
                    DirectoryObjectIdentity = rootIdentity.Value
                };
                var audiobook = new Audiobook
                {
                    Title = "Book",
                    BasePath = linkedOwnedPath
                };
                db.RootFolders.Add(rootFolder);
                db.Audiobooks.Add(audiobook);
                await db.SaveChangesAsync();
                rootId = rootFolder.Id;

                ownership = new LibraryDirectoryOwnership
                {
                    Path = linkedOwnedPath,
                    CanonicalPath = linkedOwnedPath,
                    PathSyntax = sourceResolution.Semantics.Syntax,
                    PathCaseSensitivity =
                        sourceResolution.Semantics.CaseSensitivity,
                    PathCaseSensitivityMode =
                        FileSystemCaseSensitivityMode.Auto,
                    PathIdentityBoundary = linkedOwnedPath,
                    PathIdentityLookupKey =
                        FileSystemPathIdentity.CreateLookupKey(
                            "library-directory",
                            linkedOwnedPath,
                            sourceResolution.Semantics.Syntax),
                    PathOwnershipKey = FileSystemPathIdentity.CreateKey(
                        "library-directory",
                        linkedOwnedPath,
                        sourceResolution.Semantics),
                    OwnershipToken = ownershipToken,
                    State = LibraryDirectoryOwnershipState.Owned,
                    CreationWorkflow = "Test",
                    AudiobookId = audiobook.Id,
                    ManagedRootFolderId = rootFolder.Id,
                    DirectoryObjectIdentityVersion = ManagedDirectoryIdentity.CurrentVersion,
                    DirectoryObjectIdentity = ManagedDirectoryIdentity.Create(
                        ownershipToken,
                        ownedNativeIdentity)
                };
                db.LibraryDirectoryOwnerships.Add(ownership);
                await db.SaveChangesAsync();
            }

            var result = await CreateService().StartAsync(
                rootId,
                new RootFolderPathChangeCommand(
                    physicalRoot,
                    RootFolderRelocationMode.MetadataOnly,
                    false,
                    "Physical Library",
                    false,
                    FileSystemCaseSensitivityMode.Auto));

            Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
            await using var verification =
                await _factory.CreateDbContextAsync();
            var rootAfter = await verification.RootFolders.SingleAsync();
            var ownershipAfter = await verification
                .LibraryDirectoryOwnerships.SingleAsync();
            Assert.Equal(physicalRoot, rootAfter.Path);
            Assert.Equal(physicalOwnedPath, ownershipAfter.CanonicalPath);
            Assert.True(ManagedDirectoryIdentity.Matches(
                ownershipAfter.DirectoryObjectIdentityVersion,
                ownershipAfter.DirectoryObjectIdentity,
                ownershipAfter.OwnershipToken,
                ownedNativeIdentity));
            Assert.False(await verification
                .LibraryDirectoryOwnershipPathMigrations.AnyAsync());
        }
        finally
        {
            if (Directory.Exists(linkedRoot)
                && (File.GetAttributes(linkedRoot)
                    & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(linkedRoot);
            }
        }
    }

    [DirectoryLinkFact]
    public async Task MetadataOnly_PhysicalSourceAndLinkedTarget_PreservesPhysicalIdentityWithoutSidecars()
    {
        var root = Path.Join(
            TempRoot,
            $"ownership-link-target-alias-{Guid.NewGuid():N}");
        var physicalRoot = Path.Join(root, "physical");
        var linkedRoot = Path.Join(root, "linked");
        var physicalOwnedPath = Path.Join(physicalRoot, "Book");
        var linkedOwnedPath = Path.Join(linkedRoot, "Book");
        Directory.CreateDirectory(physicalOwnedPath);
        Directory.CreateSymbolicLink(linkedRoot, physicalRoot);

        try
        {
            var sourceResolution = await new FileSystemSemanticsResolver()
                .ResolveAsync(physicalRoot);
            var targetResolution = await new FileSystemSemanticsResolver()
                .ResolveAsync(linkedRoot);
            Assert.Equal(PathIdentityState.Valid, sourceResolution.State);
            Assert.Equal(PathIdentityState.Valid, targetResolution.State);
            var rootIdentity = await new DirectoryObjectIdentityResolver()
                .ResolveAsync(physicalRoot);
            var ownedIdentity = await new DirectoryObjectIdentityResolver()
                .ResolveAsync(physicalOwnedPath);
            Assert.True(rootIdentity.IsAvailable);
            Assert.True(ownedIdentity.IsAvailable);
            string ownedNativeIdentity;
            using (var ownedAnchor =
                PinnedDirectoryCreation.OpenPinnedBoundary(physicalOwnedPath))
            {
                ownedNativeIdentity = ownedAnchor.GetDirectoryObjectIdentity();
            }
            var ownershipToken = Guid.NewGuid().ToString("N");

            int rootId;
            LibraryDirectoryOwnership ownership;
            await using (var db = await _factory.CreateDbContextAsync())
            {
                var rootFolder = new RootFolder
                {
                    Name = "Physical Library",
                    Path = physicalRoot,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                    ResolvedCaseSensitivity =
                        sourceResolution.Semantics.CaseSensitivity,
                    PathIdentityState = PathIdentityState.Valid,
                    PathIdentityKey = FileSystemPathIdentity.CreateKey(
                        "root",
                        physicalRoot,
                        sourceResolution.Semantics),
                    DirectoryObjectIdentityVersion = rootIdentity.Version,
                    DirectoryObjectIdentity = rootIdentity.Value
                };
                var audiobook = new Audiobook
                {
                    Title = "Book",
                    BasePath = physicalOwnedPath
                };
                db.RootFolders.Add(rootFolder);
                db.Audiobooks.Add(audiobook);
                await db.SaveChangesAsync();
                rootId = rootFolder.Id;

                ownership = new LibraryDirectoryOwnership
                {
                    Path = physicalOwnedPath,
                    CanonicalPath = physicalOwnedPath,
                    PathSyntax = sourceResolution.Semantics.Syntax,
                    PathCaseSensitivity =
                        sourceResolution.Semantics.CaseSensitivity,
                    PathCaseSensitivityMode =
                        FileSystemCaseSensitivityMode.Auto,
                    PathIdentityBoundary = physicalOwnedPath,
                    PathIdentityLookupKey =
                        FileSystemPathIdentity.CreateLookupKey(
                            "library-directory",
                            physicalOwnedPath,
                            sourceResolution.Semantics.Syntax),
                    PathOwnershipKey = FileSystemPathIdentity.CreateKey(
                        "library-directory",
                        physicalOwnedPath,
                        sourceResolution.Semantics),
                    OwnershipToken = ownershipToken,
                    State = LibraryDirectoryOwnershipState.Owned,
                    CreationWorkflow = "Test",
                    AudiobookId = audiobook.Id,
                    ManagedRootFolderId = rootFolder.Id,
                    DirectoryObjectIdentityVersion = ManagedDirectoryIdentity.CurrentVersion,
                    DirectoryObjectIdentity = ManagedDirectoryIdentity.Create(
                        ownershipToken,
                        ownedNativeIdentity)
                };
                db.LibraryDirectoryOwnerships.Add(ownership);
                await db.SaveChangesAsync();
            }

            var result = await CreateService().StartAsync(
                rootId,
                new RootFolderPathChangeCommand(
                    linkedRoot,
                    RootFolderRelocationMode.MetadataOnly,
                    false,
                    "Linked Library",
                    false,
                    FileSystemCaseSensitivityMode.Auto));

            Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
            await using var verification =
                await _factory.CreateDbContextAsync();
            var rootAfter = await verification.RootFolders.SingleAsync();
            var ownershipAfter = await verification
                .LibraryDirectoryOwnerships.SingleAsync();
            Assert.Equal(linkedRoot, rootAfter.Path);
            Assert.Equal(linkedOwnedPath, ownershipAfter.CanonicalPath);
            Assert.True(ManagedDirectoryIdentity.Matches(
                ownershipAfter.DirectoryObjectIdentityVersion,
                ownershipAfter.DirectoryObjectIdentity,
                ownershipAfter.OwnershipToken,
                ownedNativeIdentity));
            Assert.False(await verification
                .LibraryDirectoryOwnershipPathMigrations.AnyAsync());
        }
        finally
        {
            if (Directory.Exists(linkedRoot)
                && (File.GetAttributes(linkedRoot)
                    & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(linkedRoot);
            }
        }
    }

    [Fact]
    public async Task RelocateClassification_UsesPersistedSourceSemanticsForCaseOnlyPathChange()
    {
        var parent = Path.Join(Path.GetTempPath(), $"persisted-semantics-{Guid.NewGuid():N}");
        var source = Path.Join(parent, "Library");
        var target = Path.Join(parent, "library");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive
            };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var result = await CreateService().StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                false,
                "Library",
                false,
                FileSystemCaseSensitivityMode.Insensitive));

        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(target, (await verification.RootFolders.SingleAsync()).Path);
        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
        var relocation = await verification.RootFolderRelocations.SingleAsync();
        Assert.Equal(RootFolderRelocationMode.Relocate, relocation.Mode);
        Assert.Equal(RootFolderRelocationStatus.Completed, relocation.Status);
        Assert.Equal(source, relocation.SourcePath);
        Assert.Equal(target, relocation.TargetPath);
    }

    [Fact]
    public async Task MetadataOnlyAffectedDiscovery_UsesPersistedSensitiveSemanticsWhenProbeIsInsensitive()
    {
        var parent = Path.Join(Path.GetTempPath(), $"persisted-discovery-{Guid.NewGuid():N}");
        var source = Path.Join(parent, "Library");
        var target = Path.Join(parent, "Moved");
        var affectedBasePath = Path.Join(source, "Book");
        var caseVariantBasePath = Path.Join(parent, "library", "Unrelated");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                ResolvedCaseSensitivity = FileSystemCaseSensitivity.Sensitive,
                PathIdentityState = PathIdentityState.Valid
            };
            db.RootFolders.Add(root);
            db.Audiobooks.AddRange(
                new Audiobook
                {
                    Title = "Affected",
                    BasePath = affectedBasePath
                },
                new Audiobook
                {
                    Title = "Case Variant",
                    BasePath = caseVariantBasePath
                });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var semanticsResolver = new Mock<IFileSystemSemanticsResolver>();
        semanticsResolver.Setup(resolver => resolver.ResolveAsync(
                It.IsAny<string>(),
                It.IsAny<FileSystemCaseSensitivityMode>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, FileSystemCaseSensitivityMode, CancellationToken>((path, _, _) =>
                ValueTask.FromResult(new FileSystemSemanticsResolution(
                    new FileSystemPathSemantics(
                        FileSystemPathSemantics.CurrentHostDefault.Syntax,
                        FileSystemCaseSensitivity.Insensitive),
                    PathIdentityState.Valid,
                    Path.GetPathRoot(path) ?? path)));

        var result = await CreateService(semanticsResolver: semanticsResolver.Object).StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.MetadataOnly,
                false,
                "Moved Library",
                false,
                FileSystemCaseSensitivityMode.Auto));

        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(
            Path.Join(target, "Book"),
            (await verification.Audiobooks.SingleAsync(audiobook => audiobook.Title == "Affected")).BasePath);
        Assert.Equal(
            caseVariantBasePath,
            (await verification.Audiobooks.SingleAsync(audiobook => audiobook.Title == "Case Variant")).BasePath);
    }

    [Theory]
    [InlineData(RootFolderRelocationMode.MetadataOnly)]
    [InlineData(RootFolderRelocationMode.Relocate)]
    public async Task CaseVariantsCollapseOnInsensitiveTarget_UsesModeSpecificSafetyContract(
        RootFolderRelocationMode mode)
    {
        var source = Path.Join(Path.GetTempPath(), $"metadata-case-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"metadata-case-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        int rootId;
        var upperBasePath = Path.Join(source, "Book");
        var lowerBasePath = Path.Join(source, "book");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive
            };
            var upperAudiobook = new Audiobook
            {
                Title = "Upper",
                BasePath = upperBasePath
            };
            var lowerAudiobook = new Audiobook
            {
                Title = "Lower",
                BasePath = lowerBasePath
            };
            db.RootFolders.Add(root);
            db.Audiobooks.AddRange(upperAudiobook, lowerAudiobook);
            await db.SaveChangesAsync();
            var sensitiveSemantics = new FileSystemPathSemantics(
                FileSystemPathSemantics.CurrentHostDefault.Syntax,
                FileSystemCaseSensitivity.Sensitive);
            await AddTrackedFileAsync(
                db,
                upperAudiobook,
                Path.Join(upperBasePath, "book.m4b"),
                source,
                sensitiveSemantics,
                FileSystemCaseSensitivityMode.Sensitive);
            await AddTrackedFileAsync(
                db,
                lowerAudiobook,
                Path.Join(lowerBasePath, "book.m4b"),
                source,
                sensitiveSemantics,
                FileSystemCaseSensitivityMode.Sensitive);
            rootId = root.Id;
        }

        if (mode == RootFolderRelocationMode.Relocate)
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CreateService().StartAsync(
                    rootId,
                    new RootFolderPathChangeCommand(
                        target,
                        mode,
                        false,
                        "Moved Library",
                        false,
                        FileSystemCaseSensitivityMode.Insensitive)));
            Assert.Contains("same target path", exception.Message, StringComparison.OrdinalIgnoreCase);

            await using var relocateVerification = await _factory.CreateDbContextAsync();
            Assert.Equal(source, (await relocateVerification.RootFolders.SingleAsync()).Path);
            Assert.Empty(await relocateVerification.RootFolderRelocations.ToListAsync());
        }
        else
        {
            var result = await CreateService().StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                mode,
                false,
                "Moved Library",
                false,
                FileSystemCaseSensitivityMode.Insensitive));

            Assert.Equal(RootFolderRelocationStatus.NeedsAttention, result.Status);
            Assert.Equal(2, result.TotalJobs);
            Assert.Equal(0, result.CompletedJobs);
            await using var verification = await _factory.CreateDbContextAsync();
            Assert.Equal(target, (await verification.RootFolders.SingleAsync()).Path);
            var audiobooks = await verification.Audiobooks.OrderBy(audiobook => audiobook.Title).ToListAsync();
            Assert.Equal(lowerBasePath, audiobooks[0].BasePath);
            Assert.Equal(upperBasePath, audiobooks[1].BasePath);
            var relocation = await verification.RootFolderRelocations
                .Include(candidate => candidate.SkippedItems)
                .SingleAsync();
            Assert.Equal(2, relocation.SkippedItems.Count);
            Assert.All(
                relocation.SkippedItems,
                item => Assert.Contains(
                    "same filesystem identity",
                    item.Reason,
                    StringComparison.OrdinalIgnoreCase));
            Assert.Empty(await verification.MoveJobs.ToListAsync());
        }
    }

    [Fact]
    public async Task MetadataOnly_CaseDistinctFilesWithinOneAudiobook_SkipsOnlyThatAudiobook()
    {
        var source = Path.Join(Path.GetTempPath(), $"metadata-file-case-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"metadata-file-case-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        int rootId;
        int collidingAudiobookId;
        int safeAudiobookId;
        var collidingBasePath = Path.Join(source, "Collision");
        var safeBasePath = Path.Join(source, "Safe");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive
            };
            var colliding = new Audiobook
            {
                Title = "Collision",
                BasePath = collidingBasePath
            };
            var safe = new Audiobook
            {
                Title = "Safe",
                BasePath = safeBasePath
            };
            db.RootFolders.Add(root);
            db.Audiobooks.AddRange(colliding, safe);
            await db.SaveChangesAsync();
            var sensitiveSemantics = new FileSystemPathSemantics(
                FileSystemPathSemantics.CurrentHostDefault.Syntax,
                FileSystemCaseSensitivity.Sensitive);
            await AddTrackedFileAsync(
                db,
                colliding,
                Path.Join(collidingBasePath, "book.mp3"),
                source,
                sensitiveSemantics,
                FileSystemCaseSensitivityMode.Sensitive);
            await AddTrackedFileAsync(
                db,
                colliding,
                Path.Join(collidingBasePath, "book.MP3"),
                source,
                sensitiveSemantics,
                FileSystemCaseSensitivityMode.Sensitive);
            await AddTrackedFileAsync(
                db,
                safe,
                Path.Join(safeBasePath, "safe.m4b"),
                source,
                sensitiveSemantics,
                FileSystemCaseSensitivityMode.Sensitive);
            rootId = root.Id;
            collidingAudiobookId = colliding.Id;
            safeAudiobookId = safe.Id;
        }

        var service = CreateService();
        var result = await service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.MetadataOnly,
                false,
                "Moved Library",
                false,
                FileSystemCaseSensitivityMode.Insensitive));

        Assert.Equal(RootFolderRelocationStatus.NeedsAttention, result.Status);
        Assert.Equal(2, result.TotalJobs);
        Assert.Equal(1, result.CompletedJobs);
        Assert.Equal([collidingAudiobookId], result.SkippedAudiobookIds);
        var persistedResult = await service.GetAsync(result.RelocationId!.Value);
        Assert.Equal([collidingAudiobookId], persistedResult!.SkippedAudiobookIds);
        Assert.True(await service.IsAudiobookPathStateProtectedAsync(collidingAudiobookId));
        Assert.False(await service.IsAudiobookPathStateProtectedAsync(safeAudiobookId));
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(target, (await verification.RootFolders.SingleAsync()).Path);
        var collidingAfter = await verification.Audiobooks
            .Include(audiobook => audiobook.Files)
            .SingleAsync(audiobook => audiobook.Id == collidingAudiobookId);
        Assert.Equal(collidingBasePath, collidingAfter.BasePath);
        Assert.Contains(
            collidingAfter.Files!,
            file => file.Path == Path.Join(collidingBasePath, "book.mp3"));
        Assert.Contains(
            collidingAfter.Files!,
            file => file.Path == Path.Join(collidingBasePath, "book.MP3"));
        Assert.All(
            collidingAfter.Files!,
            file => Assert.False(string.IsNullOrWhiteSpace(file.PhysicalObjectIdentity)));
        var safeAfter = await verification.Audiobooks
            .Include(audiobook => audiobook.Files)
            .SingleAsync(audiobook => audiobook.Title == "Safe");
        Assert.Equal(Path.Join(target, "Safe"), safeAfter.BasePath);
        var safeFile = Assert.Single(safeAfter.Files!);
        Assert.Equal(
            Path.Join(target, "Safe", "safe.m4b"),
            safeFile.Path);
        Assert.Null(safeFile.PhysicalObjectIdentity);
        var relocation = await verification.RootFolderRelocations
            .Include(candidate => candidate.SkippedItems)
            .SingleAsync();
        var skipped = Assert.Single(relocation.SkippedItems);
        Assert.Equal(collidingAudiobookId, skipped.AudiobookId);
        Assert.Contains(
            "same filesystem identity",
            skipped.Reason,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(await service.IsBoundaryProtectedAsync(
            source,
            new FileSystemPathSemantics(
                FileSystemPathSemantics.CurrentHostDefault.Syntax,
                FileSystemCaseSensitivity.Sensitive)));
        Assert.False(await service.IsBoundaryProtectedAsync(
            target,
            new FileSystemPathSemantics(
                FileSystemPathSemantics.CurrentHostDefault.Syntax,
                FileSystemCaseSensitivity.Insensitive)));
    }

    [Fact]
    public async Task AbandonUnpublished_PhysicalPrecommitCrash_ReleasesRootWithoutTouchingAudiobookOrForeignTargetContent()
    {
        var (rootId, audiobookId, source, target) = await SeedRelocationScenarioAsync();
        Directory.Delete(target, recursive: true);
        var sourceFile = Path.Join(source, "Author", "Title", "book.m4b");
        var interrupted = CreateService();
        var injected = false;
        interrupted.AfterTargetReservationStatePersistedForTest = _ =>
        {
            if (injected)
            {
                return;
            }

            injected = true;
            throw new IOException(
                "Injected interruption after target reservation publication.");
        };

        await Assert.ThrowsAsync<IOException>(() =>
            interrupted.StartAsync(rootId, BuildRelocationCommand(target)));
        Assert.True(injected);

        Guid relocationId;
        await using (var persisted = await _factory.CreateDbContextAsync())
        {
            var relocation = await persisted.RootFolderRelocations
                .Include(candidate => candidate.MoveJobs)
                .SingleAsync();
            relocationId = relocation.Id;
            Assert.Equal(1, relocation.TotalJobs);
            Assert.Empty(relocation.MoveJobs);
            Assert.Equal(RootFolderRelocationStatus.NeedsAttention, relocation.Status);
            Assert.Equal(rootId, relocation.ActiveRootFolderId);
        }
        var inspectable = await CreateService().GetAsync(relocationId);
        Assert.True(Assert.IsType<RootFolderPathChangeResult>(inspectable).CanAbandon);
        Assert.True(await CreateService().IsAudiobookPathStateProtectedAsync(audiobookId));
        Assert.True(File.Exists(sourceFile));
        Assert.True(Directory.Exists(target));
        var foreignFile = Path.Join(target, "foreign.txt");
        await File.WriteAllTextAsync(foreignFile, "foreign content");

        var abandoned = await CreateService().AbandonUnpublishedAsync(relocationId);

        Assert.Equal(RootFolderRelocationStatus.Failed, abandoned.Status);
        Assert.False(abandoned.CanAbandon);
        Assert.True(File.Exists(sourceFile));
        Assert.Equal("audio", await File.ReadAllTextAsync(sourceFile));
        Assert.Equal("foreign content", await File.ReadAllTextAsync(foreignFile));
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(source, (await verification.RootFolders.SingleAsync()).Path);
        Assert.Equal(
            Path.Join(source, "Author", "Title"),
            (await verification.Audiobooks.SingleAsync()).BasePath);
        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
        Assert.Empty(await verification.RootFolderRelocationCreatedDirectories.ToListAsync());
        Assert.False(await CreateService().IsAudiobookPathStateProtectedAsync(audiobookId));
    }

    [Fact]
    public async Task AbandonUnpublished_PublishedMoveJob_IsRejected()
    {
        var (rootId, _, _, target) = await SeedRelocationScenarioAsync();
        var service = CreateService();
        var started = await service.StartAsync(
            rootId,
            BuildRelocationCommand(target));
        var relocationId = started.RelocationId!.Value;
        await using (var persisted = await _factory.CreateDbContextAsync())
        {
            Assert.Single(await persisted.MoveJobs
                .Where(job => job.RelocationId == relocationId)
                .ToListAsync());
        }
        var current = await service.GetAsync(relocationId);
        Assert.False(Assert.IsType<RootFolderPathChangeResult>(current).CanAbandon);

        var conflict = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
            service.AbandonUnpublishedAsync(relocationId));

        Assert.Equal("root_folder_relocation_cannot_abandon", conflict.Code);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.NotNull(await verification.RootFolderRelocations
            .SingleOrDefaultAsync(candidate => candidate.Id == relocationId));
        Assert.Single(await verification.MoveJobs
            .Where(job => job.RelocationId == relocationId)
            .ToListAsync());
    }

    [Fact]
    public async Task ActivePhysicalRelocation_TrackedFileWithoutBasePath_RemainsProtected()
    {
        var source = Path.Join(
            Path.GetTempPath(),
            $"physical-tracked-protection-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            Path.GetTempPath(),
            $"physical-tracked-protection-target-{Guid.NewGuid():N}");
        var filePath = Path.Join(source, "Protected", "book.m4b");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "audio");
        int audiobookId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
                ResolvedCaseSensitivity = FileSystemCaseSensitivity.Sensitive,
                PathIdentityState = PathIdentityState.Valid
            };
            var audiobook = new Audiobook
            {
                Title = "Protected Tracked File",
                BasePath = null,
                FilePath = null,
                Files =
                [
                    AudiobookFile.CreateUnresolved(filePath)
                ]
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            db.RootFolderRelocations.Add(new RootFolderRelocation
            {
                RootFolderId = root.Id,
                ActiveRootFolderId = root.Id,
                SourcePath = source,
                SourceCaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
                TargetPath = target,
                TargetCaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
                Mode = RootFolderRelocationMode.Relocate,
                Status = RootFolderRelocationStatus.NeedsAttention,
                DesiredName = root.Name,
                TotalJobs = 1,
                Error = "Move jobs were not published before interruption."
            });
            await db.SaveChangesAsync();
            audiobookId = audiobook.Id;
        }

        Assert.True(await CreateService().IsAudiobookPathStateProtectedAsync(audiobookId));
    }

    [Fact]
    public async Task ActivePhysicalRelocation_LegacyFilePathWithoutBasePath_RemainsProtected()
    {
        var source = Path.Join(
            Path.GetTempPath(),
            $"physical-legacy-protection-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            Path.GetTempPath(),
            $"physical-legacy-protection-target-{Guid.NewGuid():N}");
        var filePath = Path.Join(source, "Protected", "book.m4b");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "audio");
        int audiobookId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
                ResolvedCaseSensitivity = FileSystemCaseSensitivity.Sensitive,
                PathIdentityState = PathIdentityState.Valid
            };
            var audiobook = new Audiobook
            {
                Title = "Protected Legacy FilePath",
                BasePath = null,
                FilePath = filePath
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            db.RootFolderRelocations.Add(new RootFolderRelocation
            {
                RootFolderId = root.Id,
                ActiveRootFolderId = root.Id,
                SourcePath = source,
                SourceCaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
                TargetPath = target,
                TargetCaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
                Mode = RootFolderRelocationMode.Relocate,
                Status = RootFolderRelocationStatus.NeedsAttention,
                DesiredName = root.Name,
                TotalJobs = 1,
                Error = "Move jobs were not published before interruption."
            });
            await db.SaveChangesAsync();
            audiobookId = audiobook.Id;
        }

        Assert.True(await CreateService().IsAudiobookPathStateProtectedAsync(audiobookId));
    }

    [WindowsFact]
    public async Task ActivePhysicalRelocation_DeviceAliasSourceAudiobook_RemainsProtected()
    {
        var source = Path.Join(
            Path.GetTempPath(),
            $"physical-device-protection-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            Path.GetTempPath(),
            $"physical-device-protection-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        int audiobookId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive,
                ResolvedCaseSensitivity = FileSystemCaseSensitivity.Insensitive,
                PathIdentityState = PathIdentityState.Valid
            };
            var audiobook = new Audiobook
            {
                Title = "Protected Device Alias",
                BasePath = @"\\?\" + Path.Join(source, "Protected")
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            db.RootFolderRelocations.Add(new RootFolderRelocation
            {
                RootFolderId = root.Id,
                ActiveRootFolderId = root.Id,
                SourcePath = source,
                SourceCaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive,
                TargetPath = target,
                TargetCaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive,
                Mode = RootFolderRelocationMode.Relocate,
                Status = RootFolderRelocationStatus.NeedsAttention,
                DesiredName = root.Name,
                TotalJobs = 1,
                Error = "Move jobs were not published before interruption."
            });
            await db.SaveChangesAsync();
            audiobookId = audiobook.Id;
        }

        Assert.True(await CreateService().IsAudiobookPathStateProtectedAsync(audiobookId));
    }

    [Fact]
    public async Task ActivePhysicalRelocationWithoutPublishedMoveJobs_ProtectsSourceSideAudiobookOnly()
    {
        var source = Path.Join(
            Path.GetTempPath(),
            $"physical-precommit-protection-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            Path.GetTempPath(),
            $"physical-precommit-protection-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        int audiobookId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
                ResolvedCaseSensitivity = FileSystemCaseSensitivity.Sensitive,
                PathIdentityState = PathIdentityState.Valid
            };
            var audiobook = new Audiobook
            {
                Title = "Protected",
                BasePath = Path.Join(source, "Protected")
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            db.RootFolderRelocations.Add(new RootFolderRelocation
            {
                RootFolderId = root.Id,
                ActiveRootFolderId = root.Id,
                SourcePath = source,
                SourceCaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
                TargetPath = target,
                TargetCaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
                Mode = RootFolderRelocationMode.Relocate,
                Status = RootFolderRelocationStatus.NeedsAttention,
                DesiredName = root.Name,
                TotalJobs = 1,
                Error = "Move jobs were not published before interruption."
            });
            await db.SaveChangesAsync();
            audiobookId = audiobook.Id;
        }

        var service = CreateService();
        Assert.True(await service.IsAudiobookPathStateProtectedAsync(audiobookId));

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var audiobook = await db.Audiobooks.SingleAsync(candidate => candidate.Id == audiobookId);
            audiobook.BasePath = Path.Join(target, "Protected");
            await db.SaveChangesAsync();
        }

        Assert.False(await service.IsAudiobookPathStateProtectedAsync(audiobookId));
    }

    [Fact]
    public async Task MetadataRepair_CompletedImportJournalWithoutFileOwner_DoesNotBlockCollisionRepair()
    {
        var source = Path.Join(
            Path.GetTempPath(),
            $"metadata-completed-import-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            Path.GetTempPath(),
            $"metadata-completed-import-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        var audiobookBasePath = Path.Join(source, "Collision");
        int rootId;
        int audiobookId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive
            };
            var audiobook = new Audiobook
            {
                Title = "Collision",
                BasePath = audiobookBasePath
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            var sensitiveSemantics = new FileSystemPathSemantics(
                FileSystemPathSemantics.CurrentHostDefault.Syntax,
                FileSystemCaseSensitivity.Sensitive);
            await AddTrackedFileAsync(
                db,
                audiobook,
                Path.Join(audiobookBasePath, "book.mp3"),
                source,
                sensitiveSemantics,
                FileSystemCaseSensitivityMode.Sensitive);
            await AddTrackedFileAsync(
                db,
                audiobook,
                Path.Join(audiobookBasePath, "book.MP3"),
                source,
                sensitiveSemantics,
                FileSystemCaseSensitivityMode.Sensitive);
            rootId = root.Id;
            audiobookId = audiobook.Id;
        }

        var service = CreateService();
        var result = await service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.MetadataOnly,
                false,
                "Moved Library",
                false,
                FileSystemCaseSensitivityMode.Insensitive));
        var repair = Assert.IsType<RootFolderMetadataRepairDetails>(
            await service.GetSkippedMetadataRepairDetailsAsync(
                result.RelocationId!.Value,
                audiobookId));
        var duplicate = Assert.Single(repair.CollisionGroups)
            .Files
            .OrderBy(file => file.AudiobookFileId)
            .Last();
        var completedImportOperationId = Guid.NewGuid();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.FileMutationJournals.Add(new FileMutationJournal
            {
                OperationId = completedImportOperationId,
                Action = FileAction.Move,
                SourcePath = Path.Join(source, "download", "book.mp3"),
                DestinationPath = Path.Join(audiobookBasePath, "book.mp3"),
                SourcePhysicalObjectIdentity = "completed-import-source-generation",
                SourceLength = 1,
                State = FileMutationJournalState.Completed,
                AudiobookId = audiobookId,
                AudiobookFileId = null,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var afterRemoval = await service.RemoveSkippedMetadataRepairFileAsync(
            result.RelocationId.Value,
            audiobookId,
            duplicate.AudiobookFileId);

        Assert.Empty(afterRemoval.CollisionGroups);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.True(await verification.FileMutationJournals
            .AnyAsync(journal =>
                journal.OperationId == completedImportOperationId
                && journal.State == FileMutationJournalState.Completed
                && journal.AudiobookFileId == null));
        Assert.Single(await verification.AudiobookFiles
            .Where(file => file.AudiobookId == audiobookId)
            .ToListAsync());
    }

    [Fact]
    public async Task MetadataOnly_TargetIdentityOwnedByUnrelatedFile_SkipsCandidateAudiobook()
    {
        var source = Path.Join(Path.GetTempPath(), $"metadata-owned-target-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"metadata-owned-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        var candidateBasePath = Path.Join(source, "Candidate");
        var candidateSourceFile = Path.Join(candidateBasePath, "book.m4b");
        var occupiedTargetFile = Path.Join(target, "Candidate", "book.m4b");
        int rootId;
        int candidateAudiobookId;
        int unrelatedAudiobookId;
        int occupiedFileId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive
            };
            var candidate = new Audiobook
            {
                Title = "Candidate",
                BasePath = candidateBasePath
            };
            var unrelated = new Audiobook
            {
                Title = "Unrelated",
                BasePath = null
            };
            db.RootFolders.Add(root);
            db.Audiobooks.AddRange(candidate, unrelated);
            await db.SaveChangesAsync();
            var sourceSemantics = new FileSystemPathSemantics(
                FileSystemPathSemantics.CurrentHostDefault.Syntax,
                FileSystemCaseSensitivity.Sensitive);
            await AddTrackedFileAsync(
                db,
                candidate,
                candidateSourceFile,
                source,
                sourceSemantics,
                FileSystemCaseSensitivityMode.Sensitive);
            var targetSemantics = new FileSystemPathSemantics(
                FileSystemPathSemantics.CurrentHostDefault.Syntax,
                FileSystemCaseSensitivity.Insensitive);
            var occupied = AudiobookFile.CreateUnresolved(occupiedTargetFile);
            occupied.AudiobookId = unrelated.Id;
            occupied.ApplyPathIdentity(
                occupiedTargetFile,
                AudiobookFilePathIdentity.CreateValid(
                    occupiedTargetFile,
                    targetSemantics,
                    FileSystemCaseSensitivityMode.Insensitive,
                    target));
            db.AudiobookFiles.Add(occupied);
            await db.SaveChangesAsync();
            rootId = root.Id;
            candidateAudiobookId = candidate.Id;
            unrelatedAudiobookId = unrelated.Id;
            occupiedFileId = occupied.Id;
        }

        var service = CreateService();
        var result = await service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.MetadataOnly,
                false,
                "Moved Library",
                false,
                FileSystemCaseSensitivityMode.Insensitive));

        Assert.Equal(RootFolderRelocationStatus.NeedsAttention, result.Status);
        Assert.Equal(1, result.TotalJobs);
        Assert.Equal(0, result.CompletedJobs);
        var repair = await service.GetSkippedMetadataRepairDetailsAsync(
            result.RelocationId!.Value,
            candidateAudiobookId);
        var collision = Assert.Single(
            Assert.IsType<RootFolderMetadataRepairDetails>(repair).CollisionGroups);
        Assert.Equal(2, collision.Files.Count);
        var candidateRepairFile = Assert.Single(collision.Files, file => file.CanRemove);
        Assert.Equal(candidateAudiobookId, candidateRepairFile.AudiobookId);
        var externalOwner = Assert.Single(collision.Files, file => !file.CanRemove);
        Assert.Equal(unrelatedAudiobookId, externalOwner.AudiobookId);
        Assert.Equal(occupiedFileId, externalOwner.AudiobookFileId);
        var externalRemoval = await Assert.ThrowsAsync<ApplicationConflictException>(() =>
            service.RemoveSkippedMetadataRepairFileAsync(
                result.RelocationId.Value,
                candidateAudiobookId,
                occupiedFileId));
        Assert.Equal(
            "root_folder_metadata_repair_file_not_colliding",
            externalRemoval.Code);

        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(target, (await verification.RootFolders.SingleAsync()).Path);
        Assert.Equal(
            candidateBasePath,
            (await verification.Audiobooks.SingleAsync(audiobook => audiobook.Id == candidateAudiobookId)).BasePath);
        var relocation = await verification.RootFolderRelocations
            .Include(candidate => candidate.SkippedItems)
            .SingleAsync();
        var skipped = Assert.Single(relocation.SkippedItems);
        Assert.Equal(candidateAudiobookId, skipped.AudiobookId);
        Assert.Contains(
            "same filesystem identity",
            skipped.Reason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MetadataOnly_UnresolvedTargetIdentity_SkipsCandidateAudiobook()
    {
        var source = Path.Join(Path.GetTempPath(), $"metadata-unresolved-target-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"metadata-unresolved-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        var candidateBasePath = Path.Join(source, "Candidate");
        var candidateSourceFile = Path.Join(candidateBasePath, "book.m4b");
        var occupiedTargetFile = Path.Join(target, "Candidate", "book.m4b");
        int rootId;
        int candidateAudiobookId;
        int unresolvedAudiobookId;
        int unresolvedFileId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive
            };
            var candidate = new Audiobook
            {
                Title = "Candidate",
                BasePath = candidateBasePath
            };
            var unresolvedOwner = new Audiobook
            {
                Title = "Unresolved",
                BasePath = null
            };
            db.RootFolders.Add(root);
            db.Audiobooks.AddRange(candidate, unresolvedOwner);
            await db.SaveChangesAsync();
            var sourceSemantics = new FileSystemPathSemantics(
                FileSystemPathSemantics.CurrentHostDefault.Syntax,
                FileSystemCaseSensitivity.Sensitive);
            await AddTrackedFileAsync(
                db,
                candidate,
                candidateSourceFile,
                source,
                sourceSemantics,
                FileSystemCaseSensitivityMode.Sensitive);
            var targetSemantics = new FileSystemPathSemantics(
                FileSystemPathSemantics.CurrentHostDefault.Syntax,
                FileSystemCaseSensitivity.Insensitive);
            var unresolved = AudiobookFile.CreateUnresolved(occupiedTargetFile);
            unresolved.AudiobookId = unresolvedOwner.Id;
            unresolved.ApplyPathIdentity(
                occupiedTargetFile,
                AudiobookFilePathIdentity.CreateValid(
                    occupiedTargetFile,
                    targetSemantics,
                    FileSystemCaseSensitivityMode.Insensitive,
                    target));
            unresolved.PreparePathIdentityReconciliation(
                "Injected unresolved target identity.");
            db.AudiobookFiles.Add(unresolved);
            await db.SaveChangesAsync();
            rootId = root.Id;
            candidateAudiobookId = candidate.Id;
            unresolvedAudiobookId = unresolvedOwner.Id;
            unresolvedFileId = unresolved.Id;
        }

        var service = CreateService();
        var result = await service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.MetadataOnly,
                false,
                "Moved Library",
                false,
                FileSystemCaseSensitivityMode.Insensitive));

        Assert.Equal(RootFolderRelocationStatus.NeedsAttention, result.Status);
        Assert.Equal(1, result.TotalJobs);
        Assert.Equal(0, result.CompletedJobs);
        var repair = await service.GetSkippedMetadataRepairDetailsAsync(
            result.RelocationId!.Value,
            candidateAudiobookId);
        Assert.NotNull(repair);
        Assert.Equal(
            RootFolderRelocationSkipReasonCode.TargetIdentityUnresolvedConflict,
            repair!.ReasonCode);
        var conflict = Assert.Single(repair.CollisionGroups);
        var candidateFile = Assert.Single(conflict.Files, file => file.CanRemove);
        var externalFile = Assert.Single(conflict.Files, file => !file.CanRemove);
        Assert.Equal(unresolvedAudiobookId, externalFile.AudiobookId);
        Assert.Equal(unresolvedFileId, externalFile.AudiobookFileId);
        await service.RemoveSkippedMetadataRepairFileAsync(
            result.RelocationId.Value,
            candidateAudiobookId,
            candidateFile.AudiobookFileId);
        var retried = await service.RetryAsync(result.RelocationId.Value);
        Assert.Equal(RootFolderRelocationStatus.Completed, retried.Status);

        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(target, (await verification.RootFolders.SingleAsync()).Path);
        Assert.Equal(
            Path.Join(target, "Candidate"),
            (await verification.Audiobooks.SingleAsync(audiobook => audiobook.Id == candidateAudiobookId)).BasePath);
        Assert.Empty(await verification.RootFolderRelocationSkippedItems.ToListAsync());
    }

    [Fact]
    public async Task MetadataOnly_NonRepairableTrackedPathFailure_RejectsBeforePublishingPartialRootRepair()
    {
        var source = Path.Join(
            Path.GetTempPath(),
            $"metadata-nonrepairable-source-{Guid.NewGuid():N}");
        var target = Path.Join(
            Path.GetTempPath(),
            $"metadata-nonrepairable-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            var audiobook = new Audiobook
            {
                Title = "Broken Tracked Path",
                BasePath = Path.Join(source, "Broken"),
                Files = [AudiobookFile.CreateUnresolved()]
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var exception = await Assert.ThrowsAsync<RootFolderPathChangeRejectedException>(() =>
            CreateService().StartAsync(
                rootId,
                new RootFolderPathChangeCommand(
                    target,
                    RootFolderRelocationMode.MetadataOnly,
                    false,
                    "Moved Library",
                    false,
                    FileSystemCaseSensitivityMode.Auto)));

        Assert.Equal("root_folder_metadata_path_repair_required", exception.Code);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(source, (await verification.RootFolders.SingleAsync()).Path);
        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
        Assert.Empty(await verification.RootFolderRelocationSkippedItems.ToListAsync());
    }

    [Fact]
    public async Task MetadataOnly_UnattributableInvalidAudiobookBasePathIsPreservedWithoutClaimingIt()
    {
        var source = Path.Join(Path.GetTempPath(), $"metadata-invalid-base-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"metadata-invalid-base-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        int rootId;
        int invalidAudiobookId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            var validBasePath = Path.Join(source, "Valid");
            var invalid = new Audiobook
            {
                Title = "Invalid Legacy Path",
                BasePath = "\0invalid"
            };
            db.Audiobooks.AddRange(
                new Audiobook
                {
                    Title = "Valid",
                    BasePath = validBasePath,
                    FilePath = Path.Join(validBasePath, "book.m4b")
                },
                invalid);
            await db.SaveChangesAsync();
            rootId = root.Id;
            invalidAudiobookId = invalid.Id;
        }

        var result = await CreateService().StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.MetadataOnly,
                false,
                "Moved Library",
                false,
                FileSystemCaseSensitivityMode.Auto));

        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
        Assert.Equal(1, result.TotalJobs);
        Assert.Equal(1, result.CompletedJobs);
        await using var verification = await _factory.CreateDbContextAsync();
        var audiobooks = await verification.Audiobooks.OrderBy(audiobook => audiobook.Title).ToListAsync();
        Assert.Equal("\0invalid", audiobooks[0].BasePath);
        Assert.Equal(Path.Join(target, "Valid"), audiobooks[1].BasePath);
        Assert.Equal(Path.Join(target, "Valid", "book.m4b"), audiobooks[1].FilePath);

        Assert.Equal(
            "\0invalid",
            (await verification.Audiobooks.SingleAsync(audiobook => audiobook.Id == invalidAudiobookId)).BasePath);
        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
        Assert.Empty(await verification.RootFolderRelocationSkippedItems.ToListAsync());
        Assert.Empty(await verification.MoveJobs.ToListAsync());
    }

    [Fact]
    public async Task MetadataOnly_SourceRootFilePathReferencesAreRewrittenWithoutAttentionRecord()
    {
        var source = Path.Join(Path.GetTempPath(), $"metadata-source-root-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"metadata-source-root-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            var firstBasePath = Path.Join(source, "A Valid");
            var sourceRootFilePath = Path.Join(source, "M Source Root");
            var lastBasePath = Path.Join(source, "Z Valid");
            db.Audiobooks.AddRange(
                new Audiobook
                {
                    Title = "A Valid",
                    BasePath = firstBasePath,
                    FilePath = Path.Join(firstBasePath, "book.m4b"),
                    ImageUrl = Path.Join(firstBasePath, "cover.jpg")
                },
                new Audiobook
                {
                    Title = "M Source Root",
                    BasePath = sourceRootFilePath,
                    FilePath = sourceRootFilePath,
                    ImageUrl = Path.Join(sourceRootFilePath, "cover.jpg")
                },
                new Audiobook
                {
                    Title = "Z Valid",
                    BasePath = lastBasePath,
                    FilePath = Path.Join(lastBasePath, "book.m4b"),
                    ImageUrl = Path.Join(lastBasePath, "cover.jpg")
                });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var result = await CreateService().StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.MetadataOnly,
                false,
                "Moved Library",
                false,
                FileSystemCaseSensitivityMode.Auto));

        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(target, (await verification.RootFolders.SingleAsync()).Path);
        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
        Assert.Equal(3, result.TotalJobs);
        Assert.Equal(3, result.CompletedJobs);
        Assert.Null(result.RelocationId);

        var audiobooks = await verification.Audiobooks.OrderBy(audiobook => audiobook.Title).ToListAsync();
        Assert.Equal(Path.Join(target, "A Valid"), audiobooks[0].BasePath);
        Assert.Equal(Path.Join(target, "A Valid", "book.m4b"), audiobooks[0].FilePath);
        Assert.Equal(Path.Join(target, "A Valid", "cover.jpg"), audiobooks[0].ImageUrl);
        Assert.Equal(Path.Join(target, "M Source Root"), audiobooks[1].BasePath);
        Assert.Equal(Path.Join(target, "M Source Root"), audiobooks[1].FilePath);
        Assert.Equal(Path.Join(target, "M Source Root", "cover.jpg"), audiobooks[1].ImageUrl);
        Assert.Equal(Path.Join(target, "Z Valid"), audiobooks[2].BasePath);
        Assert.Equal(Path.Join(target, "Z Valid", "book.m4b"), audiobooks[2].FilePath);
        Assert.Equal(Path.Join(target, "Z Valid", "cover.jpg"), audiobooks[2].ImageUrl);

        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
        Assert.Empty(await verification.RootFolderRelocationSkippedItems.ToListAsync());
        Assert.Empty(await verification.MoveJobs.ToListAsync());
    }

    [Fact]
    public async Task MetadataOnly_SourceRootFilePathCompletesWithoutRetry()
    {
        var source = Path.Join(Path.GetTempPath(), $"metadata-source-root-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"metadata-source-root-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            var basePath = Path.Join(source, "Title");
            db.Audiobooks.Add(new Audiobook
            {
                Title = "Title",
                BasePath = basePath,
                FilePath = basePath,
                ImageUrl = Path.Join(basePath, "cover.jpg")
            });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var started = await CreateService().StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.MetadataOnly,
                false,
                "Moved Library",
                false,
                FileSystemCaseSensitivityMode.Auto));

        Assert.True(
            started.Status == RootFolderRelocationStatus.Completed,
            started.Error ?? $"Unexpected status: {started.Status}");
        Assert.Equal(1, started.CompletedJobs);
        Assert.Null(started.RelocationId);

        await using var verification = await _factory.CreateDbContextAsync();
        var audiobookAfter = await verification.Audiobooks.SingleAsync();
        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
        Assert.Empty(await verification.RootFolderRelocationSkippedItems.ToListAsync());
        Assert.Equal(Path.Join(target, "Title"), audiobookAfter.BasePath);
        Assert.Equal(Path.Join(target, "Title"), audiobookAfter.FilePath);
        Assert.Equal(Path.Join(target, "Title", "cover.jpg"), audiobookAfter.ImageUrl);
    }

    [Fact]
    public async Task RetryAsync_MetadataOnlyTargetSemanticsChanged_PreservesSkippedState()
    {
        var source = Path.Join(Path.GetTempPath(), $"retry-semantics-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"retry-semantics-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(target);
        var targetSyntax = FileSystemPathSemantics.CurrentHostDefault.Syntax;
        var originalTargetSemantics = new FileSystemPathSemantics(
            targetSyntax,
            FileSystemCaseSensitivity.Insensitive);
        int audiobookId;
        Guid relocationId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = target,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                ResolvedCaseSensitivity = FileSystemCaseSensitivity.Insensitive,
                PathIdentityState = PathIdentityState.Valid,
                PathIdentityKey = FileSystemPathIdentity.CreateKey(
                    "root",
                    target,
                    originalTargetSemantics)
            };
            var audiobook = new Audiobook
            {
                Title = "Skipped",
                BasePath = Path.Join(source, "Skipped")
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            audiobookId = audiobook.Id;
            var relocation = new RootFolderRelocation
            {
                RootFolderId = root.Id,
                ActiveRootFolderId = root.Id,
                SourcePath = source,
                SourceCaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
                TargetPath = target,
                TargetCaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                TargetIdentityEnrollmentState = TargetIdentityEnrollmentState.Authorized,
                Mode = RootFolderRelocationMode.MetadataOnly,
                Status = RootFolderRelocationStatus.NeedsAttention,
                DesiredName = root.Name,
                TotalJobs = 1,
                CompletedJobs = 0,
                Error = "1 audiobook(s) could not have stored paths rewritten automatically.",
                SkippedItems =
                [
                    new RootFolderRelocationSkippedItem
                    {
                        AudiobookId = audiobook.Id,
                        Reason = "Retry required.",
                        CreatedAt = DateTimeOffset.UtcNow
                    }
                ]
            };
            db.RootFolderRelocations.Add(relocation);
            await db.SaveChangesAsync();
            relocationId = relocation.Id;
        }

        var changedSemanticsResolver = new Mock<IFileSystemSemanticsResolver>();
        changedSemanticsResolver.Setup(resolver => resolver.ResolveAsync(
                target,
                FileSystemCaseSensitivityMode.Auto,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileSystemSemanticsResolution(
                new FileSystemPathSemantics(
                    targetSyntax,
                    FileSystemCaseSensitivity.Sensitive),
                PathIdentityState.Valid,
                target,
                CanonicalPath: target));

        var result = await CreateService(
            semanticsResolver: changedSemanticsResolver.Object).RetryAsync(relocationId);

        Assert.Equal(RootFolderRelocationStatus.NeedsAttention, result.Status);
        Assert.Contains("case semantics changed", result.Error, StringComparison.OrdinalIgnoreCase);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(target, (await verification.RootFolders.SingleAsync()).Path);
        Assert.Equal(
            Path.Join(source, "Skipped"),
            (await verification.Audiobooks.SingleAsync(audiobook => audiobook.Id == audiobookId)).BasePath);
        Assert.Single(await verification.RootFolderRelocationSkippedItems.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentRetryAsync_SerializesStateTransitions()
    {
        var (relocationId, rootId) = await SeedRetryableRelocationAsync();
        var coordinator = new FirstEntryPausingCoordinator();
        var service = new RootFolderRelocationService(
            _factory,
            new FileSystemSemanticsResolver(),
            new NoopHubBroadcaster(),
            TimeProvider.System,
            coordinator,
            _operationCoordinator,
            CreateMoveSourceManifestService(),
            TestLibraryFilesystemReadiness.Ready());

        var firstRetry = service.RetryAsync(relocationId);
        await coordinator.FirstEntered;
        var secondRetry = service.RetryAsync(relocationId);

        await Task.Delay(50);
        Assert.Equal(1, coordinator.EntryCount);

        coordinator.ReleaseFirst();
        var firstResult = await firstRetry;
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => secondRetry);

        Assert.Equal(RootFolderRelocationStatus.Completed, firstResult.Status);
        Assert.Contains("needing attention", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, coordinator.EntryCount);
        await using var verification = await _factory.CreateDbContextAsync();
        var relocation = await verification.RootFolderRelocations.SingleAsync();
        var root = await verification.RootFolders.SingleAsync();
        Assert.Equal(relocationId, relocation.Id);
        Assert.Equal(rootId, root.Id);
        Assert.Equal(RootFolderRelocationStatus.Completed, relocation.Status);
        Assert.Null(relocation.ActiveRootFolderId);
        Assert.Empty(await verification.MoveJobs.ToListAsync());
        Assert.Empty(await verification.RootFolderRelocationSkippedItems.ToListAsync());
    }

    [Fact]
    public async Task RetryAsync_BroadcastsAfterReleasingCoordinator()
    {
        var (relocationId, _) = await SeedRetryableRelocationAsync();
        var coordinator = new TrackingCoordinator();
        var broadcaster = new RecordingHubBroadcaster(() => coordinator.IsExecuting);
        var service = new RootFolderRelocationService(
            _factory,
            new FileSystemSemanticsResolver(),
            broadcaster,
            TimeProvider.System,
            coordinator,
            _operationCoordinator,
            CreateMoveSourceManifestService(),
            TestLibraryFilesystemReadiness.Ready());

        var result = await service.RetryAsync(relocationId);

        Assert.Equal(1, broadcaster.BroadcastCount);
        Assert.False(broadcaster.CoordinatorWasExecuting);
        Assert.Same(result, broadcaster.Payload);
    }

    [Fact]
    public async Task RetryAsync_RequestCanceledDuringBroadcast_ReturnsCommittedResult()
    {
        var (relocationId, rootId) = await SeedRetryableRelocationAsync();
        using var cancellation = new CancellationTokenSource();
        var service = new RootFolderRelocationService(
            _factory,
            new FileSystemSemanticsResolver(),
            new CancelingHubBroadcaster(cancellation),
            TimeProvider.System,
            new FilesystemMutationCoordinator(),
            _operationCoordinator,
            CreateMoveSourceManifestService(),
            TestLibraryFilesystemReadiness.Ready());

        var result = await service.RetryAsync(
            relocationId,
            cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
        await using var verification = await _factory.CreateDbContextAsync();
        var relocation = await verification.RootFolderRelocations.SingleAsync();
        var root = await verification.RootFolders.SingleAsync();
        Assert.Equal(rootId, root.Id);
        Assert.Equal(RootFolderRelocationStatus.Completed, relocation.Status);
        Assert.Null(relocation.ActiveRootFolderId);
    }

    [Fact]
    public async Task RetryAsync_CancelledWhileWaiting_DoesNotMutateOrBroadcast()
    {
        var (relocationId, rootId) = await SeedRetryableRelocationAsync();
        var coordinator = new FirstEntryPausingCoordinator();
        var broadcaster = new RecordingHubBroadcaster(() => false);
        var service = new RootFolderRelocationService(
            _factory,
            new FileSystemSemanticsResolver(),
            broadcaster,
            TimeProvider.System,
            coordinator,
            _operationCoordinator,
            CreateMoveSourceManifestService(),
            TestLibraryFilesystemReadiness.Ready());
        var blocker = coordinator.ExecuteExclusiveAsync(_ => Task.CompletedTask);
        await coordinator.FirstEntered;
        using var cancellation = new CancellationTokenSource();

        var retry = service.RetryAsync(relocationId, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry);
        Assert.Equal(0, broadcaster.BroadcastCount);
        await using (var verification = await _factory.CreateDbContextAsync())
        {
            var relocation = await verification.RootFolderRelocations.SingleAsync();
            var root = await verification.RootFolders.SingleAsync();
            Assert.Equal(rootId, relocation.ActiveRootFolderId);
            Assert.Equal(RootFolderRelocationStatus.NeedsAttention, relocation.Status);
            Assert.Equal(rootId, root.Id);
        }

        coordinator.ReleaseFirst();
        await blocker;
    }

    [Fact]
    public async Task RetryAsync_FailedManifestJob_RequeuesWithVersionFourIdentity()
    {
        var (rootId, _, _, target) = await SeedRelocationScenarioAsync();
        var service = CreateService();
        var started = await service.StartAsync(
            rootId,
            BuildRelocationCommand(target));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var relocation = await db.RootFolderRelocations.SingleAsync();
            var job = await db.MoveJobs.SingleAsync();
            job.Status = MoveJobStatus.Failed;
            job.Error = "Simulated failure.";
            job.ActiveDeduplicationKey = null;
            relocation.Status = RootFolderRelocationStatus.NeedsAttention;
            relocation.Error = job.Error;
            await db.SaveChangesAsync();
        }

        var result = await service.RetryAsync(started.RelocationId!.Value);

        Assert.Equal(RootFolderRelocationStatus.Running, result.Status);
        await using var verification = await _factory.CreateDbContextAsync();
        var retried = await verification.MoveJobs
            .Include(job => job.Entries)
            .SingleAsync();
        Assert.Equal(MoveJobStatus.Queued, retried.Status);
        Assert.Equal(MoveManifestIdentity.Version, retried.IdentityKeyVersion);
        Assert.True(retried.TryGetSourceIdentity(out var sourceIdentity));
        Assert.True(retried.TryGetTargetIdentity(out var targetIdentity));
        Assert.Equal(
            MoveManifestIdentity.CreateDeduplicationKey(
                retried.AudiobookId,
                retried.SourcePath!,
                sourceIdentity,
                retried.RequestedPath,
                targetIdentity,
                retried.Entries),
            retried.ActiveDeduplicationKey);
    }

    [Fact]
    public async Task RetryAsync_NonCurrentProtocolJob_RemainsNeedsAttentionEvenWithCurrentBoundaryEvidence()
    {
        var (rootId, _, _, target) = await SeedRelocationScenarioAsync();
        var service = CreateService();
        var started = await service.StartAsync(
            rootId,
            BuildRelocationCommand(target));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var relocation = await db.RootFolderRelocations.SingleAsync();
            var job = await db.MoveJobs.SingleAsync();
            job.ExecutionProtocolVersion =
                MoveExecutionProtocol.TargetBoundaryMarkerlessDatabaseState;
            job.Status = MoveJobStatus.Failed;
            job.Error = "Simulated legacy protocol failure.";
            job.ActiveDeduplicationKey = null;
            relocation.Status = RootFolderRelocationStatus.NeedsAttention;
            relocation.Error = job.Error;
            await db.SaveChangesAsync();
        }

        var result = await service.RetryAsync(started.RelocationId!.Value);

        Assert.Equal(RootFolderRelocationStatus.NeedsAttention, result.Status);
        await using var verification = await _factory.CreateDbContextAsync();
        var rejected = await verification.MoveJobs.SingleAsync();
        Assert.Equal(MoveJobStatus.NeedsAttention, rejected.Status);
        Assert.Null(rejected.ActiveDeduplicationKey);
        Assert.Contains(
            "current durable database execution protocol",
            rejected.Error ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RetryAsync_ManifestlessJob_RemainsNeedsAttention()
    {
        var (rootId, _, _, target) = await SeedRelocationScenarioAsync();
        var service = CreateService();
        var started = await service.StartAsync(
            rootId,
            BuildRelocationCommand(target));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var relocation = await db.RootFolderRelocations.SingleAsync();
            var job = await db.MoveJobs
                .Include(candidate => candidate.Entries)
                .SingleAsync();
            db.MoveJobEntries.RemoveRange(job.Entries);
            job.Status = MoveJobStatus.Failed;
            job.ActiveDeduplicationKey = null;
            relocation.Status = RootFolderRelocationStatus.NeedsAttention;
            await db.SaveChangesAsync();
        }

        var result = await service.RetryAsync(started.RelocationId!.Value);

        Assert.Equal(RootFolderRelocationStatus.NeedsAttention, result.Status);
        Assert.Contains("manifest evidence", result.Error, StringComparison.OrdinalIgnoreCase);
        await using var verification = await _factory.CreateDbContextAsync();
        var rejected = await verification.MoveJobs.SingleAsync();
        Assert.Equal(MoveJobStatus.NeedsAttention, rejected.Status);
        Assert.Null(rejected.ActiveDeduplicationKey);
        Assert.Contains("tracked-file source manifest", rejected.Error, StringComparison.OrdinalIgnoreCase);
    }

    [WindowsFact]
    public async Task RetryAsync_ForeignPersistedSourceIdentity_RemainsNeedsAttention()
    {
        var (rootId, _, _, target) = await SeedRelocationScenarioAsync();
        var service = CreateService();
        var started = await service.StartAsync(
            rootId,
            BuildRelocationCommand(target));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var relocation = await db.RootFolderRelocations.SingleAsync();
            var job = await db.MoveJobs.SingleAsync();
            var nativeSource = Assert.IsType<string>(job.SourcePath);
            var foreignSource = TempFileService
                .GetWindowsRootRelativeForeignAlias(nativeSource);
            job.SourcePath = foreignSource;
            job.SourcePathSyntax = FileSystemPathSyntax.Unix;
            job.SourceCaseSensitivity = FileSystemCaseSensitivity.Sensitive;
            job.SourceCaseSensitivityMode = FileSystemCaseSensitivityMode.Auto;
            job.SourceIdentityBoundary = foreignSource;
            job.Status = MoveJobStatus.Failed;
            job.ActiveDeduplicationKey = null;
            relocation.Status = RootFolderRelocationStatus.NeedsAttention;
            await db.SaveChangesAsync();
        }

        var result = await service.RetryAsync(started.RelocationId!.Value);

        Assert.Equal(RootFolderRelocationStatus.NeedsAttention, result.Status);
        await using var verification = await _factory.CreateDbContextAsync();
        var rejected = await verification.MoveJobs.SingleAsync();
        Assert.Equal(MoveJobStatus.NeedsAttention, rejected.Status);
        Assert.Null(rejected.ActiveDeduplicationKey);
        Assert.Contains(
            "invalid persisted filesystem identity",
            rejected.Error ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RetryAsync_InvalidPersistedSourceMutationBoundary_RemainsNeedsAttention()
    {
        var (rootId, _, _, target) = await SeedRelocationScenarioAsync();
        var service = CreateService();
        var started = await service.StartAsync(
            rootId,
            BuildRelocationCommand(target));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var relocation = await db.RootFolderRelocations.SingleAsync();
            var job = await db.MoveJobs.SingleAsync();
            job.SourceCleanupBoundary = Path.Join(
                Path.GetTempPath(),
                $"unrelated-boundary-{Guid.NewGuid():N}");
            job.Status = MoveJobStatus.Failed;
            job.ActiveDeduplicationKey = null;
            relocation.Status = RootFolderRelocationStatus.NeedsAttention;
            await db.SaveChangesAsync();
        }

        var result = await service.RetryAsync(started.RelocationId!.Value);

        Assert.Equal(RootFolderRelocationStatus.NeedsAttention, result.Status);
        await using var verification = await _factory.CreateDbContextAsync();
        var rejected = await verification.MoveJobs.SingleAsync();
        Assert.Equal(MoveJobStatus.NeedsAttention, rejected.Status);
        Assert.Null(rejected.ActiveDeduplicationKey);
        Assert.Contains("source mutation boundary", rejected.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RetryAsync_AllJobsCompletedAfterFinalizationBlocked_AppliesRootMetadataAndCompletes()
    {
        var source = Path.Join(Path.GetTempPath(), $"retry-finalize-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"retry-finalize-target-{Guid.NewGuid():N}");
        var otherRootPath = Path.Join(Path.GetTempPath(), $"retry-finalize-other-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
                ResolvedCaseSensitivity = FileSystemCaseSensitivity.Sensitive,
                PathIdentityState = PathIdentityState.Valid
            };
            db.RootFolders.Add(root);
            db.RootFolders.Add(new RootFolder
            {
                Name = "Other",
                Path = otherRootPath,
                IsDefault = true,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
                ResolvedCaseSensitivity = FileSystemCaseSensitivity.Sensitive,
                PathIdentityState = PathIdentityState.Valid
            });
            var audiobook = new Audiobook { Title = "Title", BasePath = Path.Join(source, "Title") };
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            await AddTrackedFileAsync(
                db,
                audiobook,
                Path.Join(audiobook.BasePath!, "book.m4b"),
                source);
            rootId = root.Id;
        }

        var service = CreateService();
        var started = await service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Moved Library",
                true,
                FileSystemCaseSensitivityMode.Insensitive));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var job = await db.MoveJobs.SingleAsync();
            job.Status = MoveJobStatus.Completed;
            job.ActiveDeduplicationKey = null;
            var relocation = await db.RootFolderRelocations.SingleAsync();
            relocation.Status = RootFolderRelocationStatus.NeedsAttention;
            relocation.Error = "Target filesystem identity became unavailable during finalization.";
            await db.SaveChangesAsync();
        }

        var result = await service.RetryAsync(started.RelocationId!.Value);

        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
        await using var verification = await _factory.CreateDbContextAsync();
        var rootAfter = await verification.RootFolders.SingleAsync(root => root.Id == rootId);
        var otherRootAfter = await verification.RootFolders.SingleAsync(root => root.Id != rootId);
        var relocationAfter = await verification.RootFolderRelocations.SingleAsync();
        Assert.Equal(target, rootAfter.Path);
        Assert.Equal("Moved Library", rootAfter.Name);
        Assert.True(rootAfter.IsDefault);
        Assert.False(otherRootAfter.IsDefault);
        Assert.Equal(FileSystemCaseSensitivityMode.Insensitive, rootAfter.CaseSensitivityMode);
        Assert.Equal(FileSystemCaseSensitivity.Insensitive, rootAfter.ResolvedCaseSensitivity);
        Assert.Null(relocationAfter.ActiveRootFolderId);
        Assert.Equal(RootFolderRelocationStatus.Completed, relocationAfter.Status);
        Assert.Equal(relocationAfter.TotalJobs, relocationAfter.CompletedJobs);
    }

    [LinuxFact]
    public async Task RetryAsync_CompletedTargetReplacedAfterSave_DoesNotCommitRootMetadata()
    {
        var (rootId, _, source, target) = await SeedRelocationScenarioAsync();
        var displacedTarget = target + "-retry-commit-original";
        var service = CreateService();
        var started = await service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Moved Library",
                false,
                FileSystemCaseSensitivityMode.Sensitive));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var job = await db.MoveJobs.SingleAsync();
            job.Status = MoveJobStatus.Completed;
            job.ActiveDeduplicationKey = null;
            var relocation = await db.RootFolderRelocations.SingleAsync();
            relocation.Status = RootFolderRelocationStatus.NeedsAttention;
            relocation.Error = "Finalization retry required.";
            await db.SaveChangesAsync();
        }

        service.BeforeCompletedRelocationAtomicCommitForTest = relocationId =>
        {
            Assert.Equal(started.RelocationId, relocationId);
            Directory.Move(target, displacedTarget);
            Directory.CreateDirectory(target);
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RetryAsync(started.RelocationId!.Value));

        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(
            source,
            (await verification.RootFolders
                .SingleAsync(candidate => candidate.Id == rootId)).Path);
        Assert.Equal(
            RootFolderRelocationStatus.NeedsAttention,
            (await verification.RootFolderRelocations
                .SingleAsync(candidate => candidate.Id == started.RelocationId)).Status);
        Assert.True(Directory.Exists(displacedTarget));
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public async Task OnMoveJobStateChangedAsync_AnonymousPublicationAppearsAfterLastJobCompletion_BlocksFinalization()
    {
        var (rootId, _, source, target) = await SeedRelocationScenarioAsync();
        var started = await CreateService().StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Moved Library",
                false,
                FileSystemCaseSensitivityMode.Sensitive));
        Guid jobId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var job = await db.MoveJobs.SingleAsync();
            jobId = job.Id;
            job.Status = MoveJobStatus.Completed;
            job.ActiveDeduplicationKey = null;
            await db.SaveChangesAsync();
        }

        var anonymousDirectory = Path.Join(source, "late-publication");
        Directory.CreateDirectory(anonymousDirectory);
        var anonymousPublishedPath = Path.Join(anonymousDirectory, "unregistered.m4b");
        await File.WriteAllTextAsync(anonymousPublishedPath, "anonymous-audio");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.FileMutationJournals.Add(new FileMutationJournal
            {
                OperationId = Guid.NewGuid(),
                ProtocolVersion = FileMutationProtocol.Current,
                Action = FileAction.Copy,
                SourcePath = Path.Join(
                    Path.GetTempPath(),
                    $"late-publication-source-{Guid.NewGuid():N}.m4b"),
                DestinationPath = anonymousPublishedPath,
                SourceParentDirectoryObjectIdentity = "source-parent",
                DestinationParentDirectoryObjectIdentity = "destination-parent",
                SourcePhysicalObjectIdentity = "source-generation",
                TargetPhysicalObjectIdentity = "target-generation",
                SourceLength = new FileInfo(anonymousPublishedPath).Length,
                State = FileMutationJournalState.TargetVerified,
                AudiobookId = null,
                AudiobookFileId = null
            });
            await db.SaveChangesAsync();
        }

        var service = CreateService(
            fileRegistrationRecoveryProbe: new FileRegistrationRecoveryProbe(_factory));
        await service.OnMoveJobStateChangedAsync(jobId);

        await using var verification = await _factory.CreateDbContextAsync();
        var rootAfter = await verification.RootFolders.SingleAsync(root => root.Id == rootId);
        var relocationAfter = await verification.RootFolderRelocations
            .SingleAsync(relocation => relocation.Id == started.RelocationId);
        Assert.Equal(source, rootAfter.Path);
        Assert.Equal(RootFolderRelocationStatus.NeedsAttention, relocationAfter.Status);
        Assert.Equal(rootId, relocationAfter.ActiveRootFolderId);
        Assert.Contains("file publication", relocationAfter.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(anonymousPublishedPath));
    }

    [LinuxFact]
    public async Task OnMoveJobStateChangedAsync_CompletedTargetReplacedAfterSave_DoesNotCommitRootMetadata()
    {
        var (rootId, _, source, target) = await SeedRelocationScenarioAsync();
        var displacedTarget = target + "-reconcile-commit-original";
        var service = CreateService();
        var started = await service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Moved Library",
                false,
                FileSystemCaseSensitivityMode.Sensitive));
        Guid jobId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var job = await db.MoveJobs.SingleAsync();
            jobId = job.Id;
            job.Status = MoveJobStatus.Completed;
            job.ActiveDeduplicationKey = null;
            await db.SaveChangesAsync();
        }

        service.BeforeCompletedRelocationAtomicCommitForTest = relocationId =>
        {
            Assert.Equal(started.RelocationId, relocationId);
            Directory.Move(target, displacedTarget);
            Directory.CreateDirectory(target);
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.OnMoveJobStateChangedAsync(jobId));

        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(
            source,
            (await verification.RootFolders
                .SingleAsync(candidate => candidate.Id == rootId)).Path);
        Assert.NotEqual(
            RootFolderRelocationStatus.Completed,
            (await verification.RootFolderRelocations
                .SingleAsync(candidate => candidate.Id == started.RelocationId)).Status);
        Assert.True(Directory.Exists(displacedTarget));
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public async Task FinalizeCompletedRelocation_ReplacedTargetWithoutAuthorization_DoesNotCommitReplacement()
    {
        var (rootId, _, source, target) = await SeedRelocationScenarioAsync();
        var service = CreateService();
        var started = await service.StartAsync(
            rootId,
            BuildRelocationCommand(target));
        Guid jobId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var job = await db.MoveJobs.SingleAsync();
            jobId = job.Id;
            job.Status = MoveJobStatus.Completed;
            job.ActiveDeduplicationKey = null;
            await db.SaveChangesAsync();
        }

        var displacedTarget = target + "-displaced";
        Directory.Move(target, displacedTarget);
        Directory.CreateDirectory(target);
        await service.OnMoveJobStateChangedAsync(jobId);

        await using var verification = await _factory.CreateDbContextAsync();
        var rootAfter = await verification.RootFolders.SingleAsync(root => root.Id == rootId);
        var relocationAfter = await verification.RootFolderRelocations
            .SingleAsync(relocation => relocation.Id == started.RelocationId);
        Assert.Equal(source, rootAfter.Path);
        Assert.Equal(RootFolderRelocationStatus.NeedsAttention, relocationAfter.Status);
        Assert.True(Directory.Exists(displacedTarget));
    }

    [Fact]
    public async Task RetryAsync_AllJobsCompletedButTargetStillUnavailable_StaysNeedsAttentionWithoutMutatingRoot()
    {
        var source = Path.Join(Path.GetTempPath(), $"retry-finalize-unavailable-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"retry-finalize-unavailable-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
                ResolvedCaseSensitivity = FileSystemCaseSensitivity.Sensitive,
                PathIdentityState = PathIdentityState.Valid
            };
            var audiobook = new Audiobook { Title = "Title", BasePath = Path.Join(source, "Title") };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            await AddTrackedFileAsync(
                db,
                audiobook,
                Path.Join(audiobook.BasePath!, "book.m4b"),
                source);
            rootId = root.Id;
        }

        var service = CreateService();
        var started = await service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Moved Library",
                true,
                FileSystemCaseSensitivityMode.Insensitive));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var job = await db.MoveJobs.SingleAsync();
            job.Status = MoveJobStatus.Completed;
            job.ActiveDeduplicationKey = null;
            var relocation = await db.RootFolderRelocations.SingleAsync();
            relocation.Status = RootFolderRelocationStatus.NeedsAttention;
            relocation.Error = "Target filesystem identity became unavailable during finalization.";
            await db.SaveChangesAsync();
        }

        var coordinator = new TrackingCoordinator();
        var broadcaster = new RecordingHubBroadcaster(() => coordinator.IsExecuting);
        var retryService = new RootFolderRelocationService(
            _factory,
            new TargetUnavailableSemanticsResolver(target),
            broadcaster,
            TimeProvider.System,
            coordinator,
            _operationCoordinator,
            CreateMoveSourceManifestService(),
            TestLibraryFilesystemReadiness.Ready());
        var result = await retryService.RetryAsync(started.RelocationId!.Value);

        Assert.Equal(RootFolderRelocationStatus.NeedsAttention, result.Status);
        Assert.Contains("became unavailable", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, broadcaster.BroadcastCount);
        Assert.False(broadcaster.CoordinatorWasExecuting);
        var publicResult = Assert.IsType<RootFolderPathChangeResult>(
            broadcaster.Payload);
        Assert.NotSame(result, publicResult);
        Assert.Equal(result.RelocationId, publicResult.RelocationId);
        Assert.Equal(
            "The relocation requires attention. Review the affected items and retry after resolving the underlying issue.",
            publicResult.Error);
        Assert.DoesNotContain(
            "became unavailable",
            publicResult.Error,
            StringComparison.OrdinalIgnoreCase);
        await using var verification = await _factory.CreateDbContextAsync();
        var rootAfter = await verification.RootFolders.SingleAsync(root => root.Id == rootId);
        var relocationAfter = await verification.RootFolderRelocations.SingleAsync();
        Assert.Equal(source, rootAfter.Path);
        Assert.Equal("Library", rootAfter.Name);
        Assert.False(rootAfter.IsDefault);
        Assert.Equal(FileSystemCaseSensitivityMode.Sensitive, rootAfter.CaseSensitivityMode);
        Assert.Equal(RootFolderRelocationStatus.NeedsAttention, relocationAfter.Status);
        Assert.Equal(rootId, relocationAfter.ActiveRootFolderId);
    }

    [Fact]
    public async Task StartRelocation_RejectsTargetWithCurrentDirectorySegment()
    {
        var source = Path.Join(Path.GetTempPath(), $"relocation-current-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"relocation-current-target-{Guid.NewGuid():N}", ".");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var service = CreateService();
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Library",
                false,
                FileSystemCaseSensitivityMode.Auto)));
        Assert.Contains("current directory", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartRelocation_RejectsTargetWithParentTraversalSegment()
    {
        var source = Path.Join(Path.GetTempPath(), $"relocation-parent-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"relocation-parent-target-{Guid.NewGuid():N}", "Child", "..", "Other");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var service = CreateService();
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Library",
                false,
                FileSystemCaseSensitivityMode.Auto)));
        Assert.Contains("parent", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartRelocation_AllowsOrdinaryValidTargetPath()
    {
        var source = Path.Join(Path.GetTempPath(), $"relocation-valid-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"relocation-valid-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var service = CreateService();
        var result = await service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Library",
                false,
                FileSystemCaseSensitivityMode.Auto));

        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(target, (await verification.RootFolders.SingleAsync()).Path);
    }

    [WindowsFact]
    public async Task StartRelocation_RejectsTargetInsideDeviceAliasExistingRoot()
    {
        var basePath = Path.Join(
            Path.GetTempPath(),
            $"relocation-device-root-conflict-{Guid.NewGuid():N}");
        var source = Path.Join(
            Path.GetTempPath(),
            $"relocation-device-root-source-{Guid.NewGuid():N}");
        var existing = Path.Join(basePath, "Books");
        var existingAlias = @"\\?\" + existing;
        var target = Path.Join(existing, "Child");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(existing);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive
            };
            db.RootFolders.Add(root);
            db.RootFolders.Add(new RootFolder
            {
                Name = "Existing Device Alias",
                Path = existingAlias,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive,
                ResolvedCaseSensitivity = FileSystemCaseSensitivity.Insensitive,
                PathIdentityState = PathIdentityState.Unavailable
            });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var exception = await Assert.ThrowsAsync<RootFolderPathChangeRejectedException>(() =>
            CreateService().StartAsync(
                rootId,
                new RootFolderPathChangeCommand(
                    target,
                    RootFolderRelocationMode.Relocate,
                    true,
                    "Library",
                    false,
                    FileSystemCaseSensitivityMode.Insensitive)));

        Assert.Equal("root_folder_target_conflict", exception.Code);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
        Assert.Equal(source, (await verification.RootFolders
            .SingleAsync(candidate => candidate.Id == rootId)).Path);
    }

    [Fact]
    public async Task StartRelocation_RejectsCaseOnlyTargetConflictWithInsensitiveExistingRoot()
    {
        var basePath = Path.Join(Path.GetTempPath(), $"relocation-case-conflict-{Guid.NewGuid():N}");
        var source = Path.Join(Path.GetTempPath(), $"relocation-case-source-{Guid.NewGuid():N}");
        var existing = Path.Join(basePath, "Books");
        var target = Path.Join(basePath, "books");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source, CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive };
            db.RootFolders.Add(root);
            db.RootFolders.Add(new RootFolder
            {
                Name = "Existing",
                Path = existing,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive,
                ResolvedCaseSensitivity = FileSystemCaseSensitivity.Insensitive,
                PathIdentityState = PathIdentityState.Valid
            });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var service = CreateService();
        var exception = await Assert.ThrowsAsync<RootFolderPathChangeRejectedException>(() => service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Library",
                false,
                FileSystemCaseSensitivityMode.Sensitive)));
        Assert.Equal("root_folder_target_conflict", exception.Code);
        Assert.Contains("already exists", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartRelocation_RejectsNestedTargetConflictWithInsensitiveExistingRoot()
    {
        var basePath = Path.Join(Path.GetTempPath(), $"relocation-nested-conflict-{Guid.NewGuid():N}");
        var source = Path.Join(Path.GetTempPath(), $"relocation-nested-source-{Guid.NewGuid():N}");
        var existing = Path.Join(basePath, "Books");
        var target = Path.Join(basePath, "books", "Child");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source, CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive };
            db.RootFolders.Add(root);
            db.RootFolders.Add(new RootFolder
            {
                Name = "Existing",
                Path = existing,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive,
                ResolvedCaseSensitivity = FileSystemCaseSensitivity.Insensitive,
                PathIdentityState = PathIdentityState.Valid
            });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var service = CreateService();
        var exception = await Assert.ThrowsAsync<RootFolderPathChangeRejectedException>(() => service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Library",
                false,
                FileSystemCaseSensitivityMode.Sensitive)));
        Assert.Equal("root_folder_target_conflict", exception.Code);
        Assert.Contains("already exists", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartRelocation_RejectsCaseOnlyTargetConflictWhenTargetIsInsensitive()
    {
        var basePath = Path.Join(Path.GetTempPath(), $"relocation-reverse-case-conflict-{Guid.NewGuid():N}");
        var source = Path.Join(Path.GetTempPath(), $"relocation-reverse-case-source-{Guid.NewGuid():N}");
        var existing = Path.Join(basePath, "Books");
        var target = Path.Join(basePath, "books");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source, CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive };
            db.RootFolders.Add(root);
            db.RootFolders.Add(new RootFolder
            {
                Name = "Existing",
                Path = existing,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
                ResolvedCaseSensitivity = FileSystemCaseSensitivity.Sensitive,
                PathIdentityState = PathIdentityState.Valid
            });
            await db.SaveChangesAsync();
            rootId = root.Id;
        }

        var service = CreateService();
        var exception = await Assert.ThrowsAsync<RootFolderPathChangeRejectedException>(() => service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Library",
                false,
                FileSystemCaseSensitivityMode.Insensitive)));
        Assert.Equal("root_folder_target_conflict", exception.Code);
        Assert.Contains("already exists", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OnMoveJobStateChanged_NonRelocationJob_SkipsGlobalCoordinator()
    {
        Guid jobId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var audiobook = new Audiobook
            {
                Title = "Ordinary Move",
                BasePath = Path.Join(Path.GetTempPath(), $"ordinary-move-{Guid.NewGuid():N}")
            };
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            var job = new MoveJob
            {
                AudiobookId = audiobook.Id,
                SourcePath = audiobook.BasePath,
                RequestedPath = Path.Join(Path.GetTempPath(), $"ordinary-target-{Guid.NewGuid():N}"),
                Status = MoveJobStatus.Running
            };
            db.MoveJobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        var coordinator = new FirstEntryPausingCoordinator();
        var service = new RootFolderRelocationService(
            _factory,
            new FileSystemSemanticsResolver(),
            new NoopHubBroadcaster(),
            TimeProvider.System,
            coordinator,
            _operationCoordinator,
            CreateMoveSourceManifestService(),
            TestLibraryFilesystemReadiness.Ready());

        await service.OnMoveJobStateChangedAsync(jobId);

        Assert.Equal(0, coordinator.EntryCount);
    }

    [Fact]
    public async Task OnMoveJobStateChanged_NonTerminalRelocationJob_SkipsGlobalCoordinator()
    {
        Guid jobId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var source = Path.Join(Path.GetTempPath(), $"running-relocation-source-{Guid.NewGuid():N}");
            var target = Path.Join(Path.GetTempPath(), $"running-relocation-target-{Guid.NewGuid():N}");
            var root = new RootFolder { Name = "Library", Path = source };
            var audiobook = new Audiobook { Title = "Running", BasePath = Path.Join(source, "Title") };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            var relocation = new RootFolderRelocation
            {
                RootFolderId = root.Id,
                ActiveRootFolderId = root.Id,
                SourcePath = source,
                TargetPath = target,
                Mode = RootFolderRelocationMode.Relocate,
                Status = RootFolderRelocationStatus.Running,
                DesiredName = root.Name,
                TotalJobs = 1
            };
            db.RootFolderRelocations.Add(relocation);
            await db.SaveChangesAsync();
            var job = new MoveJob
            {
                AudiobookId = audiobook.Id,
                SourcePath = audiobook.BasePath,
                RequestedPath = Path.Join(target, "Title"),
                Status = MoveJobStatus.Running,
                RelocationId = relocation.Id
            };
            db.MoveJobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        var coordinator = new FirstEntryPausingCoordinator();
        var service = new RootFolderRelocationService(
            _factory,
            new FileSystemSemanticsResolver(),
            new NoopHubBroadcaster(),
            TimeProvider.System,
            coordinator,
            _operationCoordinator,
            CreateMoveSourceManifestService(),
            TestLibraryFilesystemReadiness.Ready());

        await service.OnMoveJobStateChangedAsync(jobId);

        Assert.Equal(0, coordinator.EntryCount);
    }

    [Fact]
    public async Task OnMoveJobStateChanged_WaitsForFilesystemMutationCoordinator()
    {
        var source = Path.Join(Path.GetTempPath(), $"finalize-lock-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"finalize-lock-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            var audiobook = new Audiobook { Title = "Title", BasePath = Path.Join(source, "Title") };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            await AddTrackedFileAsync(
                db,
                audiobook,
                Path.Join(audiobook.BasePath!, "book.m4b"),
                source);
            rootId = root.Id;
        }

        await CreateService().StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Finalized Library",
                false,
                FileSystemCaseSensitivityMode.Auto));
        Guid jobId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var job = await db.MoveJobs.SingleAsync();
            job.Status = MoveJobStatus.Completed;
            job.ActiveDeduplicationKey = null;
            var audiobook = await db.Audiobooks.SingleAsync();
            audiobook.BasePath = job.RequestedPath;
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        var coordinator = new FirstEntryPausingCoordinator();
        var service = new RootFolderRelocationService(
            _factory,
            new FileSystemSemanticsResolver(),
            new NoopHubBroadcaster(),
            TimeProvider.System,
            coordinator,
            _operationCoordinator,
            CreateMoveSourceManifestService(),
            TestLibraryFilesystemReadiness.Ready());
        var finalizationTask = service.OnMoveJobStateChangedAsync(jobId);
        await coordinator.FirstEntered;
        Assert.False(finalizationTask.IsCompleted);

        coordinator.ReleaseFirst();
        await finalizationTask;

        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(target, (await verification.RootFolders.SingleAsync()).Path);
        Assert.Equal(
            RootFolderRelocationStatus.Completed,
            (await verification.RootFolderRelocations.SingleAsync()).Status);
    }

    [Fact]
    public async Task ReconcileActive_WaitsForFilesystemMutationCoordinator()
    {
        var source = Path.Join(Path.GetTempPath(), $"reconcile-lock-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"reconcile-lock-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            var audiobook = new Audiobook { Title = "Title", BasePath = Path.Join(source, "Title") };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            await AddTrackedFileAsync(
                db,
                audiobook,
                Path.Join(audiobook.BasePath!, "book.m4b"),
                source);
            rootId = root.Id;
        }

        await CreateService().StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Library",
                false,
                FileSystemCaseSensitivityMode.Auto));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var job = await db.MoveJobs.SingleAsync();
            job.Status = MoveJobStatus.Superseded;
            job.ActiveDeduplicationKey = null;
            await db.SaveChangesAsync();
        }

        var coordinator = new FirstEntryPausingCoordinator();
        var service = new RootFolderRelocationService(
            _factory,
            new FileSystemSemanticsResolver(),
            new NoopHubBroadcaster(),
            TimeProvider.System,
            coordinator,
            _operationCoordinator,
            CreateMoveSourceManifestService(),
            TestLibraryFilesystemReadiness.Ready());
        var reconciliationTask = service.ReconcileActiveAsync();
        await coordinator.FirstEntered;
        Assert.False(reconciliationTask.IsCompleted);

        coordinator.ReleaseFirst();
        await reconciliationTask;

        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(
            RootFolderRelocationStatus.NeedsAttention,
            (await verification.RootFolderRelocations.SingleAsync()).Status);
    }

    [Fact]
    public async Task CompletedJobs_FinalizeRootOnlyAfterEveryAudiobookPathMoved()
    {
        var source = Path.Join(Path.GetTempPath(), $"finalize-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"finalize-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            var audiobook = new Audiobook { Title = "Title", BasePath = Path.Join(source, "Title") };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            await AddTrackedFileAsync(
                db,
                audiobook,
                Path.Join(audiobook.BasePath!, "book.m4b"),
                source);
            rootId = root.Id;
        }

        var service = CreateService();
        await service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Finalized Library",
                false,
                FileSystemCaseSensitivityMode.Auto));
        Guid jobId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var job = await db.MoveJobs.SingleAsync();
            job.Status = MoveJobStatus.Completed;
            job.ActiveDeduplicationKey = null;
            var audiobook = await db.Audiobooks.SingleAsync();
            audiobook.BasePath = job.RequestedPath;
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        await service.OnMoveJobStateChangedAsync(jobId);

        await using var verification = await _factory.CreateDbContextAsync();
        var rootAfter = await verification.RootFolders.SingleAsync();
        var relocationAfter = await verification.RootFolderRelocations.SingleAsync();
        Assert.Equal(target, rootAfter.Path);
        Assert.Equal("Finalized Library", rootAfter.Name);
        Assert.Equal(RootFolderRelocationStatus.Completed, relocationAfter.Status);
        Assert.Null(relocationAfter.ActiveRootFolderId);
    }

    [Fact]
    public async Task SupersededJob_RetryPreservesTerminalStaleState()
    {
        var source = Path.Join(Path.GetTempPath(), $"superseded-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"superseded-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            var audiobook = new Audiobook { Title = "Title", BasePath = Path.Join(source, "Title") };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            await AddTrackedFileAsync(
                db,
                audiobook,
                Path.Join(audiobook.BasePath!, "book.m4b"),
                source);
            rootId = root.Id;
        }

        var service = CreateService();
        var started = await service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Library",
                false,
                FileSystemCaseSensitivityMode.Auto));
        Guid jobId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var job = await db.MoveJobs.SingleAsync();
            job.Status = MoveJobStatus.Superseded;
            job.ActiveDeduplicationKey = null;
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        await service.ReconcileActiveAsync();
        var needsAttention = await service.GetAsync(started.RelocationId!.Value);
        Assert.Equal(RootFolderRelocationStatus.NeedsAttention, needsAttention!.Status);

        var retried = await service.RetryAsync(started.RelocationId.Value);
        await using var verification = await _factory.CreateDbContextAsync();
        var preservedJob = await verification.MoveJobs.SingleAsync();
        Assert.Equal(MoveJobStatus.Superseded, preservedJob.Status);
        Assert.Equal(jobId, preservedJob.Id);
        Assert.Equal(RootFolderRelocationStatus.NeedsAttention, retried.Status);
        Assert.Contains("superseded", retried.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteRootFolder_PreservesCompletedRelocationHistoryAndKeepsHistoryQueryable()
    {
        var source = Path.Join(Path.GetTempPath(), $"delete-root-history-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"delete-root-history-target-{Guid.NewGuid():N}");
        Guid relocationId;
        DateTime? completedAt;

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            db.RootFolders.Add(root);
            await db.SaveChangesAsync();

            var relocation = new RootFolderRelocation
            {
                RootFolderId = root.Id,
                ActiveRootFolderId = null,
                SourcePath = source,
                TargetPath = target,
                Mode = RootFolderRelocationMode.Relocate,
                Status = RootFolderRelocationStatus.Completed,
                DesiredName = "Library",
                TotalJobs = 1,
                CompletedJobs = 1,
                CreatedAt = DateTime.UtcNow.AddMinutes(-5),
                CompletedAt = DateTime.UtcNow
            };
            db.RootFolderRelocations.Add(relocation);
            await db.SaveChangesAsync();
            relocationId = relocation.Id;
            completedAt = relocation.CompletedAt;

            db.RootFolders.Remove(root);
            await db.SaveChangesAsync();
        }

        await using (var verification = await _factory.CreateDbContextAsync())
        {
            Assert.Empty(await verification.RootFolders.ToListAsync());
            var relocation = await verification.RootFolderRelocations.SingleAsync(candidate => candidate.Id == relocationId);
            Assert.Null(relocation.RootFolderId);
            Assert.Equal(source, relocation.SourcePath);
            Assert.Equal(target, relocation.TargetPath);
            Assert.Equal(RootFolderRelocationStatus.Completed, relocation.Status);
            Assert.Equal(completedAt, relocation.CompletedAt);
        }

        var result = await CreateService().GetAsync(relocationId);
        Assert.NotNull(result);
        Assert.Null(result!.RootFolderId);
        Assert.Equal(target, result.CurrentPath);
        Assert.Equal(target, result.TargetPath);
        Assert.Equal(RootFolderRelocationStatus.Completed, result.Status);
    }

    [Fact]
    public async Task RetryAsync_SupersededJobWithCanonicalReplacement_DoesNotCollide()
    {
        var source = Path.Join(Path.GetTempPath(), $"superseded-collision-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"superseded-collision-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            var audiobook = new Audiobook { Title = "Title", BasePath = Path.Join(source, "Title") };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            await AddTrackedFileAsync(
                db,
                audiobook,
                Path.Join(audiobook.BasePath!, "book.m4b"),
                source);
            rootId = root.Id;
        }

        var service = CreateService();
        var started = await service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Library",
                false,
                FileSystemCaseSensitivityMode.Auto));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var superseded = await db.MoveJobs.SingleAsync();
            var key = superseded.ActiveDeduplicationKey;
            superseded.Status = MoveJobStatus.Superseded;
            superseded.ActiveDeduplicationKey = null;
            db.MoveJobs.Add(new MoveJob
            {
                AudiobookId = superseded.AudiobookId,
                RequestedPath = superseded.RequestedPath,
                Status = MoveJobStatus.Running,
                ActiveDeduplicationKey = key
            });
            var relocation = await db.RootFolderRelocations.SingleAsync();
            relocation.Status = RootFolderRelocationStatus.NeedsAttention;
            await db.SaveChangesAsync();
        }

        var result = await service.RetryAsync(started.RelocationId!.Value);

        Assert.Equal(RootFolderRelocationStatus.NeedsAttention, result.Status);
        Assert.Contains("were superseded by a newer move", result.Error);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Equal(
            MoveJobStatus.Superseded,
            (await verification.MoveJobs.SingleAsync(job => job.RelocationId != null)).Status);
        Assert.Equal(
            MoveJobStatus.Running,
            (await verification.MoveJobs.SingleAsync(job => job.RelocationId == null)).Status);
    }

    [Fact]
    public async Task StartRelocation_BroadcastFailureDoesNotUndoCommittedSaga()
    {
        var source = Path.Join(Path.GetTempPath(), $"broadcast-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"broadcast-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            var audiobook = new Audiobook { Title = "Title", BasePath = Path.Join(source, "Title") };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            await AddTrackedFileAsync(
                db,
                audiobook,
                Path.Join(audiobook.BasePath!, "book.m4b"),
                source);
            rootId = root.Id;
        }

        var service = new RootFolderRelocationService(
            _factory,
            new FileSystemSemanticsResolver(),
            new ThrowingHubBroadcaster(),
            TimeProvider.System,
            new FilesystemMutationCoordinator(),
            _operationCoordinator,
            CreateMoveSourceManifestService(),
            TestLibraryFilesystemReadiness.Ready());
        var result = await service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Library",
                false,
                FileSystemCaseSensitivityMode.Auto));

        Assert.NotNull(result.RelocationId);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Single(await verification.RootFolderRelocations.ToListAsync());
        Assert.Single(await verification.MoveJobs.ToListAsync());
    }

    [Fact]
    public async Task StartRelocation_RequestCanceledDuringBroadcast_ReturnsCommittedSaga()
    {
        var source = Path.Join(Path.GetTempPath(), $"broadcast-cancel-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"broadcast-cancel-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder { Name = "Library", Path = source };
            var audiobook = new Audiobook
            {
                Title = "Title",
                BasePath = Path.Join(source, "Title")
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            await AddTrackedFileAsync(
                db,
                audiobook,
                Path.Join(audiobook.BasePath!, "book.m4b"),
                source);
            rootId = root.Id;
        }

        using var cancellation = new CancellationTokenSource();
        var service = new RootFolderRelocationService(
            _factory,
            new FileSystemSemanticsResolver(),
            new CancelingHubBroadcaster(cancellation),
            TimeProvider.System,
            new FilesystemMutationCoordinator(),
            _operationCoordinator,
            CreateMoveSourceManifestService(),
            TestLibraryFilesystemReadiness.Ready());

        var result = await service.StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Library",
                false,
                FileSystemCaseSensitivityMode.Auto),
            cancellation.Token);

        Assert.NotNull(result.RelocationId);
        Assert.True(cancellation.IsCancellationRequested);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Single(await verification.RootFolderRelocations.ToListAsync());
        Assert.Single(await verification.MoveJobs.ToListAsync());
    }

    [Fact]
    public async Task StartRelocation_ActiveMoveBoundaryUsesPersistedInsensitiveSemanticsWhenProbeIsSensitive()
    {
        var parent = Path.Join(Path.GetTempPath(), $"persisted-active-boundary-{Guid.NewGuid():N}");
        var source = Path.Join(parent, "Library");
        var target = Path.Join(parent, "Moved");
        Directory.CreateDirectory(source);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                ResolvedCaseSensitivity = FileSystemCaseSensitivity.Insensitive,
                PathIdentityState = PathIdentityState.Valid
            };
            var unrelatedAudiobook = new Audiobook
            {
                Title = "Unrelated",
                BasePath = Path.Join(Path.GetTempPath(), $"unrelated-{Guid.NewGuid():N}")
            };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(unrelatedAudiobook);
            await db.SaveChangesAsync();
            rootId = root.Id;
            db.MoveJobs.Add(new MoveJob
            {
                AudiobookId = unrelatedAudiobook.Id,
                SourcePath = Path.Join(source.ToLowerInvariant(), "Other"),
                RequestedPath = Path.Join(Path.GetTempPath(), $"unrelated-target-{Guid.NewGuid():N}"),
                Status = MoveJobStatus.Queued,
                EnqueuedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var semanticsResolver = new Mock<IFileSystemSemanticsResolver>();
        semanticsResolver.Setup(resolver => resolver.ResolveAsync(
                It.IsAny<string>(),
                It.IsAny<FileSystemCaseSensitivityMode>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, FileSystemCaseSensitivityMode, CancellationToken>((path, _, _) =>
                ValueTask.FromResult(new FileSystemSemanticsResolution(
                    new FileSystemPathSemantics(
                        FileSystemPathSemantics.CurrentHostDefault.Syntax,
                        FileSystemCaseSensitivity.Sensitive),
                    PathIdentityState.Valid,
                    Path.GetPathRoot(path) ?? path)));

        var exception = await Assert.ThrowsAsync<RootFolderPathChangeRejectedException>(() =>
            CreateService(semanticsResolver: semanticsResolver.Object).StartAsync(
                rootId,
                new RootFolderPathChangeCommand(
                    target,
                    RootFolderRelocationMode.Relocate,
                    true,
                    "Moved Library",
                    false,
                    FileSystemCaseSensitivityMode.Auto)));

        Assert.Equal("root_folder_move_recovery_blocked", exception.Code);
        Assert.Contains("unresolved move job", exception.Message, StringComparison.OrdinalIgnoreCase);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
        Assert.Single(await verification.MoveJobs.ToListAsync());
    }

    [Fact]
    public async Task StartRelocation_RejectsOverlappingFailedPublishedMove()
    {
        var source = Path.Join(Path.GetTempPath(), $"failed-move-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"failed-move-target-{Guid.NewGuid():N}");
        var audiobookPath = Path.Join(source, "Author", "Title");
        Directory.CreateDirectory(audiobookPath);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto
            };
            var audiobook = new Audiobook { Title = "Title", BasePath = audiobookPath };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            rootId = root.Id;
            db.MoveJobs.Add(new MoveJob
            {
                AudiobookId = audiobook.Id,
                SourcePath = audiobookPath,
                RequestedPath = Path.Join(target, "Author", "Title"),
                Status = MoveJobStatus.Failed,
                Phase = MoveJobPhase.Published,
                FailureKind = MoveFailureKind.Unknown,
                EnqueuedAt = DateTime.UtcNow,
                Entries =
                [
                    new MoveJobEntry
                    {
                        RelativePath = "book.m4b",
                        EntryType = MoveJobEntryType.File,
                        Length = 1,
                        LastWriteTimeUtc = DateTime.UnixEpoch,
                        Sha256 = new string('A', 64),
                        CopyState = MoveJobEntryCopyState.Verified,
                        CleanupState = MoveJobEntryCleanupState.Deleted
                    }
                ]
            });
            await db.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<RootFolderPathChangeRejectedException>(() =>
            CreateService().StartAsync(
                rootId,
                new RootFolderPathChangeCommand(
                    target,
                    RootFolderRelocationMode.Relocate,
                    true,
                    "Renamed Library",
                    false,
                    FileSystemCaseSensitivityMode.Auto)));

        Assert.Equal("root_folder_move_recovery_blocked", exception.Code);
        Assert.Contains("unresolved move job", exception.Message, StringComparison.OrdinalIgnoreCase);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
    }

    [WindowsFact]
    public async Task StartRelocation_RejectsActiveMoveWithDeviceAliasSourceUnderRoot()
    {
        var source = Path.Join(Path.GetTempPath(), $"active-move-device-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"active-move-device-target-{Guid.NewGuid():N}");
        var audiobookPath = Path.Join(source, "Author", "Title");
        var unrelatedPath = Path.Join(Path.GetTempPath(), $"active-move-device-unrelated-{Guid.NewGuid():N}");
        Directory.CreateDirectory(audiobookPath);
        Directory.CreateDirectory(unrelatedPath);
        int rootId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive
            };
            var audiobook = new Audiobook { Title = "Title", BasePath = audiobookPath };
            var unrelatedAudiobook = new Audiobook
            {
                Title = "Unrelated",
                BasePath = unrelatedPath
            };
            db.RootFolders.Add(root);
            db.Audiobooks.AddRange(audiobook, unrelatedAudiobook);
            await db.SaveChangesAsync();
            rootId = root.Id;

            db.MoveJobs.Add(new MoveJob
            {
                AudiobookId = unrelatedAudiobook.Id,
                SourcePath = @"\\?\" + Path.Join(source, "Other"),
                RequestedPath = Path.Join(unrelatedPath, "Moved"),
                Status = MoveJobStatus.Queued,
                EnqueuedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<RootFolderPathChangeRejectedException>(() =>
            CreateService().StartAsync(
                rootId,
                new RootFolderPathChangeCommand(
                    target,
                    RootFolderRelocationMode.Relocate,
                    true,
                    "Renamed Library",
                    false,
                    FileSystemCaseSensitivityMode.Insensitive)));

        Assert.Equal("root_folder_move_recovery_blocked", exception.Code);
        Assert.Contains("unresolved move job", exception.Message, StringComparison.OrdinalIgnoreCase);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
        Assert.Single(await verification.MoveJobs.ToListAsync());
    }

    [Theory]
    [InlineData("audiobook")]
    [InlineData("source")]
    [InlineData("target")]
    [InlineData("requested-source")]
    [InlineData("source-target")]
    public async Task StartRelocation_RejectsOverlappingActiveStandaloneMove(string conflictKind)
    {
        var source = Path.Join(Path.GetTempPath(), $"active-move-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"active-move-target-{Guid.NewGuid():N}");
        var audiobookPath = Path.Join(source, "Author", "Title");
        Directory.CreateDirectory(audiobookPath);
        int rootId;
        int audiobookId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var root = new RootFolder
            {
                Name = "Library",
                Path = source,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive
            };
            var audiobook = new Audiobook { Title = "Title", BasePath = audiobookPath };
            db.RootFolders.Add(root);
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            rootId = root.Id;
            audiobookId = audiobook.Id;

            db.MoveJobs.Add(new MoveJob
            {
                AudiobookId = conflictKind == "audiobook" ? audiobookId : audiobookId + 1000,
                SourcePath = conflictKind switch
                {
                    "source" => Path.Join(source.ToUpperInvariant(), "OTHER"),
                    "source-target" => Path.Join(target.ToUpperInvariant(), "OTHER"),
                    _ => Path.Join(Path.GetTempPath(), $"unrelated-source-{Guid.NewGuid():N}")
                },
                RequestedPath = conflictKind switch
                {
                    "target" => Path.Join(target.ToUpperInvariant(), "OTHER"),
                    "requested-source" => Path.Join(source.ToUpperInvariant(), "OTHER"),
                    _ => Path.Join(Path.GetTempPath(), $"unrelated-target-{Guid.NewGuid():N}")
                },
                Status = MoveJobStatus.Queued,
                EnqueuedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<RootFolderPathChangeRejectedException>(() => CreateService().StartAsync(
            rootId,
            new RootFolderPathChangeCommand(
                target,
                RootFolderRelocationMode.Relocate,
                true,
                "Renamed Library",
                false,
                FileSystemCaseSensitivityMode.Insensitive)));

        Assert.Equal("root_folder_move_recovery_blocked", exception.Code);
        Assert.Contains("unresolved audiobook move", exception.PublicMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unresolved move job", exception.Message, StringComparison.OrdinalIgnoreCase);
        await using var verification = await _factory.CreateDbContextAsync();
        Assert.Empty(await verification.RootFolderRelocations.ToListAsync());
        Assert.Empty(await verification.RootFolderRelocationSkippedItems.ToListAsync());
        Assert.Single(await verification.MoveJobs.ToListAsync());
        Assert.Equal(source, (await verification.RootFolders.SingleAsync()).Path);
        Assert.Equal(audiobookPath, (await verification.Audiobooks.SingleAsync()).BasePath);
    }

    [WindowsFact]
    public async Task IsBoundaryProtectedAsync_MetadataOnlyForeignSource_ProtectsSourceWithoutFreezingWindowsTarget()
    {
        const string source = "/server/mnt/drive/Audiobooks";
        var target = Path.Join(TempRoot, $"foreign-metadata-target-{Guid.NewGuid():N}");
        await SeedActiveRelocationAsync(
            source,
            target,
            FileSystemCaseSensitivityMode.Sensitive,
            FileSystemCaseSensitivityMode.Insensitive,
            RootFolderRelocationMode.MetadataOnly);
        var service = CreateService();

        Assert.True(await service.IsBoundaryProtectedAsync(
            source + "/Author/Book",
            new FileSystemPathSemantics(
                FileSystemPathSyntax.Unix,
                FileSystemCaseSensitivity.Sensitive)));
        Assert.False(await service.IsBoundaryProtectedAsync(
            Path.Join(target, "Author", "Book"),
            new FileSystemPathSemantics(
                FileSystemPathSyntax.Windows,
                FileSystemCaseSensitivity.Insensitive)));
    }

    [Fact]
    public async Task IsBoundaryProtectedAsync_MetadataOnlyAmbiguousSource_UsesTargetSyntaxContext()
    {
        var source = $"//legacy/library-{Guid.NewGuid():N}";
        var target = Path.Join(TempRoot, $"ambiguous-metadata-target-{Guid.NewGuid():N}");
        Assert.False(FileSystemPathIdentity.TryDetectAbsoluteSyntax(source, out _));
        await SeedActiveRelocationAsync(
            source,
            target,
            FileSystemCaseSensitivityMode.Sensitive,
            FileSystemCaseSensitivityMode.Sensitive,
            RootFolderRelocationMode.MetadataOnly);
        var service = CreateService();
        var targetSyntax = FileSystemPathSemantics.CurrentHostDefault.Syntax;
        Assert.True(FileSystemPathIdentity.TryDetectAbsoluteSyntax(
            source,
            targetSyntax,
            out _));
        var sourceSemantics = new FileSystemPathSemantics(
            targetSyntax,
            FileSystemCaseSensitivity.Sensitive);

        Assert.True(await service.IsBoundaryProtectedAsync(
            source + "/Author/Book",
            sourceSemantics));
        Assert.False(await service.IsBoundaryProtectedAsync(
            Path.Join(target, "Author", "Book"),
            FileSystemPathSemantics.CurrentHostDefault));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IsBoundaryProtectedAsync_HonorsPersistedInsensitiveBoundaryMode(bool useSourceBoundary)
    {
        var protectedPath = Path.Join(TempRoot, useSourceBoundary ? "SourceBoundary" : "TargetBoundary");
        var otherPath = Path.Join(TempRoot, useSourceBoundary ? "TargetBoundary" : "SourceBoundary");
        await SeedActiveRelocationAsync(
            useSourceBoundary ? protectedPath : otherPath,
            useSourceBoundary ? otherPath : protectedPath,
            FileSystemCaseSensitivityMode.Insensitive,
            FileSystemCaseSensitivityMode.Insensitive);
        var service = CreateService();
        var caseDistinctPath = Path.Join(
            Path.GetDirectoryName(protectedPath)!,
            Path.GetFileName(protectedPath).ToUpperInvariant(),
            "Book");

        var protectedResult = await service.IsBoundaryProtectedAsync(
            caseDistinctPath,
            new FileSystemPathSemantics(
                FileSystemPathSemantics.CurrentHostDefault.Syntax,
                FileSystemCaseSensitivity.Sensitive));

        Assert.True(protectedResult);
    }

    [Fact]
    public async Task IsBoundaryProtectedAsync_PreservesCaseDistinctSensitiveBoundary()
    {
        var protectedPath = Path.Join(TempRoot, "CaseSensitiveBoundary");
        await SeedActiveRelocationAsync(
            protectedPath,
            Path.Join(TempRoot, "Target"),
            FileSystemCaseSensitivityMode.Sensitive,
            FileSystemCaseSensitivityMode.Sensitive);
        var service = CreateService();
        var caseDistinctPath = Path.Join(
            Path.GetDirectoryName(protectedPath)!,
            Path.GetFileName(protectedPath).ToUpperInvariant(),
            "Book");

        var protectedResult = await service.IsBoundaryProtectedAsync(
            caseDistinctPath,
            new FileSystemPathSemantics(
                FileSystemPathSemantics.CurrentHostDefault.Syntax,
                FileSystemCaseSensitivity.Sensitive));

        Assert.False(protectedResult);
    }

    [Fact]
    public async Task IsBoundaryProtectedAsync_FailsClosedWhenBoundarySemanticsAreUnavailable()
    {
        var protectedPath = Path.Join(TempRoot, "UnavailableBoundary");
        await SeedActiveRelocationAsync(
            protectedPath,
            Path.Join(TempRoot, "Target"),
            FileSystemCaseSensitivityMode.Auto,
            FileSystemCaseSensitivityMode.Sensitive);
        var service = new RootFolderRelocationService(
            _factory,
            new TargetUnavailableSemanticsResolver(protectedPath),
            new NoopHubBroadcaster(),
            TimeProvider.System,
            new FilesystemMutationCoordinator(),
            _operationCoordinator,
            CreateMoveSourceManifestService(),
            TestLibraryFilesystemReadiness.Ready());
        var caseDistinctPath = Path.Join(
            Path.GetDirectoryName(protectedPath)!,
            Path.GetFileName(protectedPath).ToUpperInvariant(),
            "Book");

        var protectedResult = await service.IsBoundaryProtectedAsync(
            caseDistinctPath,
            new FileSystemPathSemantics(
                FileSystemPathSemantics.CurrentHostDefault.Syntax,
                FileSystemCaseSensitivity.Sensitive));

        Assert.True(protectedResult);
    }

    [WindowsFact]
    public async Task IsBoundaryProtectedAsync_DeviceAliasActiveSource_BlocksOrdinaryChild()
    {
        var physicalSource = Path.Join(
            TempRoot,
            $"active-boundary-device-source-{Guid.NewGuid():N}");
        var deviceAliasSource = @"\\?\" + physicalSource;
        var target = Path.Join(
            TempRoot,
            $"active-boundary-device-target-{Guid.NewGuid():N}");
        await SeedActiveRelocationAsync(
            deviceAliasSource,
            target,
            FileSystemCaseSensitivityMode.Insensitive,
            FileSystemCaseSensitivityMode.Insensitive);

        Assert.True(await CreateService().IsBoundaryProtectedAsync(
            Path.Join(physicalSource, "Book"),
            new FileSystemPathSemantics(
                FileSystemPathSyntax.Windows,
                FileSystemCaseSensitivity.Insensitive)));
    }

    [Theory]
    [InlineData("child")]
    [InlineData("parent")]
    public async Task IsBoundaryProtectedAsync_BlocksContainmentInEitherDirection(string relationship)
    {
        var protectedPath = Path.Join(TempRoot, "Boundary", "Nested");
        await SeedActiveRelocationAsync(
            protectedPath,
            Path.Join(TempRoot, "Target"),
            FileSystemCaseSensitivityMode.Sensitive,
            FileSystemCaseSensitivityMode.Sensitive);
        var candidate = relationship == "child"
            ? Path.Join(protectedPath, "Book")
            : Path.GetDirectoryName(protectedPath)!;

        Assert.True(await CreateService().IsBoundaryProtectedAsync(
            candidate,
            new FileSystemPathSemantics(
                FileSystemPathSemantics.CurrentHostDefault.Syntax,
                FileSystemCaseSensitivity.Sensitive)));
    }

    private async Task SeedActiveRelocationAsync(
        string sourcePath,
        string targetPath,
        FileSystemCaseSensitivityMode sourceMode,
        FileSystemCaseSensitivityMode targetMode,
        RootFolderRelocationMode mode = RootFolderRelocationMode.Relocate)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var root = new RootFolder { Name = $"Root-{Guid.NewGuid():N}", Path = sourcePath };
        db.RootFolders.Add(root);
        await db.SaveChangesAsync();
        db.RootFolderRelocations.Add(new RootFolderRelocation
        {
            RootFolderId = root.Id,
            ActiveRootFolderId = root.Id,
            SourcePath = sourcePath,
            SourceCaseSensitivityMode = sourceMode,
            TargetPath = targetPath,
            TargetCaseSensitivityMode = targetMode,
            Mode = mode,
            DesiredName = root.Name,
            Status = RootFolderRelocationStatus.Running
        });
        await db.SaveChangesAsync();
    }

    private sealed record OwnershipMigrationScenario(
        long OwnershipId,
        Guid RelocationId,
        string RootPath,
        string OwnedPath,
        string SourceOwnershipKey,
        string TargetOwnershipKey);

    private async Task<OwnershipMigrationScenario>
        SeedPublishedOwnershipMigrationAsync()
    {
        var rootPath = Path.Join(
            TempRoot,
            $"ownership-recovery-root-{Guid.NewGuid():N}");
        var ownedPath = Path.Join(rootPath, "Book");
        Directory.CreateDirectory(ownedPath);
        var sourceResolution = await new FileSystemSemanticsResolver()
            .ResolveAsync(ownedPath);
        Assert.Equal(PathIdentityState.Valid, sourceResolution.State);
        var targetSemantics = new FileSystemPathSemantics(
            sourceResolution.Semantics.Syntax,
            FileSystemCaseSensitivity.Sensitive);
        var rootIdentity = await new DirectoryObjectIdentityResolver()
            .ResolveAsync(rootPath);
        Assert.True(rootIdentity.IsAvailable, rootIdentity.UnavailableReason);
        using var ownedAnchor =
            PinnedDirectoryCreation.OpenPinnedBoundary(ownedPath);
        var ownershipToken = Guid.NewGuid().ToString("N");
        var ownershipIdentity = ManagedDirectoryIdentity.Create(
            ownershipToken,
            ownedAnchor.GetDirectoryObjectIdentity());

        await using var db = await _factory.CreateDbContextAsync();
        var root = new RootFolder
        {
            Name = "Library",
            Path = rootPath,
            DirectoryObjectIdentityVersion = rootIdentity.Version,
            DirectoryObjectIdentity = rootIdentity.Value,
            DirectoryObjectIdentityUnavailableReason =
                rootIdentity.UnavailableReason
        };
        var audiobook = new Audiobook
        {
            Title = "Book",
            BasePath = ownedPath
        };
        db.RootFolders.Add(root);
        db.Audiobooks.Add(audiobook);
        await db.SaveChangesAsync();

        var sourceOwnershipKey = FileSystemPathIdentity.CreateKey(
            "library-directory",
            ownedPath,
            sourceResolution.Semantics);
        var ownership = new LibraryDirectoryOwnership
        {
            Path = ownedPath,
            CanonicalPath = ownedPath,
            PathSyntax = sourceResolution.Semantics.Syntax,
            PathCaseSensitivity =
                sourceResolution.Semantics.CaseSensitivity,
            PathCaseSensitivityMode =
                FileSystemCaseSensitivityMode.Auto,
            PathIdentityBoundary = ownedPath,
            PathIdentityLookupKey =
                FileSystemPathIdentity.CreateLookupKey(
                    "library-directory",
                    ownedPath,
                    sourceResolution.Semantics.Syntax),
            PathOwnershipKey = sourceOwnershipKey,
            OwnershipToken = ownershipToken,
            State = LibraryDirectoryOwnershipState.Owned,
            CreationWorkflow = "Test",
            AudiobookId = audiobook.Id,
            ManagedRootFolderId = root.Id,
            DirectoryObjectIdentityVersion =
                ManagedDirectoryIdentity.CurrentVersion,
            DirectoryObjectIdentity = ownershipIdentity
        };
        db.LibraryDirectoryOwnerships.Add(ownership);
        var relocation = new RootFolderRelocation
        {
            RootFolderId = root.Id,
            ActiveRootFolderId = root.Id,
            SourcePath = rootPath,
            TargetPath = rootPath,
            Mode = RootFolderRelocationMode.MetadataOnly,
            Status = RootFolderRelocationStatus.NeedsAttention,
            DesiredName = "Renamed Library",
            TargetCaseSensitivityMode =
                FileSystemCaseSensitivityMode.Sensitive,
            TargetIdentityEnrollmentState =
                TargetIdentityEnrollmentState.Authorized,
            TargetDirectoryObjectIdentityVersion =
                rootIdentity.Version,
            TargetDirectoryObjectIdentity = rootIdentity.Value
        };
        db.RootFolderRelocations.Add(relocation);
        await db.SaveChangesAsync();
        var targetOwnershipKey = FileSystemPathIdentity.CreateKey(
            "library-directory",
            ownedPath,
            targetSemantics);
        db.LibraryDirectoryOwnershipPathMigrations.Add(
            new LibraryDirectoryOwnershipPathMigration
            {
                OwnershipId = ownership.Id,
                RelocationId = relocation.Id,
                SourceCanonicalPath = ownedPath,
                SourcePathSyntax = sourceResolution.Semantics.Syntax,
                SourceCaseSensitivity =
                    sourceResolution.Semantics.CaseSensitivity,
                SourceCaseSensitivityMode =
                    FileSystemCaseSensitivityMode.Auto,
                SourceIdentityBoundary = ownedPath,
                SourceIdentityLookupKey =
                    ownership.PathIdentityLookupKey,
                SourceOwnershipKey = sourceOwnershipKey,
                TargetCanonicalPath = ownedPath,
                TargetPathSyntax = targetSemantics.Syntax,
                TargetCaseSensitivity =
                    targetSemantics.CaseSensitivity,
                TargetCaseSensitivityMode =
                    FileSystemCaseSensitivityMode.Sensitive,
                TargetIdentityBoundary = ownedPath,
                TargetIdentityLookupKey =
                    FileSystemPathIdentity.CreateLookupKey(
                        "library-directory",
                        ownedPath,
                        targetSemantics.Syntax),
                TargetOwnershipKey = targetOwnershipKey
            });
        await db.SaveChangesAsync();
        return new OwnershipMigrationScenario(
            ownership.Id,
            relocation.Id,
            rootPath,
            ownedPath,
            sourceOwnershipKey,
            targetOwnershipKey);
    }

    private async Task<(int RootId, int AudiobookId, string Source, string Target)>
        SeedRelocationScenarioAsync()
    {
        var source = OperatingSystem.IsWindows()
            ? FileService.GetWindowsRootRelativeTempPath("relocation-source")
            : Path.Join(Path.GetTempPath(), $"relocation-source-{Guid.NewGuid():N}");
        var target = OperatingSystem.IsWindows()
            ? FileService.GetWindowsRootRelativeTempPath("relocation-target")
            : Path.Join(Path.GetTempPath(), $"relocation-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Join(source, "Author", "Title"));
        Directory.CreateDirectory(target);
        await using var db = await _factory.CreateDbContextAsync();
        var root = new RootFolder { Name = "Library", Path = source };
        var audiobook = new Audiobook
        {
            Title = "Title",
            BasePath = Path.Join(source, "Author", "Title")
        };
        db.RootFolders.Add(root);
        db.Audiobooks.Add(audiobook);
        await db.SaveChangesAsync();
        await AddTrackedFileAsync(
            db,
            audiobook,
            Path.Join(audiobook.BasePath!, "book.m4b"),
            source);
        return (root.Id, audiobook.Id, source, target);
    }

    private static RootFolderPathChangeCommand BuildRelocationCommand(string target) => new(
        target,
        RootFolderRelocationMode.Relocate,
        true,
        "Moved Library",
        false,
        FileSystemCaseSensitivityMode.Auto);

    private async Task<(Guid RelocationId, int RootId)> SeedRetryableRelocationAsync()
    {
        var source = Path.Join(Path.GetTempPath(), $"retry-source-{Guid.NewGuid():N}");
        var target = Path.Join(Path.GetTempPath(), $"retry-target-{Guid.NewGuid():N}");
        await using var db = await _factory.CreateDbContextAsync();
        var root = new RootFolder { Name = "Library", Path = source };
        db.RootFolders.Add(root);
        await db.SaveChangesAsync();
        var relocation = new RootFolderRelocation
        {
            RootFolderId = root.Id,
            ActiveRootFolderId = root.Id,
            SourcePath = source,
            TargetPath = target,
            Mode = RootFolderRelocationMode.MetadataOnly,
            Status = RootFolderRelocationStatus.NeedsAttention,
            DesiredName = root.Name,
            Error = "Retry required."
        };
        db.RootFolderRelocations.Add(relocation);
        await db.SaveChangesAsync();
        return (relocation.Id, root.Id);
    }

    private static async Task AddTrackedFileAsync(
        ListenArrDbContext db,
        Audiobook audiobook,
        string path,
        string boundary,
        FileSystemPathSemantics? semantics = null,
        FileSystemCaseSensitivityMode requestedMode = FileSystemCaseSensitivityMode.Auto)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "audio");
        var resolvedSemantics = semantics;
        if (!resolvedSemantics.HasValue)
        {
            var resolution = await new FileSystemSemanticsResolver().ResolveAsync(path);
            Assert.Equal(PathIdentityState.Valid, resolution.State);
            resolvedSemantics = resolution.Semantics;
        }

        var identity = AudiobookFilePathIdentity.CreateValid(
            path,
            resolvedSemantics.Value,
            requestedMode,
            boundary);
        var trackedFile = AudiobookFile.CreateUnresolved(path);
        trackedFile.AudiobookId = audiobook.Id;
        trackedFile.ApplyPathIdentity(path, identity);
        using (var parent = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
            Path.GetDirectoryName(path)!,
            createMissing: false))
        using (var file = parent.OpenExistingFileForStableRead(Path.GetFileName(path)))
        {
            trackedFile.ApplyPhysicalObjectIdentity(
                file.GetObjectIdentity(),
                DateTime.UtcNow);
        }
        db.AudiobookFiles.Add(trackedFile);
        await db.SaveChangesAsync();
    }

    private sealed class FirstEntryPausingCoordinator : IFilesystemMutationCoordinator
    {
        private readonly FilesystemMutationCoordinator _inner = new();
        private readonly TaskCompletionSource _firstEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirst =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entries;

        public Task FirstEntered => _firstEntered.Task;

        public int EntryCount => Volatile.Read(ref _entries);

        public void ReleaseFirst() => _releaseFirst.TrySetResult();

        public Task ExecuteExclusiveAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default) =>
            _inner.ExecuteExclusiveAsync(
                token => PauseFirstThenExecuteAsync(operation, token),
                cancellationToken);

        public Task<T> ExecuteExclusiveAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default) =>
            _inner.ExecuteExclusiveAsync(
                token => PauseFirstThenExecuteAsync(operation, token),
                cancellationToken);

        private async Task PauseFirstThenExecuteAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _entries) == 1)
            {
                _firstEntered.TrySetResult();
                await _releaseFirst.Task.WaitAsync(cancellationToken);
            }

            await operation(cancellationToken);
        }

        private async Task<T> PauseFirstThenExecuteAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _entries) == 1)
            {
                _firstEntered.TrySetResult();
                await _releaseFirst.Task.WaitAsync(cancellationToken);
            }

            return await operation(cancellationToken);
        }
    }

    private sealed class TrackingCoordinator : IFilesystemMutationCoordinator
    {
        private readonly FilesystemMutationCoordinator _inner = new();
        private int _executing;

        public bool IsExecuting => Volatile.Read(ref _executing) != 0;

        public Task ExecuteExclusiveAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default) =>
            _inner.ExecuteExclusiveAsync(
                async token =>
                {
                    Interlocked.Increment(ref _executing);
                    try
                    {
                        await operation(token);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _executing);
                    }
                },
                cancellationToken);

        public Task<T> ExecuteExclusiveAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default) =>
            _inner.ExecuteExclusiveAsync(
                async token =>
                {
                    Interlocked.Increment(ref _executing);
                    try
                    {
                        return await operation(token);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _executing);
                    }
                },
                cancellationToken);
    }

    private sealed class RecordingHubBroadcaster(Func<bool> isCoordinatorExecuting) : IHubBroadcaster
    {
        public int BroadcastCount { get; private set; }
        public bool CoordinatorWasExecuting { get; private set; }
        public object? Payload { get; private set; }

        public Task BroadcastQueueUpdateAsync(QueueSnapshot queueSnapshot) => Task.CompletedTask;

        public Task BroadcastAsync(
            string method,
            object payload,
            CancellationToken cancellationToken = default)
        {
            BroadcastCount++;
            CoordinatorWasExecuting |= isCoordinatorExecuting();
            Payload = payload;
            return Task.CompletedTask;
        }

        public Task BroadcastAsync(
            RealtimeHubTarget target,
            string method,
            object payload,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private RootFolderRelocationService CreateService(
        IServiceScopeFactory? manifestScopeFactory = null,
        IFileSystemSemanticsResolver? semanticsResolver = null,
        IDirectoryObjectIdentityResolver? directoryObjectIdentityResolver = null,
        ILibraryFilesystemReadiness? filesystemReadiness = null,
        IFileRegistrationRecoveryProbe? fileRegistrationRecoveryProbe = null) => new(
        _factory,
        semanticsResolver ?? new FileSystemSemanticsResolver(),
        new NoopHubBroadcaster(),
        TimeProvider.System,
        new FilesystemMutationCoordinator(),
        _operationCoordinator,
        manifestScopeFactory ?? CreateMoveSourceManifestService(),
        filesystemReadiness ?? TestLibraryFilesystemReadiness.Ready(),
        directoryObjectIdentityResolver,
        fileRegistrationRecoveryProbe);

    private ManifestServiceScopeFactory CreateMoveSourceManifestService()
    {
        var repository = new Mock<IAudiobookFileRepository>();
        repository
            .Setup(candidate => candidate.GetByAudiobookIdAsync(
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns<int, CancellationToken>(async (audiobookId, cancellationToken) =>
            {
                await using var db = await _factory.CreateDbContextAsync(cancellationToken);
                return await db.AudiobookFiles
                    .AsNoTracking()
                    .Where(file => file.AudiobookId == audiobookId)
                    .ToListAsync(cancellationToken);
            });
        return new ManifestServiceScopeFactory(
            () => new MoveSourceManifestService(repository.Object));
    }

    private sealed class ManifestServiceScopeFactory(
        Func<IMoveSourceManifestService> serviceFactory) : IServiceScopeFactory
    {
        public int CreatedScopeCount { get; private set; }
        public int DisposedScopeCount { get; private set; }
        public int BuildCount { get; private set; }
        public List<IMoveSourceManifestService> ResolvedServices { get; } = [];

        public IServiceScope CreateScope()
        {
            CreatedScopeCount++;
            var service = new TrackingManifestService(
                serviceFactory(),
                () => BuildCount++);
            ResolvedServices.Add(service);
            return new ManifestServiceScope(
                service,
                () => DisposedScopeCount++);
        }

        private sealed class TrackingManifestService(
            IMoveSourceManifestService inner,
            Action onBuild) : IMoveSourceManifestService
        {
            public Task<MoveSourceManifest> BuildAsync(
                Audiobook audiobook,
                CancellationToken cancellationToken = default)
            {
                onBuild();
                return inner.BuildAsync(audiobook, cancellationToken);
            }
        }

        private sealed class ManifestServiceScope(
            IMoveSourceManifestService service,
            Action onDispose) : IServiceScope, IAsyncDisposable
        {
            private bool _disposed;

            public IServiceProvider ServiceProvider { get; } =
                new ManifestServiceProvider(service);

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                onDispose();
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }

        private sealed class ManifestServiceProvider(
            IMoveSourceManifestService service) : IServiceProvider
        {
            public object? GetService(Type serviceType) =>
                serviceType == typeof(IMoveSourceManifestService)
                    ? service
                    : null;
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<ListenArrDbContext> options)
        : IDbContextFactory<ListenArrDbContext>
    {
        public ListenArrDbContext CreateDbContext() => new(options);
        public Task<ListenArrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class SwitchableTargetSemanticsResolver(string targetPath)
        : IFileSystemSemanticsResolver
    {
        private readonly string _targetPath = Path.GetFullPath(targetPath);
        private readonly FileSystemSemanticsResolver _inner = new();

        public bool ReportOppositeTargetSemantics { get; set; }

        public async ValueTask<FileSystemSemanticsResolution> ResolveAsync(
            string path,
            FileSystemCaseSensitivityMode mode = FileSystemCaseSensitivityMode.Auto,
            CancellationToken cancellationToken = default)
        {
            var resolution = await _inner.ResolveAsync(
                path,
                mode,
                cancellationToken);
            if (!ReportOppositeTargetSemantics
                || resolution.State != PathIdentityState.Valid
                || !string.Equals(
                    Path.GetFullPath(path),
                    _targetPath,
                    FileSystemPathSemantics.CurrentHostDefault.CaseSensitivity
                        == FileSystemCaseSensitivity.Insensitive
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal))
            {
                return resolution;
            }

            var opposite = resolution.Semantics.CaseSensitivity
                == FileSystemCaseSensitivity.Sensitive
                    ? FileSystemCaseSensitivity.Insensitive
                    : FileSystemCaseSensitivity.Sensitive;
            return resolution with
            {
                Semantics = new FileSystemPathSemantics(
                    resolution.Semantics.Syntax,
                    opposite)
            };
        }
    }

    private sealed class SourceThrowingSemanticsResolver(string sourcePath) : IFileSystemSemanticsResolver
    {
        private readonly string _sourcePath = Path.GetFullPath(sourcePath);
        private readonly FileSystemSemanticsResolver _inner = new();

        public ValueTask<FileSystemSemanticsResolution> ResolveAsync(
            string path,
            FileSystemCaseSensitivityMode mode = FileSystemCaseSensitivityMode.Auto,
            CancellationToken cancellationToken = default)
        {
            var fullPath = Path.GetFullPath(path);
            if (string.Equals(
                fullPath,
                _sourcePath,
                FileSystemPathSemantics.CurrentHostDefault.CaseSensitivity == FileSystemCaseSensitivity.Insensitive
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
            {
                throw new IOException("simulated source resolution failure");
            }

            return _inner.ResolveAsync(path, mode, cancellationToken);
        }
    }

    private sealed class TargetUnavailableSemanticsResolver(string unavailablePath) : IFileSystemSemanticsResolver
    {
        private readonly string _unavailablePath = Path.GetFullPath(unavailablePath);
        private readonly FileSystemSemanticsResolver _inner = new();

        public ValueTask<FileSystemSemanticsResolution> ResolveAsync(
            string path,
            FileSystemCaseSensitivityMode mode = FileSystemCaseSensitivityMode.Auto,
            CancellationToken cancellationToken = default)
        {
            var fullPath = Path.GetFullPath(path);
            if (string.Equals(
                fullPath,
                _unavailablePath,
                FileSystemPathSemantics.CurrentHostDefault.CaseSensitivity == FileSystemCaseSensitivity.Insensitive
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
            {
                return ValueTask.FromResult(new FileSystemSemanticsResolution(
                    new FileSystemPathSemantics(
                        FileSystemPathSemantics.CurrentHostDefault.Syntax,
                        FileSystemCaseSensitivity.Unknown),
                    PathIdentityState.Unavailable,
                    fullPath,
                    "Target filesystem identity became unavailable during finalization."));
            }

            return _inner.ResolveAsync(path, mode, cancellationToken);
        }
    }

    private sealed class CancelingHubBroadcaster(
        CancellationTokenSource cancellation) : IHubBroadcaster
    {
        public Task BroadcastQueueUpdateAsync(QueueSnapshot queueSnapshot) => Task.CompletedTask;

        public Task BroadcastAsync(
            string method,
            object payload,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task BroadcastAsync(
            RealtimeHubTarget target,
            string method,
            object payload,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class ThrowingHubBroadcaster : IHubBroadcaster
    {
        public Task BroadcastQueueUpdateAsync(QueueSnapshot queueSnapshot) => Task.CompletedTask;

        public Task BroadcastAsync(
            string method,
            object payload,
            CancellationToken cancellationToken = default) =>
            throw new IOException("SignalR unavailable");

        public Task BroadcastAsync(
            RealtimeHubTarget target,
            string method,
            object payload,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
