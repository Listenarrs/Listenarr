using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving
{
    [Trait("Name", "AudiobookContentMoveServiceTests")]
    [Trait("Category", "BackgroundWorkers")]
    public partial class AudiobookContentMoveServiceTests : BaseTests
    {
        private const string TestLeaseOwner = "test-worker";

        private static MoveLeaseToken LeaseToken(int generation = 1) =>
            new(TestLeaseOwner, generation);

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            await ConfigureManagedTestRootAsync();
        }

        private async Task ConfigureManagedTestRootAsync()
        {
            var rootPath = Path.GetFullPath(FileService.GetTempPath());
            var rootIdentity = await _provider
                .GetRequiredService<IDirectoryObjectIdentityResolver>()
                .ResolveAsync(rootPath);
            Assert.True(rootIdentity.IsAvailable, rootIdentity.UnavailableReason);
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            if (await db.RootFolders.AnyAsync())
            {
                return;
            }

            db.RootFolders.Add(new RootFolder
            {
                Name = "Test library",
                Path = rootPath,
                ResolvedCaseSensitivity =
                    FileSystemPathSemantics.CurrentHostDefault.CaseSensitivity,
                PathIdentityState = PathIdentityState.Valid,
                DirectoryObjectIdentityVersion = rootIdentity.Version,
                DirectoryObjectIdentity = rootIdentity.Value,
                DirectoryObjectIdentityUnavailableReason =
                    rootIdentity.UnavailableReason
            });
            await db.SaveChangesAsync();
        }

        [Theory]
        [InlineData(FileSystemCaseSensitivity.Insensitive, true)]
        [InlineData(FileSystemCaseSensitivity.Sensitive, false)]
        public void ValidateTargetManifest_CaseOnlyEntriesFollowTargetSemantics(
            FileSystemCaseSensitivity targetCaseSensitivity,
            bool shouldReject)
        {
            var target = Path.Join(FileService.GetTempPath(), $"content-move-target-manifest-{Guid.NewGuid():N}");
            var targetSemantics = new FileSystemPathSemantics(
                FileSystemPathSemantics.CurrentHostDefault.Syntax,
                targetCaseSensitivity);
            var manifest = new[]
            {
                new MoveJobEntry { RelativePath = "Book.m4b", EntryType = MoveJobEntryType.File },
                new MoveJobEntry { RelativePath = "book.m4b", EntryType = MoveJobEntryType.File }
            };

            if (shouldReject)
            {
                var exception = Assert.Throws<MoveNeedsAttentionException>(() =>
                    AudiobookContentMoveService.ValidateTargetManifest(target, manifest, targetSemantics));
                Assert.Contains("cannot represent both", exception.Message);
            }
            else
            {
                AudiobookContentMoveService.ValidateTargetManifest(target, manifest, targetSemantics);
            }
        }

        [Theory]
        [InlineData("../escape.m4b")]
        [InlineData("/escape.m4b")]
        public void ValidateTargetManifest_RejectsEscapedEntries(string relativePath)
        {
            var target = Path.Join(FileService.GetTempPath(), $"content-move-target-escape-{Guid.NewGuid():N}");
            var manifest = new[]
            {
                new MoveJobEntry { RelativePath = relativePath, EntryType = MoveJobEntryType.File }
            };

            Assert.Throws<MoveNeedsAttentionException>(() =>
                AudiobookContentMoveService.ValidateTargetManifest(
                    target,
                    manifest,
                    FileSystemPathSemantics.CurrentHostDefault));
        }

        [Fact]
        public async Task MoveContentsAsync_NormalMove_MovesContentsAndDeletesSource()
        {
            var source = FileService.GetTempDirectory("content-move-normal-src");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var extras = Path.Join(source, "extras");
            Directory.CreateDirectory(extras);
            await FileService.GetFileAsync(extras, "cover.jpg", "image");
            var target = Path.Join(FileService.GetTempPath(), $"content-move-normal-dst-{Guid.NewGuid():N}");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var result = await service.MoveContentsAsync(
                await CreateLeasedMoveRequestAsync(source, target),
                CancellationToken.None);

            Assert.Equal(Path.GetFullPath(source), result.Source);
            Assert.Equal(Path.GetFullPath(target), result.Target);
            Assert.False(result.TargetInsideSource);
            Assert.False(result.SourceInsideTarget);
            Assert.False(Directory.Exists(source));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
            Assert.True(File.Exists(Path.Join(target, "extras", "cover.jpg")));
        }

        [Fact]
        public async Task MoveContentsAsync_NormalMoveTargetBoundaryReplaced_DoesNotPublishIntoReplacement()
        {
            var source = FileService.GetTempDirectory(
                "content-move-normal-target-replacement-src");
            var sourceFile = await FileService.GetFileAsync(
                source,
                "book.m4b",
                "original audio");
            var targetRoot = Path.Join(
                Path.GetTempPath(),
                $"listenarr-normal-move-authority-{Guid.NewGuid():N}");
            var displacedRoot = targetRoot + ".original";
            var target = Path.Join(targetRoot, "Author", "Book");
            Directory.CreateDirectory(targetRoot);
            try
            {
                var request = await CreateLeasedMoveRequestAsync(source, target);
                Directory.Move(targetRoot, displacedRoot);
                Directory.CreateDirectory(targetRoot);
                var foreignFile = Path.Join(targetRoot, "foreign.txt");
                await File.WriteAllTextAsync(
                    foreignFile,
                    "foreign generation");

                var service = _provider.GetRequiredService<AudiobookContentMoveService>();
                var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                    service.MoveContentsAsync(request, CancellationToken.None));

                Assert.Contains(
                    "target boundary",
                    exception.Message,
                    StringComparison.OrdinalIgnoreCase);
                Assert.True(File.Exists(sourceFile));
                Assert.Equal("original audio", await File.ReadAllTextAsync(sourceFile));
                Assert.False(Directory.Exists(target));
                Assert.Equal(
                    "foreign generation",
                    await File.ReadAllTextAsync(foreignFile));
            }
            finally
            {
                if (Directory.Exists(targetRoot))
                {
                    Directory.Delete(targetRoot, recursive: true);
                }
                if (Directory.Exists(displacedRoot))
                {
                    Directory.Delete(displacedRoot, recursive: true);
                }
            }
        }

        [Fact]
        public async Task MoveContentsAsync_ActiveRelocationTargetRootReplaced_DoesNotPublishIntoReplacement()
        {
            var source = FileService.GetTempDirectory(
                "content-move-relocation-target-replacement-src");
            var sourceFile = await FileService.GetFileAsync(
                source,
                "book.m4b",
                "original audio");
            var targetRoot = Path.Join(
                Path.GetTempPath(),
                $"listenarr-relocation-authority-{Guid.NewGuid():N}");
            var target = Path.Join(targetRoot, "Author", "Book");
            Directory.CreateDirectory(targetRoot);
            try
            {
                var targetIdentity = await _provider
                    .GetRequiredService<IDirectoryObjectIdentityResolver>()
                    .ResolveAsync(targetRoot);
                Assert.True(targetIdentity.IsAvailable, targetIdentity.UnavailableReason);
                var factory = _provider
                    .GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
                Guid relocationId;
                await using (var db = await factory.CreateDbContextAsync())
                {
                    var root = await db.RootFolders.SingleAsync();
                    var relocation = new RootFolderRelocation
                    {
                        RootFolderId = root.Id,
                        ActiveRootFolderId = root.Id,
                        SourcePath = root.Path,
                        TargetPath = targetRoot,
                        Mode = RootFolderRelocationMode.Relocate,
                        Status = RootFolderRelocationStatus.Running,
                        DesiredName = root.Name,
                        TargetIdentityEnrollmentState =
                            TargetIdentityEnrollmentState.Authorized,
                        TargetDirectoryObjectIdentityVersion = targetIdentity.Version,
                        TargetDirectoryObjectIdentity = targetIdentity.Value,
                        TargetDirectoryObjectIdentityUnavailableReason =
                            targetIdentity.UnavailableReason
                    };
                    db.RootFolderRelocations.Add(relocation);
                    await db.SaveChangesAsync();
                    relocationId = relocation.Id;
                }

                var request = await CreateLeasedMoveRequestAsync(source, target);
                await using (var db = await factory.CreateDbContextAsync())
                {
                    var job = await db.MoveJobs.SingleAsync(candidate =>
                        candidate.Id == request.JobId);
                    job.RelocationId = relocationId;
                    await db.SaveChangesAsync();
                }
                Directory.Delete(targetRoot, recursive: true);
                Directory.CreateDirectory(targetRoot);
                var foreignFile = Path.Join(targetRoot, "foreign.txt");
                await File.WriteAllTextAsync(foreignFile, "foreign generation");

                var service = _provider.GetRequiredService<AudiobookContentMoveService>();
                await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                    service.MoveContentsAsync(request, CancellationToken.None));

                Assert.True(File.Exists(sourceFile));
                Assert.Equal("original audio", await File.ReadAllTextAsync(sourceFile));
                Assert.False(Directory.Exists(target));
                Assert.Equal("foreign generation", await File.ReadAllTextAsync(foreignFile));
            }
            finally
            {
                if (Directory.Exists(targetRoot))
                {
                    Directory.Delete(targetRoot, recursive: true);
                }
            }
        }

        [LinuxFact]
        public async Task MoveContentsAsync_EndpointEqualityUsesBothFilesystemSemantics()
        {

            var parent = FileService.GetTempDirectory("content-move-endpoint-semantics");
            var source = Path.Join(parent, "Book");
            var target = Path.Join(parent, "book");
            Directory.CreateDirectory(source);
            var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceSemantics: new FileSystemPathSemantics(
                    FileSystemPathSyntax.Unix,
                    FileSystemCaseSensitivity.Sensitive),
                targetSemantics: new FileSystemPathSemantics(
                    FileSystemPathSyntax.Unix,
                    FileSystemCaseSensitivity.Insensitive));
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();

            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.Contains("distinct", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(sourceFile));
            Assert.False(Directory.Exists(target));
        }

        [Fact]
        public async Task MoveContentsAsync_StaleLeaseGeneration_DoesNotMutateFilesystem()
        {
            var source = FileService.GetTempDirectory("content-move-stale-lease-src");
            var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"content-move-stale-lease-dst-{Guid.NewGuid():N}");
            var jobId = Guid.NewGuid();
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.MoveJobs.Add(new MoveJob
                {
                    Id = jobId,
                    AudiobookId = 1,
                    RequestedPath = target,
                    SourcePath = source,
                    Status = MoveJobStatus.Running,
                    LeaseGeneration = 2,
                    ActiveDeduplicationKey = $"test:{jobId:N}"
                });
                await db.SaveChangesAsync();
            }

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            await Assert.ThrowsAsync<MoveLeaseLostException>(() => service.MoveContentsAsync(
                new AudiobookContentMoveRequest(
                    source,
                    target,
                    jobId,
                    true,
                    FileSystemPathSemantics.CurrentHostDefault,
                    FileSystemPathSemantics.CurrentHostDefault,
                    LeaseToken(1)),
                CancellationToken.None));

            Assert.True(File.Exists(sourceFile));
            Assert.False(Directory.Exists(target));
        }

        [Theory]
        [InlineData("owner")]
        [InlineData("generation")]
        [InlineData("expiration")]
        [InlineData("status")]
        public async Task MoveContentsAsync_InvalidLeaseState_DoesNotMutateFilesystem(string invalidState)
        {
            var source = FileService.GetTempDirectory($"content-move-invalid-lease-src-{invalidState}");
            var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"content-move-invalid-lease-dst-{invalidState}-{Guid.NewGuid():N}");
            var jobId = Guid.NewGuid();
            var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                var job = await db.MoveJobs.SingleAsync(job => job.Id == jobId);
                switch (invalidState)
                {
                    case "owner":
                        job.LeaseOwner = "other-worker";
                        break;
                    case "generation":
                        job.LeaseGeneration = 2;
                        break;
                    case "expiration":
                        job.LeaseExpiresAt = DateTime.UtcNow.AddSeconds(-1);
                        break;
                    case "status":
                        job.Status = MoveJobStatus.Completed;
                        break;
                }

                await db.SaveChangesAsync();
            }

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            await Assert.ThrowsAsync<MoveLeaseLostException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.True(File.Exists(sourceFile));
            Assert.False(Directory.Exists(target));
        }

        [Fact]
        public async Task MoveContentsAsync_DefaultLeaseGeneration_DoesNotMutateFilesystem()
        {
            var source = FileService.GetTempDirectory("content-move-zero-lease-src");
            var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"content-move-zero-lease-dst-{Guid.NewGuid():N}");
            var jobId = Guid.NewGuid();
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.MoveJobs.Add(new MoveJob
                {
                    Id = jobId,
                    AudiobookId = 1,
                    RequestedPath = target,
                    SourcePath = source,
                    Status = MoveJobStatus.Running,
                    LeaseGeneration = 1,
                    ActiveDeduplicationKey = $"test:{jobId:N}"
                });
                await db.SaveChangesAsync();
            }

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            await Assert.ThrowsAsync<MoveLeaseLostException>(() => service.MoveContentsAsync(
                new AudiobookContentMoveRequest(
                    source,
                    target,
                    jobId,
                    true,
                    FileSystemPathSemantics.CurrentHostDefault,
                    FileSystemPathSemantics.CurrentHostDefault,
                    LeaseToken(0)),
                CancellationToken.None));

            Assert.True(File.Exists(sourceFile));
            Assert.False(Directory.Exists(target));
        }

        [Fact]
        public async Task MoveContentsAsync_TargetInsideSource_MovesContentsIntoChildAndKeepsTarget()
        {
            var source = FileService.GetTempDirectory("content-move-child-src");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var extras = Path.Join(source, "extras");
            Directory.CreateDirectory(extras);
            await FileService.GetFileAsync(extras, "cover.jpg", "image");
            var target = Path.Join(source, " test");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var result = await service.MoveContentsAsync(
                await CreateLeasedMoveRequestAsync(source, target),
                CancellationToken.None);

            Assert.True(result.TargetInsideSource);
            Assert.False(result.SourceInsideTarget);
            Assert.True(Directory.Exists(source));
            Assert.True(Directory.Exists(target));
            Assert.False(File.Exists(Path.Join(source, "book.m4b")));
            Assert.False(Directory.Exists(extras));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
            Assert.True(File.Exists(Path.Join(target, "extras", "cover.jpg")));
        }

        [Fact]
        public async Task MoveContentsAsync_TargetDeepInsideSource_WithSiblingContent_FailsWithoutDeletingAnything()
        {
            var source = FileService.GetTempDirectory("content-move-deep-child-src");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var targetAncestor = Path.Join(source, "container");
            Directory.CreateDirectory(targetAncestor);
            await FileService.GetFileAsync(targetAncestor, "stale-sibling.txt", "stale");
            var target = Path.Join(targetAncestor, "target");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = await CreateLeasedMoveRequestAsync(source, target);
            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.Contains("unexpected content", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(source));
            Assert.False(Directory.Exists(target));
            Assert.True(File.Exists(Path.Join(source, "book.m4b")));
            Assert.True(File.Exists(Path.Join(targetAncestor, "stale-sibling.txt")));
        }

        [Fact]
        public async Task MoveContentsAsync_SourceInsideTarget_MovesContentsUpAndDeletesOldChild()
        {
            var target = FileService.GetTempDirectory("content-move-parent-target");
            var source = Path.Join(target, " test");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var result = await service.MoveContentsAsync(
                await CreateLeasedMoveRequestAsync(source, target),
                CancellationToken.None);

            Assert.False(result.TargetInsideSource);
            Assert.True(result.SourceInsideTarget);
            Assert.True(Directory.Exists(target));
            Assert.False(Directory.Exists(source));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task MoveContentsAsync_SourceInsideTarget_WithUnrelatedSibling_Fails()
        {
            var target = FileService.GetTempDirectory("content-move-parent-with-sibling-target");
            var source = Path.Join(target, "Title");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var sibling = Path.Join(target, "OtherBook");
            Directory.CreateDirectory(sibling);
            await FileService.GetFileAsync(sibling, "other.m4b", "other");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = await CreateLeasedMoveRequestAsync(source, target);
            var ex = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.Contains("unowned directory", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Join(source, "book.m4b")));
            Assert.True(File.Exists(Path.Join(sibling, "other.m4b")));
        }

        [Fact]
        public async Task MoveContentsAsync_SourceInsideEmptyParent_DoesNotDeleteParent()
        {
            var sourceParent = FileService.GetTempDirectory("content-move-empty-parent");
            var source = Path.Join(sourceParent, " test");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(FileService.GetTempPath(), $"content-move-cleaned-dst-{Guid.NewGuid():N}");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            await service.MoveContentsAsync(
                await CreateLeasedMoveRequestAsync(source, target),
                CancellationToken.None);

            Assert.False(Directory.Exists(source));
            Assert.True(Directory.Exists(sourceParent));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task FinalizeMove_WithCleanupBoundary_RemovesEmptyAncestorsButKeepsBoundary()
        {
            var sourceRoot = FileService.GetTempDirectory("content-move-cleanup-boundary");
            var source = Path.Join(sourceRoot, "Author", "Series", "Title", "test");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(FileService.GetTempPath(), $"content-move-cleanup-boundary-dst-{Guid.NewGuid():N}");
            await ClaimOwnedDirectoriesAsync(
                Path.Join(sourceRoot, "Author"),
                Path.Join(sourceRoot, "Author", "Series"),
                Path.Join(sourceRoot, "Author", "Series", "Title"));

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: sourceRoot);
            var result = await service.MoveContentsAsync(request, CancellationToken.None);

            Assert.True(Directory.Exists(Path.Join(sourceRoot, "Author")));

            await service.FinalizeMoveAsync(request, result, CancellationToken.None);

            Assert.True(Directory.Exists(sourceRoot));
            Assert.False(Directory.Exists(Path.Join(sourceRoot, "Author")));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task FinalizeMove_CommonAncestorFence_DoesNotDeleteUnownedSourceRoot()
        {
            var commonRoot = FileService.GetTempDirectory("content-move-cross-root-fence");
            var downloads = Path.Join(commonRoot, "downloads");
            var author = Path.Join(downloads, "Author");
            var source = Path.Join(author, "Book");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(commonRoot, "library", "Author", "Book");
            await ClaimOwnedDirectoriesAsync(author);

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: commonRoot);
            var result = await service.MoveContentsAsync(request, CancellationToken.None);
            await service.FinalizeMoveAsync(request, result, CancellationToken.None);

            Assert.False(Directory.Exists(source));
            Assert.False(Directory.Exists(author));
            Assert.True(Directory.Exists(downloads));
            Assert.True(Directory.Exists(commonRoot));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task FinalizeMove_SourceEqualsCleanupBoundary_PreservesBoundaryDirectory()
        {
            var source = FileService.GetTempDirectory("content-move-source-is-boundary");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(FileService.GetTempPath(), $"content-move-source-is-boundary-dst-{Guid.NewGuid():N}");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: source);
            var result = await service.MoveContentsAsync(request, CancellationToken.None);

            Assert.True(Directory.Exists(source));
            Assert.Empty(Directory.EnumerateFileSystemEntries(source));

            await service.FinalizeMoveAsync(request, result, CancellationToken.None);

            Assert.True(Directory.Exists(source));
            await service.CleanupCompletedMoveArtifactsAsync(request, result, CancellationToken.None);
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task FinalizeMove_ExistingEmptyTarget_PrunesSourceParentAfterNestedQuarantineCleanup()
        {
            var sourceRoot = FileService.GetTempDirectory("content-move-existing-target-root");
            var series = Path.Join(sourceRoot, "Matt Dinniman", "Dungeon Crawler Carl");
            var oldTitle = Path.Join(series, "A Parade of Horribles (2026)");
            var source = Path.Join(oldTitle, "test");
            var sourceDisc = Path.Join(source, "Disc 01");
            Directory.CreateDirectory(sourceDisc);
            await FileService.GetFileAsync(sourceDisc, "book.m4b", "audio");
            var target = Path.Join(series, "A Parade of Horribles (20262)", "test");
            Directory.CreateDirectory(target);
            await ClaimOwnedDirectoriesAsync(oldTitle);

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: sourceRoot);
            var result = await service.MoveContentsAsync(request, CancellationToken.None);

            Assert.False(Directory.Exists(source));
            Assert.True(Directory.Exists(oldTitle));
            Assert.Empty(Directory.EnumerateFileSystemEntries(oldTitle));

            await service.FinalizeMoveAsync(request, result, CancellationToken.None);

            Assert.False(Directory.Exists(oldTitle));
            await service.CleanupCompletedMoveArtifactsAsync(request, result, CancellationToken.None);
            Assert.True(File.Exists(Path.Join(target, "Disc 01", "book.m4b")));
            Assert.True(Directory.Exists(sourceRoot));
        }

        [LinuxFact]
        public async Task FinalizeMove_InsensitiveConfiguredRootAlias_PreservesPhysicalLibraryRoot()
        {

            var parent = FileService.GetTempDirectory("content-move-case-alias-parent");
            var physicalRoot = Path.Join(parent, "library");
            var configuredRoot = Path.Join(parent, "Library");
            var author = Path.Join(physicalRoot, "Author");
            var title = Path.Join(author, "Title");
            var source = Path.Join(title, "test");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(parent, "other", "Author", "Title", "test");
            await ClaimOwnedDirectoriesAsync(author, title);

            var semanticsResolver = new Mock<IFileSystemSemanticsResolver>();
            semanticsResolver
                .Setup(resolver => resolver.ResolveAsync(
                    It.IsAny<string>(),
                    It.IsAny<FileSystemCaseSensitivityMode>(),
                    It.IsAny<CancellationToken>()))
                .Returns((string path, FileSystemCaseSensitivityMode _, CancellationToken _) =>
                    ValueTask.FromResult(
                        string.Equals(path, configuredRoot, StringComparison.Ordinal)
                            ? new FileSystemSemanticsResolution(
                                new FileSystemPathSemantics(
                                    FileSystemPathSyntax.Unix,
                                    FileSystemCaseSensitivity.Insensitive),
                                PathIdentityState.Valid,
                                parent)
                            : new FileSystemSemanticsResolution(
                                new FileSystemPathSemantics(
                                    FileSystemPathSyntax.Unix,
                                    FileSystemCaseSensitivity.Sensitive),
                                PathIdentityState.Valid,
                                Path.GetPathRoot(path) ?? path)));
            var boundaryResolver = new MoveCleanupBoundaryResolver(semanticsResolver.Object);
            var boundary = await boundaryResolver.ResolveAsync(
                source,
                target,
                [new RootFolder
                {
                    Name = "Library",
                    Path = configuredRoot,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive
                }],
                configuredRoot);
            Assert.True(boundary.IsAvailable, boundary.Reason);
            Assert.Equal(physicalRoot, boundary.Boundary);

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: boundary.Boundary);
            var result = await service.MoveContentsAsync(request, CancellationToken.None);
            await service.FinalizeMoveAsync(request, result, CancellationToken.None);

            Assert.False(Directory.Exists(source));
            Assert.False(Directory.Exists(title));
            Assert.False(Directory.Exists(author));
            Assert.True(Directory.Exists(physicalRoot));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
            await service.CleanupCompletedMoveArtifactsAsync(request, result, CancellationToken.None);
        }

        [Fact]
        public async Task FinalizeMove_MissingCleanupBoundary_PreservesUnownedParentsAndCompletes()
        {
            var sourceRoot = FileService.GetTempDirectory("content-move-missing-boundary-root");
            var oldTitle = Path.Join(sourceRoot, "Author", "Old Title");
            var source = Path.Join(oldTitle, "test");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(FileService.GetTempPath(), $"content-move-missing-boundary-dst-{Guid.NewGuid():N}");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = await CreateLeasedMoveRequestAsync(source, target);
            var result = await service.MoveContentsAsync(request, CancellationToken.None);

            await service.FinalizeMoveAsync(request, result, CancellationToken.None);

            Assert.True(Directory.Exists(oldTitle));
            Assert.False(Directory.Exists(source));
            await service.CleanupCompletedMoveArtifactsAsync(request, result, CancellationToken.None);
        }

        [Fact]
        public async Task FinalizeMove_RetryAfterImmediateParentRemoved_PrunesHigherEmptyAncestors()
        {
            var sourceRoot = FileService.GetTempDirectory("content-move-finalize-retry-root");
            var author = Path.Join(sourceRoot, "Author");
            var oldTitle = Path.Join(author, "Old Title");
            var source = Path.Join(oldTitle, "test");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(FileService.GetTempPath(), $"content-move-finalize-retry-dst-{Guid.NewGuid():N}");
            await ClaimOwnedDirectoriesAsync(author, oldTitle);

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: sourceRoot);
            var result = await service.MoveContentsAsync(request, CancellationToken.None);

            var ownershipStore = _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>();
            var resolution = await ownershipStore.ResolveOwnedAsync(
                oldTitle,
                FileSystemPathSemantics.CurrentHostDefault);
            var ownership = Assert.IsType<LibraryDirectoryOwnership>(resolution.Ownership);
            var ownershipKey = Assert.IsType<string>(ownership.PathOwnershipKey);
            await ownershipStore.BeginRemovalAsync(ownership.Id, ownershipKey);
            Directory.Delete(oldTitle, false);
            Assert.True(Directory.Exists(author));
            Assert.False(Directory.Exists(oldTitle));

            await service.FinalizeMoveAsync(request, result, CancellationToken.None);

            Assert.False(Directory.Exists(author));
            Assert.True(Directory.Exists(sourceRoot));
            await service.CleanupCompletedMoveArtifactsAsync(request, result, CancellationToken.None);
        }

        [Fact]
        public async Task FinalizeMove_LiveRemovingEmptyPath_CompletesWithoutMarkerProof()
        {
            var sourceRoot = FileService.GetTempDirectory("content-move-finalize-predelete-retry-root");
            var oldTitle = Path.Join(sourceRoot, "Author", "Old Title");
            var source = Path.Join(oldTitle, "test");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            await ClaimOwnedDirectoriesAsync(oldTitle);
            var target = Path.Join(
                FileService.GetTempPath(),
                $"content-move-finalize-predelete-retry-dst-{Guid.NewGuid():N}");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: sourceRoot);
            var result = await service.MoveContentsAsync(request, CancellationToken.None);
            var ownershipStore = _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>();
            var resolution = await ownershipStore.ResolveOwnedAsync(
                oldTitle,
                FileSystemPathSemantics.CurrentHostDefault);
            var ownership = Assert.IsType<LibraryDirectoryOwnership>(resolution.Ownership);
            var ownershipKey = Assert.IsType<string>(ownership.PathOwnershipKey);
            await ownershipStore.BeginRemovalAsync(ownership.Id, ownershipKey);
            Assert.True(Directory.Exists(oldTitle));
            Assert.Empty(Directory.EnumerateFileSystemEntries(oldTitle));

            await service.FinalizeMoveAsync(request, result, CancellationToken.None);

            Assert.False(Directory.Exists(oldTitle));
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var persisted = await db.LibraryDirectoryOwnerships.SingleAsync(
                candidate => candidate.Id == ownership.Id);
            Assert.Equal(LibraryDirectoryOwnershipState.Removed, persisted.State);
            Assert.Null(persisted.PathOwnershipKey);
        }

        [Fact]
        public async Task FinalizeMove_MarkRemovedFailure_RetriesFromDatabaseIntent()
        {
            var sourceRoot = FileService.GetTempDirectory("content-move-finalize-mark-removed-failure-root");
            var oldTitle = Path.Join(sourceRoot, "Author", "Old Title");
            var source = Path.Join(oldTitle, "test");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            await ClaimOwnedDirectoriesAsync(oldTitle);
            var target = Path.Join(
                FileService.GetTempPath(),
                $"content-move-finalize-mark-removed-failure-dst-{Guid.NewGuid():N}");

            var normalService = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: sourceRoot);
            var result = await normalService.MoveContentsAsync(request, CancellationToken.None);
            var ownershipStore = _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>();
            var resolution = await ownershipStore.ResolveOwnedAsync(
                oldTitle,
                FileSystemPathSemantics.CurrentHostDefault);
            var ownership = Assert.IsType<LibraryDirectoryOwnership>(resolution.Ownership);
            var ownershipKey = Assert.IsType<string>(ownership.PathOwnershipKey);
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            var failingService = new AudiobookContentMoveService(
                NullLogger<AudiobookContentMoveService>.Instance,
                factory,
                TimeProvider.System,
                directoryOwnershipStore: new FailingMarkRemovedOwnershipStore(ownershipStore));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                failingService.FinalizeMoveAsync(request, result, CancellationToken.None));

            Assert.False(Directory.Exists(oldTitle));
            await using (var failedDb = await factory.CreateDbContextAsync())
            {
                var interrupted = await failedDb.LibraryDirectoryOwnerships.AsNoTracking()
                    .SingleAsync(candidate => candidate.Id == ownership.Id);
                Assert.Equal(LibraryDirectoryOwnershipState.Removing, interrupted.State);
                Assert.Equal(ownershipKey, interrupted.PathOwnershipKey);
            }

            await normalService.FinalizeMoveAsync(request, result, CancellationToken.None);

            await using var recoveredDb = await factory.CreateDbContextAsync();
            var recovered = await recoveredDb.LibraryDirectoryOwnerships.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == ownership.Id);
            Assert.Equal(LibraryDirectoryOwnershipState.Removed, recovered.State);
            Assert.Null(recovered.PathOwnershipKey);
        }

        [Fact]
        public async Task FinalizeMove_LiveRemovingPathWithNewContent_RetainsDirectory()
        {
            var sourceRoot = FileService.GetTempDirectory("content-move-finalize-predelete-arrival-root");
            var oldTitle = Path.Join(sourceRoot, "Author", "Old Title");
            var source = Path.Join(oldTitle, "test");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            await ClaimOwnedDirectoriesAsync(oldTitle);
            var target = Path.Join(
                FileService.GetTempPath(),
                $"content-move-finalize-predelete-arrival-dst-{Guid.NewGuid():N}");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: sourceRoot);
            var result = await service.MoveContentsAsync(request, CancellationToken.None);
            var ownershipStore = _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>();
            var resolution = await ownershipStore.ResolveOwnedAsync(
                oldTitle,
                FileSystemPathSemantics.CurrentHostDefault);
            var ownership = Assert.IsType<LibraryDirectoryOwnership>(resolution.Ownership);
            var ownershipKey = Assert.IsType<string>(ownership.PathOwnershipKey);
            await ownershipStore.BeginRemovalAsync(ownership.Id, ownershipKey);
            await File.WriteAllTextAsync(Path.Join(oldTitle, "arrived-late.txt"), "keep");

            await service.FinalizeMoveAsync(request, result, CancellationToken.None);

            Assert.True(Directory.Exists(oldTitle));
            Assert.True(File.Exists(Path.Join(oldTitle, "arrived-late.txt")));
            var interrupted = await ownershipStore.ResolveOwnedAsync(
                oldTitle,
                FileSystemPathSemantics.CurrentHostDefault);
            Assert.Equal(LibraryDirectoryOwnershipState.Retained, interrupted.Ownership?.State);
            Assert.Equal(ownershipKey, interrupted.Ownership?.PathOwnershipKey);
        }

        [Fact]
        public async Task FinalizeMove_MissingRemovedDirectory_ConvergesFromDatabaseIntent()
        {
            var sourceRoot = FileService.GetTempDirectory("content-move-missing-parent-proof-root");
            var oldTitle = Path.Join(sourceRoot, "Author", "Old Title");
            var source = Path.Join(oldTitle, "test");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            await ClaimOwnedDirectoriesAsync(oldTitle);
            var target = Path.Join(
                FileService.GetTempPath(),
                $"content-move-missing-parent-proof-dst-{Guid.NewGuid():N}");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: sourceRoot);
            var result = await service.MoveContentsAsync(request, CancellationToken.None);
            var ownershipStore = _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>();
            var resolution = await ownershipStore.ResolveOwnedAsync(
                oldTitle,
                FileSystemPathSemantics.CurrentHostDefault);
            var ownership = Assert.IsType<LibraryDirectoryOwnership>(resolution.Ownership);
            var ownershipKey = Assert.IsType<string>(ownership.PathOwnershipKey);
            await ownershipStore.BeginRemovalAsync(ownership.Id, ownershipKey);
            Directory.Delete(oldTitle, false);

            await service.FinalizeMoveAsync(request, result, CancellationToken.None);

            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var persisted = await db.LibraryDirectoryOwnerships.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == ownership.Id);
            Assert.Equal(LibraryDirectoryOwnershipState.Removed, persisted.State);
            Assert.Null(persisted.PathOwnershipKey);
        }

        [Fact]
        public async Task FinalizeMove_MarkerlessOwnership_PrunesDirectory()
        {
            var sourceRoot = FileService.GetTempDirectory("content-move-missing-ownership-marker");
            var oldTitle = Path.Join(sourceRoot, "Author", "Old Title");
            var source = Path.Join(oldTitle, "test");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            await ClaimOwnedDirectoriesAsync(oldTitle);
            var target = Path.Join(FileService.GetTempPath(), $"content-move-missing-owner-dst-{Guid.NewGuid():N}");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: sourceRoot);
            var result = await service.MoveContentsAsync(request, CancellationToken.None);

            await service.FinalizeMoveAsync(request, result, CancellationToken.None);

            Assert.False(Directory.Exists(oldTitle));
        }

        [Fact]
        public async Task FinalizeMove_ContentAppearsBeforeOwnedParentDelete_PreservesDirectory()
        {
            var sourceRoot = FileService.GetTempDirectory("content-move-owned-parent-race");
            var oldTitle = Path.Join(sourceRoot, "Author", "Old Title");
            var source = Path.Join(oldTitle, "test");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            await ClaimOwnedDirectoriesAsync(oldTitle);
            var target = Path.Join(FileService.GetTempPath(), $"content-move-owned-parent-race-dst-{Guid.NewGuid():N}");
            var injector = new AddFileBeforeSourceAncestorDelete(oldTitle);
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            var service = new AudiobookContentMoveService(
                NullLogger<AudiobookContentMoveService>.Instance,
                factory,
                TimeProvider.System,
                injector,
                directoryOwnershipStore: _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>());
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: sourceRoot);
            var result = await service.MoveContentsAsync(request, CancellationToken.None);

            await service.FinalizeMoveAsync(request, result, CancellationToken.None);

            Assert.True(Directory.Exists(oldTitle));
            Assert.True(File.Exists(Path.Join(oldTitle, "arrived-late.txt")));
            var resolution = await _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>()
                .ResolveOwnedAsync(oldTitle, FileSystemPathSemantics.CurrentHostDefault);
            Assert.Equal(LibraryDirectoryOwnershipState.Owned, resolution.Ownership?.State);
        }

        [Fact]
        public async Task FinalizeMove_MissingBoundaryWithNonEmptyParent_CompletesAtNaturalStop()
        {
            var sourceParent = FileService.GetTempDirectory("content-move-nonempty-stop-parent");
            await FileService.GetFileAsync(sourceParent, "keep.txt", "keep");
            var source = Path.Join(sourceParent, "test");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(FileService.GetTempPath(), $"content-move-nonempty-stop-dst-{Guid.NewGuid():N}");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = await CreateLeasedMoveRequestAsync(source, target);
            var result = await service.MoveContentsAsync(request, CancellationToken.None);

            await service.FinalizeMoveAsync(request, result, CancellationToken.None);

            await service.CleanupCompletedMoveArtifactsAsync(request, result, CancellationToken.None);
            Assert.True(Directory.Exists(sourceParent));
            Assert.True(File.Exists(Path.Join(sourceParent, "keep.txt")));
        }

        [Fact]
        public async Task MoveContentsAsync_DeleteEmptySourceFalse_KeepsEmptySourceDirectory()
        {
            var source = FileService.GetTempDirectory("content-move-keep-source");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(FileService.GetTempPath(), $"content-move-keep-source-dst-{Guid.NewGuid():N}");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            await service.MoveContentsAsync(
                await CreateLeasedMoveRequestAsync(source, target, deleteEmptySource: false),
                CancellationToken.None);

            Assert.True(Directory.Exists(source));
            Assert.Empty(Directory.EnumerateFileSystemEntries(source));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task MoveContentsAsync_SourceInsideNonEmptyParent_DoesNotDeleteParent()
        {
            var sourceParent = FileService.GetTempDirectory("content-move-nonempty-parent");
            await FileService.GetFileAsync(sourceParent, "keep.txt", "keep");
            var source = Path.Join(sourceParent, " test");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(FileService.GetTempPath(), $"content-move-nonempty-dst-{Guid.NewGuid():N}");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            await service.MoveContentsAsync(
                await CreateLeasedMoveRequestAsync(source, target),
                CancellationToken.None);

            Assert.False(Directory.Exists(source));
            Assert.True(Directory.Exists(sourceParent));
            Assert.True(File.Exists(Path.Join(sourceParent, "keep.txt")));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task MoveContentsAsync_TargetContainsUnrelatedFiles_Fails()
        {
            var source = FileService.GetTempDirectory("content-move-collision-src");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = FileService.GetTempDirectory("content-move-collision-dst");
            await FileService.GetFileAsync(target, "existing.txt", "blocked");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = await CreateLeasedMoveRequestAsync(source, target);
            var ex = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.Contains("unowned file", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(source));
            Assert.True(File.Exists(Path.Join(source, "book.m4b")));
            Assert.True(File.Exists(Path.Join(target, "existing.txt")));
        }

        [Fact]
        public async Task MoveContentsAsync_TargetContainsOnlySourceSubtree_AllowsMove()
        {
            var target = FileService.GetTempDirectory("content-move-source-subtree-target");
            var source = Path.Join(target, "nested", "source");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            await service.MoveContentsAsync(
                await CreateLeasedMoveRequestAsync(source, target),
                CancellationToken.None);

            Assert.True(Directory.Exists(target));
            Assert.False(Directory.Exists(source));
            Assert.True(Directory.Exists(Path.Join(target, "nested")));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task MoveContentsAsync_TargetInsideSource_TargetAlreadyContainsFile_Fails()
        {
            var source = FileService.GetTempDirectory("content-move-child-collision-src");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(source, " test");
            Directory.CreateDirectory(target);
            await FileService.GetFileAsync(target, "existing.txt", "blocked");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = await CreateLeasedMoveRequestAsync(source, target);
            var ex = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.Contains("overlaps", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(source));
            Assert.True(File.Exists(Path.Join(source, "book.m4b")));
            Assert.True(File.Exists(Path.Join(target, "existing.txt")));
        }

        [Fact]
        public async Task MoveContentsAsync_PersistedSubsetManifest_PreservesForeignSourceFile()
        {
            var source = FileService.GetTempDirectory("content-move-subset-src");
            var ownedFile = await FileService.GetFileAsync(
                source,
                "book.m4b",
                "owned audio");
            var foreignFile = await FileService.GetFileAsync(
                source,
                "operator-note.txt",
                "preserve me");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"content-move-subset-dst-{Guid.NewGuid():N}");
            var request = await CreateLeasedMoveRequestAsync(source, target);
            await PersistFileManifestAsync(request.JobId, "book.m4b", ownedFile);
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();

            var result = await service.MoveContentsAsync(
                request,
                CancellationToken.None);

            Assert.True(result.SourceCleanupCompleted);
            Assert.True(Directory.Exists(source));
            Assert.False(File.Exists(ownedFile));
            Assert.Equal("preserve me", await File.ReadAllTextAsync(foreignFile));
            Assert.Equal(
                "owned audio",
                await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
            Assert.False(File.Exists(Path.Join(target, "operator-note.txt")));
        }

        [Fact]
        public async Task MoveContentsAsync_TrackedFileReplacedAfterManifest_RequiresAttention()
        {
            var source = FileService.GetTempDirectory("content-move-replaced-src");
            var ownedFile = await FileService.GetFileAsync(
                source,
                "book.m4b",
                "original audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"content-move-replaced-dst-{Guid.NewGuid():N}");
            var request = await CreateLeasedMoveRequestAsync(source, target);
            await File.WriteAllTextAsync(ownedFile, "replacement audio with different bytes");
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();

            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.Contains("changed", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                "replacement audio with different bytes",
                await File.ReadAllTextAsync(ownedFile));
            Assert.False(Directory.Exists(target));
        }

        [Fact]
        public async Task MoveContentsAsync_ForeignSourceFileAppearsAfterPublish_PreservesItAndCompletesOwnedCleanup()
        {
            var source = FileService.GetTempDirectory("content-move-drift-src");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(FileService.GetTempPath(), $"content-move-drift-dst-{Guid.NewGuid():N}");
            var faultInjector = new AddSourceFileAfterPublish(source);
            var service = new AudiobookContentMoveService(
                _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                faultInjector);

            var request = await CreateLeasedMoveRequestAsync(source, target);
            var result = await service.MoveContentsAsync(
                request,
                CancellationToken.None);

            Assert.True(result.SourceCleanupCompleted);
            Assert.False(File.Exists(Path.Join(source, "book.m4b")));
            Assert.True(File.Exists(Path.Join(source, "arrived-late.txt")));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task MoveContentsAsync_TargetChangesAfterPublish_BlocksSourceCleanup()
        {
            var source = FileService.GetTempDirectory("content-move-target-drift-src");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"content-move-target-drift-dst-{Guid.NewGuid():N}");
            var service = new AudiobookContentMoveService(
                _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new AddTargetFileAfterPublish(target));

            var request = await CreateLeasedMoveRequestAsync(source, target);
            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.Contains("unowned file", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Join(source, "book.m4b")));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
            Assert.True(File.Exists(Path.Join(target, "arrived-late.txt")));
        }

        [Fact]
        public async Task MoveContentsAsync_MarkerlessProtocol_WritesOnlyFinalUserContent()
        {
            var root = FileService.GetTempDirectory("content-move-markerless-root");
            var source = Path.Join(root, "source");
            var sourceDisc = Path.Join(source, "Disc 01");
            Directory.CreateDirectory(sourceDisc);
            await FileService.GetFileAsync(sourceDisc, "book.m4b", "audio");
            await FileService.GetFileAsync(source, "cover.jpg", "image");
            var target = Path.Join(root, "destination", "Author", "Book");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();

            var result = await service.MoveContentsAsync(
                request,
                CancellationToken.None);
            await service.FinalizeMoveAsync(
                request,
                result,
                CancellationToken.None);
            await service.CleanupCompletedMoveArtifactsAsync(
                request,
                result,
                CancellationToken.None);

            Assert.False(Directory.Exists(source));
            Assert.Equal("audio", await File.ReadAllTextAsync(
                Path.Join(target, "Disc 01", "book.m4b")));
            Assert.Equal("image", await File.ReadAllTextAsync(
                Path.Join(target, "cover.jpg")));
            AssertNoListenarrArtifacts(root);

            var factory = _provider.GetRequiredService<
                IDbContextFactory<ListenArrDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var job = await db.MoveJobs
                .Include(candidate => candidate.Entries)
                .Include(candidate => candidate.CreatedDirectories)
                .SingleAsync(candidate => candidate.Id == request.JobId);
            Assert.Equal(
                MoveExecutionProtocol.MarkerlessDatabaseState,
                job.ExecutionProtocolVersion);
            Assert.Equal(
                MoveJobEntryCleanupState.Deleted,
                job.SourceDirectoryCleanupState);
            Assert.All(
                job.Entries.Where(entry =>
                    entry.EntryType == MoveJobEntryType.File),
                entry =>
                {
                    Assert.Equal(MoveJobEntryCopyState.Verified, entry.CopyState);
                    Assert.Equal(MoveJobEntryCleanupState.Deleted, entry.CleanupState);
                    Assert.False(string.IsNullOrWhiteSpace(
                        entry.SourcePhysicalObjectIdentity));
                    Assert.False(string.IsNullOrWhiteSpace(
                        entry.TargetPhysicalObjectIdentity));
                });
            Assert.All(
                job.CreatedDirectories,
                directory => Assert.Equal(
                    MoveCreatedDirectoryState.Retained,
                    directory.State));
        }

        [Fact]
        public async Task MoveContentsAsync_MarkerlessOwnedSource_RetiresDurableOwnership()
        {
            var root = FileService.GetTempDirectory(
                "content-move-markerless-owned-source-root");
            var source = Path.Join(root, "Owned Book");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(root, "destination", "Book");
            var ownershipStore = _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>();
            var ownership = await ownershipStore.RecordCreatedAsync(
                new LibraryDirectoryOwnershipClaim(
                    source,
                    FileSystemPathSemantics.CurrentHostDefault,
                    "rename",
                    AudiobookId: 98));
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();

            var result = await service.MoveContentsAsync(
                request,
                CancellationToken.None);

            Assert.True(result.SourceCleanupCompleted);
            Assert.False(Directory.Exists(source));
            Assert.Equal("audio", await File.ReadAllTextAsync(
                Path.Join(target, "book.m4b")));
            var resolution = await ownershipStore.ResolveOwnedAsync(
                source,
                FileSystemPathSemantics.CurrentHostDefault);
            Assert.Equal(
                LibraryDirectoryOwnershipResolutionState.Unowned,
                resolution.State);
            var factory = _provider.GetRequiredService<
                IDbContextFactory<ListenArrDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var persisted = await db.LibraryDirectoryOwnerships
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == ownership.Id);
            Assert.Equal(LibraryDirectoryOwnershipState.Removed, persisted.State);
            Assert.Null(persisted.PathOwnershipKey);
            Assert.Null(persisted.ManagedRootFolderId);
            AssertNoListenarrArtifacts(root);
        }

        [Fact]
        public async Task MoveContentsAsync_MarkerlessOwnedSource_MarkRemovedFailureResumesFromDurableIntents()
        {
            var root = FileService.GetTempDirectory(
                "content-move-markerless-owned-source-retry-root");
            var source = Path.Join(root, "Owned Book");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(root, "destination", "Book");
            var ownershipStore = _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>();
            var ownership = await ownershipStore.RecordCreatedAsync(
                new LibraryDirectoryOwnershipClaim(
                    source,
                    FileSystemPathSemantics.CurrentHostDefault,
                    "rename",
                    AudiobookId: 98));
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            var factory = _provider.GetRequiredService<
                IDbContextFactory<ListenArrDbContext>>();
            var interruptedService = new AudiobookContentMoveService(
                NullLogger<AudiobookContentMoveService>.Instance,
                factory,
                TimeProvider.System,
                directoryOwnershipStore:
                    new FailingMarkRemovedOwnershipStore(ownershipStore));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                interruptedService.MoveContentsAsync(
                    request,
                    CancellationToken.None));

            Assert.False(Directory.Exists(source));
            Assert.Equal("audio", await File.ReadAllTextAsync(
                Path.Join(target, "book.m4b")));
            await using (var interruptedDb = await factory.CreateDbContextAsync())
            {
                var interruptedOwnership = await interruptedDb
                    .LibraryDirectoryOwnerships
                    .AsNoTracking()
                    .SingleAsync(candidate => candidate.Id == ownership.Id);
                var interruptedJob = await interruptedDb.MoveJobs
                    .AsNoTracking()
                    .SingleAsync(candidate => candidate.Id == request.JobId);
                Assert.Equal(
                    LibraryDirectoryOwnershipState.Removing,
                    interruptedOwnership.State);
                Assert.Equal(
                    MoveJobEntryCleanupState.DeleteAuthorized,
                    interruptedJob.SourceDirectoryCleanupState);
            }

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var result = await service.MoveContentsAsync(
                request,
                CancellationToken.None);
            await service.FinalizeMoveAsync(
                request,
                result,
                CancellationToken.None);
            await service.CleanupCompletedMoveArtifactsAsync(
                request,
                result,
                CancellationToken.None);

            await using var verification = await factory.CreateDbContextAsync();
            var retired = await verification.LibraryDirectoryOwnerships
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == ownership.Id);
            var completedJob = await verification.MoveJobs
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == request.JobId);
            Assert.Equal(LibraryDirectoryOwnershipState.Removed, retired.State);
            Assert.Null(retired.PathOwnershipKey);
            Assert.Null(retired.ManagedRootFolderId);
            Assert.Equal(
                MoveJobEntryCleanupState.Deleted,
                completedJob.SourceDirectoryCleanupState);
            AssertNoListenarrArtifacts(root);
        }

        [Fact]
        public async Task CleanupTerminalTargetScaffoldingAsync_MarkerlessTargetWithContent_DoesNotRequireLegacyMarker()
        {
            var root = FileService.GetTempDirectory(
                "content-move-markerless-terminal-cleanup-root");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(root, "destination", "Book");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();

            _ = await service.MoveContentsAsync(
                request,
                CancellationToken.None);
            await service.CleanupTerminalTargetScaffoldingAsync(
                request,
                CancellationToken.None);

            Assert.Equal("audio", await File.ReadAllTextAsync(
                Path.Join(target, "book.m4b")));
            AssertNoListenarrArtifacts(root);
            var factory = _provider.GetRequiredService<
                IDbContextFactory<ListenArrDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var directories = await db.MoveJobCreatedDirectories
                .AsNoTracking()
                .Where(directory => directory.MoveJobId == request.JobId)
                .ToListAsync();
            Assert.NotEmpty(directories);
            Assert.All(
                directories,
                directory => Assert.Equal(
                    MoveCreatedDirectoryState.Retained,
                    directory.State));
        }

        [Fact]
        public async Task MoveContentsAsync_MarkerlessPlanWithoutHashes_PersistsSourceProofBeforePublication()
        {
            var root = FileService.GetTempDirectory(
                "content-move-markerless-worker-proof-root");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(root, "destination", "Book");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            var factory = _provider.GetRequiredService<
                IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                var fileEntry = await db.MoveJobEntries.SingleAsync(entry =>
                    entry.MoveJobId == request.JobId
                    && entry.EntryType == MoveJobEntryType.File);
                fileEntry.Sha256 = null;
                await db.SaveChangesAsync();
            }

            var progress = new List<(double Value, string Phase)>();
            request = request with
            {
                ProgressReporter = (value, phase, _) =>
                {
                    progress.Add((value, phase));
                    return Task.CompletedTask;
                }
            };
            var service = new AudiobookContentMoveService(
                _provider.GetRequiredService<
                    ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<
                    IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new DisableMarkerlessFileRename());

            var result = await service.MoveContentsAsync(
                request,
                CancellationToken.None);

            Assert.True(result.SourceCleanupCompleted);
            Assert.Equal("audio", await File.ReadAllTextAsync(
                Path.Join(target, "book.m4b")));
            Assert.Contains(progress, update =>
                string.Equals(
                    update.Phase,
                    "Verifying source",
                    StringComparison.Ordinal));
            Assert.Contains(progress, update => update.Value >= 25);

            await using var verificationDb = await factory.CreateDbContextAsync();
            var persisted = await verificationDb.MoveJobEntries
                .AsNoTracking()
                .SingleAsync(entry =>
                    entry.MoveJobId == request.JobId
                    && entry.EntryType == MoveJobEntryType.File);
            Assert.NotNull(persisted.Sha256);
            Assert.Equal(64, persisted.Sha256!.Length);
            Assert.False(string.IsNullOrWhiteSpace(
                persisted.SourcePhysicalObjectIdentity));
            Assert.Equal(MoveJobEntryCopyState.Verified, persisted.CopyState);
        }

        [LinuxFact]
        [System.Runtime.Versioning.SupportedOSPlatform("linux")]
        public async Task MoveContentsAsync_MarkerlessCopyHashBackfill_PreservesEquivalentPersistedSourceIdentity()
        {
            var root = FileService.GetTempDirectory(
                "content-move-markerless-compatible-source-hash-root");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(root, "destination", "Book");
            var targetFile = Path.Join(target, "book.m4b");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            string durableSourceIdentity;
            using (var lease = PinnedAudiobookFileRegistrationLease.Open(sourceFile))
            {
                Assert.StartsWith(
                    "linux-generation:",
                    lease.PhysicalObjectIdentity,
                    StringComparison.Ordinal);
                durableSourceIdentity =
                    LinuxIdentityTestHelper.ToMergedV1AugmentedIdentity(
                        lease.PhysicalObjectIdentity);
                Assert.NotEqual(durableSourceIdentity, lease.PhysicalObjectIdentity);
                Assert.True(
                    PinnedDirectoryCreation.ArePersistedObjectIdentitiesDurablyEquivalent(
                        durableSourceIdentity,
                        lease.PhysicalObjectIdentity));
            }

            var factory = _provider.GetRequiredService<
                IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                var entry = await db.MoveJobEntries.SingleAsync(candidate =>
                    candidate.MoveJobId == request.JobId
                    && candidate.EntryType == MoveJobEntryType.File);
                entry.SourcePhysicalObjectIdentity = durableSourceIdentity;
                entry.Sha256 = null;
                await db.SaveChangesAsync();
            }

            var service = new AudiobookContentMoveService(
                _provider.GetRequiredService<
                    ILogger<AudiobookContentMoveService>>(),
                factory,
                TimeProvider.System,
                new DisableMarkerlessFileRename());

            var result = await service.MoveContentsAsync(
                request,
                CancellationToken.None);

            Assert.True(result.SourceCleanupCompleted);
            Assert.False(Directory.Exists(source));
            Assert.Equal("audio", await File.ReadAllTextAsync(targetFile));
            await using var verification = await factory.CreateDbContextAsync();
            var persisted = await verification.MoveJobEntries
                .AsNoTracking()
                .SingleAsync(candidate =>
                    candidate.MoveJobId == request.JobId
                    && candidate.EntryType == MoveJobEntryType.File);
            Assert.Equal(durableSourceIdentity, persisted.SourcePhysicalObjectIdentity);
            Assert.NotNull(persisted.Sha256);
            Assert.Equal(64, persisted.Sha256!.Length);
            Assert.Equal(MoveJobEntryCopyState.Verified, persisted.CopyState);
        }

        [Fact]
        public async Task MoveContentsAsync_MarkerlessRetryAfterPublication_UsesDatabaseStateOnly()
        {
            var root = FileService.GetTempDirectory(
                "content-move-markerless-retry-root");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(root, "destination", "Book");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            var interruptedService = new AudiobookContentMoveService(
                _provider.GetRequiredService<
                    ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<
                    IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new FailAfterPublishedOnce());

            await Assert.ThrowsAsync<IOException>(() =>
                interruptedService.MoveContentsAsync(
                    request,
                    CancellationToken.None));

            Assert.True(File.Exists(Path.Join(source, "book.m4b")));
            Assert.Equal("audio", await File.ReadAllTextAsync(
                Path.Join(target, "book.m4b")));
            AssertNoListenarrArtifacts(root);

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var result = await service.MoveContentsAsync(
                request,
                CancellationToken.None);
            await service.FinalizeMoveAsync(
                request,
                result,
                CancellationToken.None);
            await service.CleanupCompletedMoveArtifactsAsync(
                request,
                result,
                CancellationToken.None);

            Assert.False(Directory.Exists(source));
            Assert.Equal("audio", await File.ReadAllTextAsync(
                Path.Join(target, "book.m4b")));
            AssertNoListenarrArtifacts(root);
        }

        [LinuxFact]
        [System.Runtime.Versioning.SupportedOSPlatform("linux")]
        public async Task GetRecoverableMoveAsync_InaccessibleVerifiedTarget_IsTransientNotNeedsAttention()
        {
            var root = FileService.GetTempDirectory(
                "content-move-markerless-inaccessible-target-root");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(root, "destination", "Book");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            var interruptedService = new AudiobookContentMoveService(
                _provider.GetRequiredService<
                    ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<
                    IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new FailAfterPublishedOnce());

            await Assert.ThrowsAsync<IOException>(() =>
                interruptedService.MoveContentsAsync(
                    request,
                    CancellationToken.None));

            var targetParent = Path.GetDirectoryName(target)!;
            var originalMode = File.GetUnixFileMode(targetParent);
            File.SetUnixFileMode(targetParent, UnixFileMode.None);
            try
            {
                // Root can bypass Unix permission checks. The unprivileged Linux
                // validation environment exercises the access-denied branch.
                if (!Directory.Exists(target))
                {
                    var service = _provider.GetRequiredService<
                        AudiobookContentMoveService>();
                    var exception = await Record.ExceptionAsync(() =>
                        service.GetRecoverableMoveAsync(
                            request,
                            CancellationToken.None));
                    Assert.NotNull(exception);
                    Assert.IsNotType<MoveNeedsAttentionException>(exception);
                    Assert.True(
                        exception is UnauthorizedAccessException
                            or IOException
                            or System.ComponentModel.Win32Exception,
                        exception.ToString());
                }
            }
            finally
            {
                File.SetUnixFileMode(targetParent, originalMode);
            }

            var normalService = _provider.GetRequiredService<
                AudiobookContentMoveService>();
            var result = await normalService.MoveContentsAsync(
                request,
                CancellationToken.None);
            Assert.True(result.SourceCleanupCompleted);
            Assert.Equal(
                "audio",
                await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
        }

        [LinuxFact]
        [System.Runtime.Versioning.SupportedOSPlatform("linux")]
        public async Task MoveContentsAsync_InaccessiblePersistedSourceManifest_IsTransientBeforeMutation()
        {
            var root = FileService.GetTempDirectory(
                "content-move-inaccessible-persisted-source-root");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            var sourceFile = await FileService.GetFileAsync(
                source,
                "book.m4b",
                "audio");
            var target = Path.Join(root, "destination", "Book");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);

            var originalMode = File.GetUnixFileMode(source);
            File.SetUnixFileMode(source, UnixFileMode.None);
            try
            {
                // The source directory itself is still visible from its parent, but
                // an unprivileged process cannot inspect the persisted file beneath it.
                if (!File.Exists(sourceFile))
                {
                    var service = _provider.GetRequiredService<
                        AudiobookContentMoveService>();
                    var exception = await Record.ExceptionAsync(() =>
                        service.MoveContentsAsync(
                            request,
                            CancellationToken.None));
                    Assert.NotNull(exception);
                    Assert.IsNotType<MoveNeedsAttentionException>(exception);
                    Assert.True(
                        exception is UnauthorizedAccessException
                            or IOException
                            or System.ComponentModel.Win32Exception,
                        exception.ToString());
                    Assert.False(Directory.Exists(target));
                }
            }
            finally
            {
                File.SetUnixFileMode(source, originalMode);
            }

            Assert.True(File.Exists(sourceFile));
            var recovered = await _provider.GetRequiredService<
                AudiobookContentMoveService>()
                .MoveContentsAsync(request, CancellationToken.None);
            Assert.True(recovered.SourceCleanupCompleted);
            Assert.Equal(
                "audio",
                await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
        }

        [WindowsFact]
        public async Task MoveContentsAsync_MarkerlessNativeRename_SourceContentChangedBeforeExecution_DoesNotRename()
        {
            var root = FileService.GetTempDirectory(
                "content-move-markerless-native-rename-stale-hash-root");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            var sourceFile = await FileService.GetFileAsync(
                source,
                "book.m4b",
                "audio");
            var originalLastWriteTimeUtc = File.GetLastWriteTimeUtc(sourceFile);
            var target = Path.Join(root, "destination", "Book");
            var targetFile = Path.Join(target, "book.m4b");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);

            await File.WriteAllTextAsync(sourceFile, "other");
            File.SetLastWriteTimeUtc(sourceFile, originalLastWriteTimeUtc);

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.True(File.Exists(sourceFile));
            Assert.Equal("other", await File.ReadAllTextAsync(sourceFile));
            Assert.False(File.Exists(targetFile));
            AssertNoListenarrArtifacts(root);
        }

        [WindowsFact]
        public async Task MoveContentsAsync_MarkerlessNativeRename_HoldsStableContentProofThroughFinalVerification()
        {
            var root = FileService.GetTempDirectory(
                "content-move-markerless-stable-native-rename-root");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(root, "destination", "Book");
            var targetFile = Path.Join(target, "book.m4b");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            var factory = _provider.GetRequiredService<
                IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                var entry = await db.MoveJobEntries.SingleAsync(candidate =>
                    candidate.MoveJobId == request.JobId
                    && candidate.EntryType == MoveJobEntryType.File);
                entry.Sha256 = null;
                await db.SaveChangesAsync();
            }
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();

            var result = await service.MoveContentsAsync(
                request,
                CancellationToken.None);

            Assert.NotNull(result.TargetVerificationLease);
            await using (var db = await factory.CreateDbContextAsync())
            {
                var entry = await db.MoveJobEntries
                    .AsNoTracking()
                    .SingleAsync(candidate =>
                        candidate.MoveJobId == request.JobId
                        && candidate.EntryType == MoveJobEntryType.File);
                Assert.False(string.IsNullOrWhiteSpace(entry.Sha256));
                Assert.Equal(64, entry.Sha256!.Length);
                Assert.Equal(
                    entry.SourcePhysicalObjectIdentity,
                    entry.TargetPhysicalObjectIdentity);
            }
            Assert.ThrowsAny<Exception>(() =>
            {
                using var writer = new FileStream(
                    targetFile,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
            });

            await service.FinalizeMoveAsync(
                request,
                result,
                CancellationToken.None);
            await service.CleanupCompletedMoveArtifactsAsync(
                request,
                result,
                CancellationToken.None);

            Assert.ThrowsAny<Exception>(() =>
            {
                using var writer = new FileStream(
                    targetFile,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
            });
            result.TargetVerificationLease!.Dispose();
            using (var writer = new FileStream(
                targetFile,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete))
            {
                Assert.True(writer.CanWrite);
            }
            Assert.Equal("audio", await File.ReadAllTextAsync(targetFile));
            AssertNoListenarrArtifacts(root);
        }

        [LinuxFact]
        public async Task MoveContentsAsync_MarkerlessNativeRenameUnsupported_FallsBackToVerifiedCopy()
        {
            var root = FileService.GetTempDirectory(
                "content-move-markerless-native-rename-unsupported-root");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            var sourceFile = await FileService.GetFileAsync(
                source,
                "book.m4b",
                "audio");
            var target = Path.Join(root, "destination", "Book");
            var targetFile = Path.Join(target, "book.m4b");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            string sourceIdentity;
            using (var lease = PinnedAudiobookFileRegistrationLease.Open(sourceFile))
            {
                sourceIdentity = lease.PhysicalObjectIdentity;
            }
            var service = new AudiobookContentMoveService(
                _provider.GetRequiredService<
                    ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<
                    IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new NativeRenameUnsupported(22));

            var result = await service.MoveContentsAsync(
                request,
                CancellationToken.None);
            await service.FinalizeMoveAsync(
                request,
                result,
                CancellationToken.None);
            await service.CleanupCompletedMoveArtifactsAsync(
                request,
                result,
                CancellationToken.None);

            Assert.False(Directory.Exists(source));
            Assert.Equal("audio", await File.ReadAllTextAsync(targetFile));
            using (var lease = PinnedAudiobookFileRegistrationLease.Open(targetFile))
            {
                Assert.False(lease.MatchesPhysicalObjectIdentity(sourceIdentity));
            }
            var factory = _provider.GetRequiredService<
                IDbContextFactory<ListenArrDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var entry = await db.MoveJobEntries
                .AsNoTracking()
                .SingleAsync(candidate =>
                    candidate.MoveJobId == request.JobId
                    && candidate.EntryType == MoveJobEntryType.File);
            Assert.Equal(MoveJobEntryCopyState.Verified, entry.CopyState);
            Assert.Equal(MoveJobEntryCleanupState.Deleted, entry.CleanupState);
            Assert.NotEqual(
                entry.SourcePhysicalObjectIdentity,
                entry.TargetPhysicalObjectIdentity);
            AssertNoListenarrArtifacts(root);
        }

        [LinuxFact]
        public async Task MoveContentsAsync_MarkerlessMultiFile_CanMixNativeRenameAndVerifiedCopyFallback()
        {
            var root = FileService.GetTempDirectory(
                "content-move-markerless-mixed-native-copy-root");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            var firstSource = await FileService.GetFileAsync(
                source,
                "a.m4b",
                "first audio");
            var secondSource = await FileService.GetFileAsync(
                source,
                "b.m4b",
                "second audio");
            var target = Path.Join(root, "destination", "Book");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            var sourceIdentities = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var firstLease = PinnedAudiobookFileRegistrationLease.Open(firstSource))
            using (var secondLease = PinnedAudiobookFileRegistrationLease.Open(secondSource))
            {
                sourceIdentities["a.m4b"] = firstLease.PhysicalObjectIdentity;
                sourceIdentities["b.m4b"] = secondLease.PhysicalObjectIdentity;
            }
            var service = new AudiobookContentMoveService(
                _provider.GetRequiredService<
                    ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<
                    IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new NativeRenameFirstThenUnsupported());

            var result = await service.MoveContentsAsync(
                request,
                CancellationToken.None);

            Assert.True(result.SourceCleanupCompleted);
            Assert.False(Directory.Exists(source));
            Assert.Equal(
                "first audio",
                await File.ReadAllTextAsync(Path.Join(target, "a.m4b")));
            Assert.Equal(
                "second audio",
                await File.ReadAllTextAsync(Path.Join(target, "b.m4b")));
            var factory = _provider.GetRequiredService<
                IDbContextFactory<ListenArrDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var entries = await db.MoveJobEntries
                .AsNoTracking()
                .Where(candidate =>
                    candidate.MoveJobId == request.JobId
                    && candidate.EntryType == MoveJobEntryType.File)
                .OrderBy(candidate => candidate.RelativePath)
                .ToListAsync();
            Assert.Equal(2, entries.Count);
            var preservedGenerationCount = entries.Count(entry =>
                sourceIdentities.TryGetValue(entry.RelativePath, out var sourceIdentity)
                && string.Equals(
                    sourceIdentity,
                    entry.TargetPhysicalObjectIdentity,
                    StringComparison.Ordinal));
            Assert.Equal(1, preservedGenerationCount);
            Assert.Equal(1, entries.Count - preservedGenerationCount);
            Assert.All(
                entries,
                entry => Assert.Equal(
                    MoveJobEntryCopyState.Verified,
                    entry.CopyState));
            AssertNoListenarrArtifacts(root);
        }

        [LinuxFact]
        public async Task MoveContentsAsync_MarkerlessNativeRenameErrorAfterPublication_RecoversPublishedGenerationWithoutCopy()
        {
            var root = FileService.GetTempDirectory(
                "content-move-markerless-native-rename-published-error-root");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            var sourceFile = await FileService.GetFileAsync(
                source,
                "book.m4b",
                "audio");
            var target = Path.Join(root, "destination", "Book");
            var targetFile = Path.Join(target, "book.m4b");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            string sourceIdentity;
            using (var lease = PinnedAudiobookFileRegistrationLease.Open(sourceFile))
            {
                sourceIdentity = lease.PhysicalObjectIdentity;
            }
            var service = new AudiobookContentMoveService(
                _provider.GetRequiredService<
                    ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<
                    IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new NativeRenamePublishedBeforeError(22));

            var result = await service.MoveContentsAsync(
                request,
                CancellationToken.None);
            await service.FinalizeMoveAsync(
                request,
                result,
                CancellationToken.None);
            await service.CleanupCompletedMoveArtifactsAsync(
                request,
                result,
                CancellationToken.None);

            Assert.False(Directory.Exists(source));
            Assert.Equal("audio", await File.ReadAllTextAsync(targetFile));
            using (var lease = PinnedAudiobookFileRegistrationLease.Open(targetFile))
            {
                Assert.True(lease.MatchesPhysicalObjectIdentity(sourceIdentity));
            }
            var factory = _provider.GetRequiredService<
                IDbContextFactory<ListenArrDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var entry = await db.MoveJobEntries
                .AsNoTracking()
                .SingleAsync(candidate =>
                    candidate.MoveJobId == request.JobId
                    && candidate.EntryType == MoveJobEntryType.File);
            Assert.Equal(
                entry.SourcePhysicalObjectIdentity,
                entry.TargetPhysicalObjectIdentity);
            AssertNoListenarrArtifacts(root);
        }

        [LinuxFact]
        public async Task MoveContentsAsync_MarkerlessNativeRenameUnsupported_TargetAppearsBeforeObservation_FailsClosedWithoutOverwrite()
        {
            var root = FileService.GetTempDirectory(
                "content-move-markerless-native-rename-target-race-root");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            var sourceFile = await FileService.GetFileAsync(
                source,
                "book.m4b",
                "audio");
            var target = Path.Join(root, "destination", "Book");
            var targetFile = Path.Join(target, "book.m4b");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            var service = new AudiobookContentMoveService(
                _provider.GetRequiredService<
                    ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<
                    IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new CreateTargetAfterNativeRenameFailure(targetFile));

            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(
                    request,
                    CancellationToken.None));

            Assert.Contains(
                "ambiguous",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(sourceFile));
            Assert.Equal("audio", await File.ReadAllTextAsync(sourceFile));
            Assert.Equal("foreign", await File.ReadAllTextAsync(targetFile));
            AssertNoListenarrArtifacts(root);
        }

        [LinuxFact]
        public async Task MoveContentsAsync_MarkerlessNativeRenameUnsupported_SourceReplacedAfterFallbackAuthorization_DoesNotPublishTarget()
        {
            var root = FileService.GetTempDirectory(
                "content-move-markerless-native-rename-source-race-root");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            var sourceFile = await FileService.GetFileAsync(
                source,
                "book.m4b",
                "audio");
            var target = Path.Join(root, "destination", "Book");
            var targetFile = Path.Join(target, "book.m4b");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            var service = new AudiobookContentMoveService(
                _provider.GetRequiredService<
                    ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<
                    IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new ReplaceSourceAfterNativeRenameFallbackAuthorized(sourceFile));

            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(
                    request,
                    CancellationToken.None));

            Assert.Contains(
                "changed physical generation",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal("replacement", await File.ReadAllTextAsync(sourceFile));
            Assert.False(File.Exists(targetFile));
            AssertNoListenarrArtifacts(root);
        }

        [LinuxFact]
        public async Task MoveContentsAsync_MarkerlessNativeRenameFailureThatIsNotUnsupported_DoesNotCopy()
        {
            var root = FileService.GetTempDirectory(
                "content-move-markerless-native-rename-denied-root");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            var sourceFile = await FileService.GetFileAsync(
                source,
                "book.m4b",
                "audio");
            var target = Path.Join(root, "destination", "Book");
            var targetFile = Path.Join(target, "book.m4b");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            var service = new AudiobookContentMoveService(
                _provider.GetRequiredService<
                    ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<
                    IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new NativeRenameUnsupported(13));

            var exception = await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() =>
                service.MoveContentsAsync(
                    request,
                    CancellationToken.None));

            Assert.Equal(13, exception.NativeErrorCode);
            Assert.True(File.Exists(sourceFile));
            Assert.Equal("audio", await File.ReadAllTextAsync(sourceFile));
            Assert.False(File.Exists(targetFile));
            AssertNoListenarrArtifacts(root);
        }

        [LinuxFact]
        public async Task MoveContentsAsync_MarkerlessNativeRename_CompatibleExpectedSourceToken_PersistsSameDurableTargetToken()
        {
            var root = FileService.GetTempDirectory(
                "content-move-compatible-source-token-root");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            var sourceFile = await FileService.GetFileAsync(
                source,
                "book.m4b",
                "audio");
            var target = Path.Join(root, "destination", "Book");
            var targetFile = Path.Join(target, "book.m4b");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            string durableSourceIdentity;
            using (var lease = PinnedAudiobookFileRegistrationLease.Open(sourceFile))
            {
                Assert.StartsWith(
                    "linux-generation:",
                    lease.PhysicalObjectIdentity,
                    StringComparison.Ordinal);
                durableSourceIdentity =
                    LinuxIdentityTestHelper.ToMergedV1AugmentedIdentity(
                        lease.PhysicalObjectIdentity);
            }
            var factory = _provider.GetRequiredService<
                IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                var entry = await db.MoveJobEntries.SingleAsync(candidate =>
                    candidate.MoveJobId == request.JobId
                    && candidate.EntryType == MoveJobEntryType.File);
                entry.SourcePhysicalObjectIdentity = durableSourceIdentity;
                entry.Sha256 = null;
                await db.SaveChangesAsync();
            }

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var result = await service.MoveContentsAsync(
                request,
                CancellationToken.None);
            await service.FinalizeMoveAsync(
                request,
                result,
                CancellationToken.None);
            await service.CleanupCompletedMoveArtifactsAsync(
                request,
                result,
                CancellationToken.None);

            Assert.False(Directory.Exists(source));
            Assert.Equal("audio", await File.ReadAllTextAsync(targetFile));
            await using (var db = await factory.CreateDbContextAsync())
            {
                var entry = await db.MoveJobEntries
                    .AsNoTracking()
                    .SingleAsync(candidate =>
                        candidate.MoveJobId == request.JobId
                        && candidate.EntryType == MoveJobEntryType.File);
                Assert.Equal(MoveJobEntryCopyState.Verified, entry.CopyState);
                Assert.Equal(durableSourceIdentity, entry.SourcePhysicalObjectIdentity);
                Assert.Equal(durableSourceIdentity, entry.TargetPhysicalObjectIdentity);
            }
            AssertNoListenarrArtifacts(root);
        }

        [LinuxFact]
        public async Task MoveContentsAsync_MarkerlessNativeRename_ContentChangesAfterHash_PreservesMovedGenerationForRecovery()
        {
            var root = FileService.GetTempDirectory(
                "content-move-markerless-native-rename-post-hash-mutation-root");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            var sourceFile = await FileService.GetFileAsync(
                source,
                "book.m4b",
                "audio");
            var originalLastWriteTimeUtc = File.GetLastWriteTimeUtc(sourceFile);
            var target = Path.Join(root, "destination", "Book");
            var targetFile = Path.Join(target, "book.m4b");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            var service = new AudiobookContentMoveService(
                _provider.GetRequiredService<
                    ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<
                    IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new MutateBeforeMarkerlessNativeRename(
                    sourceFile,
                    originalLastWriteTimeUtc));

            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(
                    request,
                    CancellationToken.None));

            Assert.False(File.Exists(sourceFile));
            Assert.Equal("other", await File.ReadAllTextAsync(targetFile));
            Assert.Contains(
                "content verification",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
            var factory = _provider.GetRequiredService<
                IDbContextFactory<ListenArrDbContext>>();
            await using var verification = await factory.CreateDbContextAsync();
            var entry = await verification.MoveJobEntries
                .AsNoTracking()
                .SingleAsync(candidate =>
                    candidate.MoveJobId == request.JobId
                    && candidate.EntryType == MoveJobEntryType.File);
            Assert.Equal(MoveJobEntryCopyState.Verified, entry.CopyState);
            Assert.Equal(MoveJobEntryCleanupState.Pending, entry.CleanupState);
            AssertNoListenarrArtifacts(root);
        }

        [LinuxFact]
        public async Task MoveContentsAsync_MarkerlessNativeRename_PersistedMergedV1TokenPair_ResumesWithMissingSource()
        {
            var root = FileService.GetTempDirectory(
                "content-move-compatible-persisted-pair-root");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            var sourceFile = await FileService.GetFileAsync(
                source,
                "book.m4b",
                "audio");
            var target = Path.Join(root, "destination", "Book");
            var targetFile = Path.Join(target, "book.m4b");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            string durableSourceIdentity;
            using (var lease = PinnedAudiobookFileRegistrationLease.Open(sourceFile))
            {
                Assert.StartsWith(
                    "linux-generation:",
                    lease.PhysicalObjectIdentity,
                    StringComparison.Ordinal);
                durableSourceIdentity =
                    LinuxIdentityTestHelper.ToMergedV1AugmentedIdentity(
                        lease.PhysicalObjectIdentity);
            }
            var factory = _provider.GetRequiredService<
                IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                var entry = await db.MoveJobEntries.SingleAsync(candidate =>
                    candidate.MoveJobId == request.JobId
                    && candidate.EntryType == MoveJobEntryType.File);
                entry.SourcePhysicalObjectIdentity = durableSourceIdentity;
                entry.Sha256 = null;
                await db.SaveChangesAsync();
            }
            var interruptedService = new AudiobookContentMoveService(
                _provider.GetRequiredService<
                    ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<
                    IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new FailOnceAfterMarkerlessNativeRename());

            await Assert.ThrowsAsync<IOException>(() =>
                interruptedService.MoveContentsAsync(
                    request,
                    CancellationToken.None));
            Assert.False(File.Exists(sourceFile));
            Assert.Equal("audio", await File.ReadAllTextAsync(targetFile));
            string preferredTargetIdentity;
            using (var lease = PinnedAudiobookFileRegistrationLease.Open(targetFile))
            {
                preferredTargetIdentity = lease.PhysicalObjectIdentity;
            }
            Assert.NotEqual(durableSourceIdentity, preferredTargetIdentity);
            await using (var db = await factory.CreateDbContextAsync())
            {
                var entry = await db.MoveJobEntries.SingleAsync(candidate =>
                    candidate.MoveJobId == request.JobId
                    && candidate.EntryType == MoveJobEntryType.File);
                Assert.Equal(MoveJobEntryCopyState.Pending, entry.CopyState);
                entry.CopyState = MoveJobEntryCopyState.Verified;
                entry.TargetPhysicalObjectIdentity = preferredTargetIdentity;
                await db.SaveChangesAsync();
            }

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var result = await service.MoveContentsAsync(
                request,
                CancellationToken.None);
            await service.FinalizeMoveAsync(
                request,
                result,
                CancellationToken.None);
            await service.CleanupCompletedMoveArtifactsAsync(
                request,
                result,
                CancellationToken.None);

            Assert.False(Directory.Exists(source));
            Assert.Equal("audio", await File.ReadAllTextAsync(targetFile));
            await using (var db = await factory.CreateDbContextAsync())
            {
                var entry = await db.MoveJobEntries
                    .AsNoTracking()
                    .SingleAsync(candidate =>
                        candidate.MoveJobId == request.JobId
                        && candidate.EntryType == MoveJobEntryType.File);
                Assert.Equal(MoveJobEntryCopyState.Verified, entry.CopyState);
                Assert.Equal(durableSourceIdentity, entry.SourcePhysicalObjectIdentity);
                Assert.Equal(preferredTargetIdentity, entry.TargetPhysicalObjectIdentity);
                Assert.True(
                    PinnedDirectoryCreation.ArePersistedObjectIdentitiesDurablyEquivalent(
                        entry.SourcePhysicalObjectIdentity!,
                        entry.TargetPhysicalObjectIdentity!));
            }
            AssertNoListenarrArtifacts(root);
        }

        [Fact]
        public async Task MoveContentsAsync_MarkerlessNativeRenameCrashBeforeStateCommit_ResumesByPhysicalGeneration()
        {
            var root = FileService.GetTempDirectory(
                "content-move-markerless-native-rename-retry-root");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var sourceFile = Path.Join(source, "book.m4b");
            var target = Path.Join(root, "destination", "Book");
            var targetFile = Path.Join(target, "book.m4b");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            var factory = _provider.GetRequiredService<
                IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                var entry = await db.MoveJobEntries.SingleAsync(candidate =>
                    candidate.MoveJobId == request.JobId
                    && candidate.EntryType == MoveJobEntryType.File);
                entry.Sha256 = null;
                await db.SaveChangesAsync();
            }
            var interruptedService = new AudiobookContentMoveService(
                _provider.GetRequiredService<
                    ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<
                    IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new FailOnceAfterMarkerlessNativeRename());

            await Assert.ThrowsAsync<IOException>(() =>
                interruptedService.MoveContentsAsync(
                    request,
                    CancellationToken.None));

            Assert.False(File.Exists(sourceFile));
            Assert.Equal("audio", await File.ReadAllTextAsync(targetFile));
            AssertNoListenarrArtifacts(root);

            await using (var db = await factory.CreateDbContextAsync())
            {
                var entry = await db.MoveJobEntries
                    .AsNoTracking()
                    .SingleAsync(candidate =>
                        candidate.MoveJobId == request.JobId
                        && candidate.EntryType == MoveJobEntryType.File);
                Assert.Equal(MoveJobEntryCopyState.Pending, entry.CopyState);
                Assert.Null(entry.TargetPhysicalObjectIdentity);
                Assert.False(string.IsNullOrWhiteSpace(
                    entry.SourcePhysicalObjectIdentity));
                Assert.False(string.IsNullOrWhiteSpace(entry.Sha256));
                Assert.Equal(64, entry.Sha256!.Length);
            }

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var result = await service.MoveContentsAsync(
                request,
                CancellationToken.None);
            await service.FinalizeMoveAsync(
                request,
                result,
                CancellationToken.None);
            await service.CleanupCompletedMoveArtifactsAsync(
                request,
                result,
                CancellationToken.None);

            Assert.False(Directory.Exists(source));
            Assert.Equal("audio", await File.ReadAllTextAsync(targetFile));
            AssertNoListenarrArtifacts(root);

            await using (var db = await factory.CreateDbContextAsync())
            {
                var entry = await db.MoveJobEntries
                    .AsNoTracking()
                    .SingleAsync(candidate =>
                        candidate.MoveJobId == request.JobId
                        && candidate.EntryType == MoveJobEntryType.File);
                Assert.Equal(MoveJobEntryCopyState.Verified, entry.CopyState);
                Assert.Equal(MoveJobEntryCleanupState.Deleted, entry.CleanupState);
                Assert.Equal(
                    entry.SourcePhysicalObjectIdentity,
                    entry.TargetPhysicalObjectIdentity);
            }
        }

        [Fact]
        public async Task MoveContentsAsync_MarkerlessNativeRenameCrashThenTargetChanges_RequiresAttention()
        {
            var root = FileService.GetTempDirectory(
                "content-move-markerless-native-rename-changed-retry-root");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(root, "destination", "Book");
            var targetFile = Path.Join(target, "book.m4b");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            var factory = _provider.GetRequiredService<
                IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                var entry = await db.MoveJobEntries.SingleAsync(candidate =>
                    candidate.MoveJobId == request.JobId
                    && candidate.EntryType == MoveJobEntryType.File);
                entry.Sha256 = null;
                await db.SaveChangesAsync();
            }
            var interruptedService = new AudiobookContentMoveService(
                _provider.GetRequiredService<
                    ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<
                    IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new FailOnceAfterMarkerlessNativeRename());

            await Assert.ThrowsAsync<IOException>(() =>
                interruptedService.MoveContentsAsync(
                    request,
                    CancellationToken.None));

            await File.WriteAllTextAsync(targetFile, "modified audio");
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();

            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.Contains(
                "changed",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal("modified audio", await File.ReadAllTextAsync(targetFile));
            AssertNoListenarrArtifacts(root);
        }

        [Fact]
        public async Task MoveContentsAsync_MarkerlessCrashBeforeTargetFileIdentity_PreservesUnprovenFinalFile()
        {
            var root = FileService.GetTempDirectory(
                "content-move-markerless-file-unproven-root");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(root, "destination", "Book");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            var interruptedService = new AudiobookContentMoveService(
                _provider.GetRequiredService<
                    ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<
                    IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new FailOnceAtCopyMutationPoint(
                    CopyMutationFaultPoint
                        .AfterMarkerlessFileCreationBeforeStateUpdate));

            await Assert.ThrowsAsync<IOException>(() =>
                interruptedService.MoveContentsAsync(
                    request,
                    CancellationToken.None));

            var targetFile = Path.Join(target, "book.m4b");
            Assert.True(File.Exists(targetFile));
            Assert.Equal(0, new FileInfo(targetFile).Length);
            Assert.True(File.Exists(Path.Join(source, "book.m4b")));
            AssertNoListenarrArtifacts(root);

            var factory = _provider.GetRequiredService<
                IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                var entry = await db.MoveJobEntries
                    .AsNoTracking()
                    .SingleAsync(candidate =>
                        candidate.MoveJobId == request.JobId
                        && candidate.EntryType == MoveJobEntryType.File);
                Assert.Equal(MoveJobEntryCopyState.Pending, entry.CopyState);
                Assert.Null(entry.TargetPhysicalObjectIdentity);
            }

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));
            Assert.Contains(
                "no persisted markerless ownership proof",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, new FileInfo(targetFile).Length);
            Assert.True(File.Exists(Path.Join(source, "book.m4b")));
            AssertNoListenarrArtifacts(root);
        }

        [Fact]
        public async Task MoveContentsAsync_MarkerlessTargetReplacedBeforeMetadataPreservation_DoesNotMutateReplacement()
        {
            var root = FileService.GetTempDirectory(
                "content-move-markerless-metadata-replacement-root");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            var sourceFile = await FileService.GetFileAsync(
                source,
                "book.m4b",
                "audio");
            var sourceTimestamp = new DateTime(
                2020,
                1,
                2,
                3,
                4,
                5,
                DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(sourceFile, sourceTimestamp);
            var target = Path.Join(root, "destination", "Book");
            var targetFile = Path.Join(target, "book.m4b");
            var replacementTimestamp = new DateTime(
                2021,
                6,
                7,
                8,
                9,
                10,
                DateTimeKind.Utc);
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            var injector = new ReplaceMarkerlessTargetAfterWrite(
                targetFile,
                replacementTimestamp);
            var service = new AudiobookContentMoveService(
                _provider.GetRequiredService<
                    ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<
                    IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                injector);

            var exception = await Record.ExceptionAsync(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.NotNull(exception);
            Assert.True(exception is IOException or InvalidOperationException);
            Assert.True(injector.Replaced);
            Assert.Equal("replacement", await File.ReadAllTextAsync(targetFile));
            Assert.Equal(
                replacementTimestamp,
                File.GetLastWriteTimeUtc(targetFile));
            Assert.True(File.Exists(injector.DisplacedPath));
        }

        [Fact]
        public async Task MoveContentsAsync_MarkerlessMetadataPreservationFailure_RemainsNonFatal()
        {
            var root = FileService.GetTempDirectory(
                "content-move-markerless-metadata-nonfatal-root");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(root, "destination", "Book");
            var targetFile = Path.Join(target, "book.m4b");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            var injector = new FailMarkerlessMetadataPreservationOnce();
            var service = new AudiobookContentMoveService(
                _provider.GetRequiredService<
                    ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<
                    IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                injector);

            var result = await service.MoveContentsAsync(
                request,
                CancellationToken.None);
            await service.FinalizeMoveAsync(
                request,
                result,
                CancellationToken.None);
            await service.CleanupCompletedMoveArtifactsAsync(
                request,
                result,
                CancellationToken.None);

            Assert.True(injector.Triggered);
            Assert.Equal("audio", await File.ReadAllTextAsync(targetFile));
            Assert.False(Directory.Exists(source));
            AssertNoListenarrArtifacts(root);
        }

        [LinuxFact]
        public async Task VerifyFinalizedMoveAsync_CrossFilesystemTimestampPrecision_UsesObservedTargetTimestamp()
        {
            var root = FileService.GetTempDirectory(
                "content-move-cross-filesystem-timestamp-precision");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            var sourceFile = await FileService.GetFileAsync(
                source,
                "book.m4b",
                "audio");
            var sourceTimestamp = new DateTime(
                2026,
                8,
                26,
                12,
                34,
                56,
                789,
                DateTimeKind.Utc).AddTicks(1234);
            File.SetLastWriteTimeUtc(sourceFile, sourceTimestamp);
            var target = Path.Join(root, "destination", "Book");
            var targetFile = Path.Join(target, "book.m4b");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            var service = new AudiobookContentMoveService(
                _provider.GetRequiredService<
                    ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<
                    IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new ForceCrossVolumeMoveFaultInjector());

            var result = await service.MoveContentsAsync(
                request,
                CancellationToken.None);
            Assert.NotNull(result.TargetVerificationLease);

            var coarseTargetTimestamp = new DateTime(
                sourceTimestamp.Ticks
                    - sourceTimestamp.Ticks % TimeSpan.TicksPerSecond,
                DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(targetFile, coarseTargetTimestamp);
            Assert.NotEqual(sourceTimestamp, File.GetLastWriteTimeUtc(targetFile));

            await service.VerifyFinalizedMoveAsync(
                request,
                CancellationToken.None,
                result.TargetVerificationLease);
            await service.VerifyFinalizedMoveAsync(
                request,
                CancellationToken.None,
                result.TargetVerificationLease);

            File.SetLastWriteTimeUtc(
                targetFile,
                coarseTargetTimestamp.AddSeconds(1));
            await service.VerifyFinalizedMoveAsync(
                request,
                CancellationToken.None,
                result.TargetVerificationLease);

            await File.WriteAllTextAsync(targetFile, "other");
            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.VerifyFinalizedMoveAsync(
                    request,
                    CancellationToken.None,
                    result.TargetVerificationLease));
            Assert.Contains(
                "failed final content verification",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
            result.TargetVerificationLease!.Dispose();
        }

        [LinuxFact]
        public async Task MoveContentsAsync_StalePlannedSourceTimestamp_RefreshesAfterPinnedContentProof()
        {
            var root = FileService.GetTempDirectory(
                "content-move-stale-source-timestamp");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            var sourceFile = await FileService.GetFileAsync(
                source,
                "book.m4b",
                "audio");
            var actualTimestamp = File.GetLastWriteTimeUtc(sourceFile);
            var target = Path.Join(root, "destination", "Book");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);

            var factory = _provider.GetRequiredService<
                IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                var entry = await db.MoveJobEntries.SingleAsync(candidate =>
                    candidate.MoveJobId == request.JobId
                    && candidate.EntryType == MoveJobEntryType.File);
                entry.LastWriteTimeUtc = actualTimestamp.AddSeconds(-2);
                await db.SaveChangesAsync();
            }

            var service = new AudiobookContentMoveService(
                _provider.GetRequiredService<
                    ILogger<AudiobookContentMoveService>>(),
                factory,
                TimeProvider.System,
                new ForceCrossVolumeMoveFaultInjector());

            var result = await service.MoveContentsAsync(
                request,
                CancellationToken.None);

            Assert.Equal(
                "audio",
                await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
            await using (var db = await factory.CreateDbContextAsync())
            {
                var persisted = await db.MoveJobEntries
                    .AsNoTracking()
                    .SingleAsync(candidate =>
                        candidate.MoveJobId == request.JobId
                        && candidate.EntryType == MoveJobEntryType.File);
                Assert.Equal(actualTimestamp, persisted.LastWriteTimeUtc);
                Assert.NotNull(persisted.Sha256);
            }
            result.TargetVerificationLease?.Dispose();
        }

        [Fact]
        public async Task MoveContentsAsync_MarkerlessRetryAfterTargetFileStateUpdate_Completes()
        {
            await AssertMarkerlessTargetFileRetryAsync(
                CopyMutationFaultPoint.AfterMarkerlessFileStateUpdate);
        }

        [Fact]
        public async Task MoveContentsAsync_MarkerlessRetryAfterTargetFileWrite_Completes()
        {
            await AssertMarkerlessTargetFileRetryAsync(
                CopyMutationFaultPoint
                    .AfterMarkerlessFileWriteBeforePublishedState);
        }

        private async Task AssertMarkerlessTargetFileRetryAsync(
            CopyMutationFaultPoint faultPoint)
        {
            var root = FileService.GetTempDirectory(
                "content-move-markerless-file-retry-root");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(root, "destination", "Book");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            var interruptedService = new AudiobookContentMoveService(
                _provider.GetRequiredService<
                    ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<
                    IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new FailOnceAtCopyMutationPoint(faultPoint));

            await Assert.ThrowsAsync<IOException>(() =>
                interruptedService.MoveContentsAsync(
                    request,
                    CancellationToken.None));

            var targetFile = Path.Join(target, "book.m4b");
            Assert.True(File.Exists(targetFile));
            AssertNoListenarrArtifacts(root);
            var factory = _provider.GetRequiredService<
                IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                var entry = await db.MoveJobEntries
                    .AsNoTracking()
                    .SingleAsync(candidate =>
                        candidate.MoveJobId == request.JobId
                        && candidate.EntryType == MoveJobEntryType.File);
                Assert.Equal(MoveJobEntryCopyState.Staged, entry.CopyState);
                Assert.False(string.IsNullOrWhiteSpace(
                    entry.TargetPhysicalObjectIdentity));
            }

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var result = await service.MoveContentsAsync(
                request,
                CancellationToken.None);
            await service.FinalizeMoveAsync(
                request,
                result,
                CancellationToken.None);
            await service.CleanupCompletedMoveArtifactsAsync(
                request,
                result,
                CancellationToken.None);

            Assert.False(Directory.Exists(source));
            Assert.Equal("audio", await File.ReadAllTextAsync(targetFile));
            AssertNoListenarrArtifacts(root);
        }

        [Fact]
        public async Task MoveContentsAsync_ForcedCopyRetention_OnSameVolume_NeverDeletesSource()
        {
            var root = FileService.GetTempDirectory(
                "content-move-forced-copy-retention");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            var sourceFile = await FileService.GetFileAsync(
                source,
                "book.m4b",
                "audio");
            var target = Path.Join(root, "destination", "Book");
            var request = (await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState)) with
            {
                ForceCopyAndRetainSource = true
            };
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();

            var result = await service.MoveContentsAsync(
                request,
                CancellationToken.None);

            Assert.True(result.SourceCleanupCompleted);
            Assert.True(result.SourceRetained);
            Assert.Equal("audio", await File.ReadAllTextAsync(sourceFile));
            Assert.Equal(
                "audio",
                await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task ResumeSourceCleanupAsync_ForcedCopyRetention_RejectsDestructiveResult()
        {
            var root = FileService.GetTempDirectory(
                "content-move-forced-retention-resume-guard");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            var sourceFile = await FileService.GetFileAsync(
                source,
                "book.m4b",
                "audio");
            var target = Path.Join(root, "destination", "Book");
            var request = (await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState)) with
            {
                ForceCopyAndRetainSource = true
            };
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var completed = await service.MoveContentsAsync(
                request,
                CancellationToken.None);
            var contradictory = completed with
            {
                SourceCleanupCompleted = true,
                SourceRetained = false
            };

            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.ResumeSourceCleanupAsync(
                    request,
                    contradictory,
                    CancellationToken.None));

            Assert.Contains(
                "Forced source retention cannot accept a destructive recovery result",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal("audio", await File.ReadAllTextAsync(sourceFile));
            Assert.Equal(
                "audio",
                await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
        }

        [LinuxFact]
        public async Task MoveContentsAsync_ForcedCrossVolumeCopiesAndDurablyRetainsSource()
        {
            var root = FileService.GetTempDirectory(
                "content-move-markerless-cross-volume-blocked");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            var sourceFile = await FileService.GetFileAsync(
                source,
                "book.m4b",
                "audio");
            var companionDirectory = Path.Join(source, "metadata");
            Directory.CreateDirectory(companionDirectory);
            var companionFile = await FileService.GetFileAsync(
                companionDirectory,
                "cover.jpg",
                "image");
            var target = Path.Join(root, "destination", "Book");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            var service = new AudiobookContentMoveService(
                _provider.GetRequiredService<
                    ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<
                    IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new ForceCrossVolumeMoveFaultInjector());

            var result = await service.MoveContentsAsync(
                request,
                CancellationToken.None);

            Assert.True(result.SourceCleanupCompleted);
            Assert.True(result.SourceRetained);
            Assert.Equal("audio", await File.ReadAllTextAsync(sourceFile));
            Assert.Equal("image", await File.ReadAllTextAsync(companionFile));
            Assert.Equal(
                "audio",
                await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
            Assert.Equal(
                "image",
                await File.ReadAllTextAsync(
                    Path.Join(target, "metadata", "cover.jpg")));
            Assert.True(Directory.Exists(target));
            AssertNoListenarrArtifacts(root);
            await using var db = await _provider
                .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
                .CreateDbContextAsync();
            var persisted = await db.MoveJobs
                .AsNoTracking()
                .Include(job => job.Entries)
                .SingleAsync(job => job.Id == request.JobId);
            Assert.Equal(
                MoveJobEntryCleanupState.Retained,
                persisted.SourceDirectoryCleanupState);
            Assert.All(
                persisted.Entries.Where(entry =>
                    !MoveManifestIdentity.IsBoundaryAuthorization(entry)),
                entry => Assert.Equal(
                    MoveJobEntryCleanupState.Retained,
                    entry.CleanupState));

            var recovered = await service.MoveContentsAsync(
                request,
                CancellationToken.None);
            Assert.True(recovered.SourceRetained);
            Assert.Equal("audio", await File.ReadAllTextAsync(sourceFile));
            Assert.Equal("image", await File.ReadAllTextAsync(companionFile));
        }

        [LinuxFact]
        public async Task MoveContentsAsync_ForcedCrossVolumeAuthorized_DeletesFilesButKeepsManagedRoot()
        {
            var root = FileService.GetTempDirectory(
                "content-move-markerless-cross-volume-authorized");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            var sourceFile = await FileService.GetFileAsync(
                source,
                "book.m4b",
                "audio");
            var target = Path.Join(root, "destination", "Book");
            var request = (await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: source,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState)) with
            {
                DeleteEmptySource = false,
                SourceCleanupMode = MoveSourceCleanupMode.DeleteAfterVerifiedCopy,
                SourceRootFolderId = 1,
                SourcePolicyRevision = 3,
                TargetRootFolderId = 2,
                TargetPolicyRevision = 4
            };
            var service = new AudiobookContentMoveService(
                _provider.GetRequiredService<
                    ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<
                    IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new ForceCrossVolumeMoveFaultInjector(),
                sourceCleanupPolicyResolver: new AlwaysCurrentMoveCleanupPolicyResolver());

            var result = await service.MoveContentsAsync(
                request,
                CancellationToken.None);

            Assert.True(result.SourceCleanupCompleted);
            Assert.False(result.SourceRetained);
            Assert.False(File.Exists(sourceFile));
            Assert.True(Directory.Exists(source));
            Assert.Equal(
                "audio",
                await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
        }

        [LinuxFact]
        public async Task MoveContentsAsync_CrossVolumeRetentionInterrupted_ResumesWithoutDeletingSource()
        {
            var root = FileService.GetTempDirectory(
                "content-move-markerless-cross-volume-retention-retry");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            var sourceFile = await FileService.GetFileAsync(
                source,
                "book.m4b",
                "audio");
            var companionFile = await FileService.GetFileAsync(
                source,
                "cover.jpg",
                "image");
            var target = Path.Join(root, "destination", "Book");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            var faultInjector = new FailOnceDuringCrossVolumeRetention();
            var service = new AudiobookContentMoveService(
                _provider.GetRequiredService<
                    ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<
                    IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                faultInjector);

            await Assert.ThrowsAsync<IOException>(() => service.MoveContentsAsync(
                request,
                CancellationToken.None));

            Assert.Equal("audio", await File.ReadAllTextAsync(sourceFile));
            Assert.Equal("image", await File.ReadAllTextAsync(companionFile));
            await using (var interruptedDb = await _provider
                .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
                .CreateDbContextAsync())
            {
                var interruptedEntries = await interruptedDb.MoveJobEntries
                    .AsNoTracking()
                    .Where(entry => entry.MoveJobId == request.JobId)
                    .ToListAsync();
                interruptedEntries = interruptedEntries
                    .Where(entry =>
                        !MoveManifestIdentity.IsBoundaryAuthorization(entry))
                    .ToList();
                Assert.Contains(interruptedEntries, entry =>
                    entry.CleanupState == MoveJobEntryCleanupState.Retained);
                Assert.Contains(interruptedEntries, entry =>
                    entry.CleanupState == MoveJobEntryCleanupState.Pending);
            }

            var recovered = await service.MoveContentsAsync(
                request,
                CancellationToken.None);

            Assert.True(recovered.SourceRetained);
            Assert.NotNull(recovered.TargetVerificationLease);
            Assert.Equal("audio", await File.ReadAllTextAsync(sourceFile));
            Assert.Equal("image", await File.ReadAllTextAsync(companionFile));
            Assert.Equal(
                "audio",
                await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
            Assert.Equal(
                "image",
                await File.ReadAllTextAsync(Path.Join(target, "cover.jpg")));
        }

        [LinuxFact]
        public async Task GetRecoverableMoveAsync_MixedRetainedAndDeletedDisposition_FailsClosed()
        {
            var root = FileService.GetTempDirectory(
                "content-move-markerless-cross-volume-mixed-cleanup");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var companionFile = await FileService.GetFileAsync(
                source,
                "cover.jpg",
                "image");
            var target = Path.Join(root, "destination", "Book");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            var service = new AudiobookContentMoveService(
                _provider.GetRequiredService<
                    ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<
                    IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new ForceCrossVolumeMoveFaultInjector());

            var result = await service.MoveContentsAsync(
                request,
                CancellationToken.None);
            result.TargetVerificationLease?.Dispose();
            File.Delete(companionFile);
            await using (var db = await _provider
                .GetRequiredService<IDbContextFactory<ListenArrDbContext>>()
                .CreateDbContextAsync())
            {
                var companionEntry = await db.MoveJobEntries.SingleAsync(entry =>
                    entry.MoveJobId == request.JobId
                    && entry.RelativePath == "cover.jpg");
                companionEntry.CleanupState = MoveJobEntryCleanupState.Deleted;
                await db.SaveChangesAsync();
            }

            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(
                () => service.GetRecoverableMoveAsync(
                    request,
                    CancellationToken.None));

            Assert.Contains(
                "mixes retained and deleted",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Join(source, "book.m4b")));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
            Assert.True(File.Exists(Path.Join(target, "cover.jpg")));
        }

        [Fact]
        public async Task MoveContentsAsync_MarkerlessRetryAfterDirectoryCreationBeforeStateUpdate_RetainsAndCompletes()
        {
            await AssertMarkerlessDirectoryCreationRetryAsync(
                TargetScaffoldPreparationFaultPoint
                    .AfterMarkerlessDirectoryCreationBeforeStateUpdate,
                MoveCreatedDirectoryState.Planned,
                expectIdentity: false);
        }

        [Fact]
        public async Task MoveContentsAsync_MarkerlessRetryAfterDirectoryStateUpdate_Completes()
        {
            await AssertMarkerlessDirectoryCreationRetryAsync(
                TargetScaffoldPreparationFaultPoint
                    .AfterMarkerlessDirectoryStateUpdate,
                OperatingSystem.IsWindows()
                    ? MoveCreatedDirectoryState.Created
                    : MoveCreatedDirectoryState.Retained,
                expectIdentity: true);
        }

        [LinuxFact]
        [System.Runtime.Versioning.SupportedOSPlatform("linux")]
        public async Task CleanupTerminalTargetScaffoldingAsync_InaccessiblePlannedDirectory_DoesNotMarkRemoved()
        {
            var root = FileService.GetTempDirectory(
                "content-move-inaccessible-target-scaffold-root");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var targetParent = Path.Join(root, "destination");
            Directory.CreateDirectory(targetParent);
            var target = Path.Join(targetParent, "Book");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            var interruptedService = new AudiobookContentMoveService(
                _provider.GetRequiredService<
                    ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<
                    IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new FailOnceAtTargetScaffoldPreparationPoint(
                    TargetScaffoldPreparationFaultPoint
                        .AfterMarkerlessDirectoryCreationBeforeStateUpdate));

            await Assert.ThrowsAsync<IOException>(() =>
                interruptedService.MoveContentsAsync(
                    request,
                    CancellationToken.None));

            var factory = _provider.GetRequiredService<
                IDbContextFactory<ListenArrDbContext>>();
            await using (var interruptedDb = await factory.CreateDbContextAsync())
            {
                var planned = await interruptedDb.MoveJobCreatedDirectories
                    .AsNoTracking()
                    .SingleAsync(directory =>
                        directory.MoveJobId == request.JobId
                        && directory.Path == target);
                Assert.Equal(MoveCreatedDirectoryState.Planned, planned.State);
                Assert.Null(planned.DirectoryObjectIdentity);
            }
            Assert.True(Directory.Exists(target));

            var originalMode = File.GetUnixFileMode(targetParent);
            File.SetUnixFileMode(targetParent, UnixFileMode.None);
            try
            {
                if (!Directory.Exists(target))
                {
                    var service = _provider.GetRequiredService<
                        AudiobookContentMoveService>();
                    var exception = await Record.ExceptionAsync(() =>
                        service.CleanupTerminalTargetScaffoldingAsync(
                            request,
                            CancellationToken.None));
                    Assert.NotNull(exception);
                    Assert.IsNotType<MoveNeedsAttentionException>(exception);

                    await using var blockedDb = await factory.CreateDbContextAsync();
                    var blocked = await blockedDb.MoveJobCreatedDirectories
                        .AsNoTracking()
                        .SingleAsync(directory =>
                            directory.MoveJobId == request.JobId
                            && directory.Path == target);
                    Assert.Equal(MoveCreatedDirectoryState.Planned, blocked.State);
                }
            }
            finally
            {
                File.SetUnixFileMode(targetParent, originalMode);
            }

            await _provider.GetRequiredService<AudiobookContentMoveService>()
                .CleanupTerminalTargetScaffoldingAsync(
                    request,
                    CancellationToken.None);

            await using var verification = await factory.CreateDbContextAsync();
            var retained = await verification.MoveJobCreatedDirectories
                .AsNoTracking()
                .SingleAsync(directory =>
                    directory.MoveJobId == request.JobId
                    && directory.Path == target);
            Assert.Equal(MoveCreatedDirectoryState.Retained, retained.State);
            Assert.True(Directory.Exists(target));
        }

        private async Task AssertMarkerlessDirectoryCreationRetryAsync(
            TargetScaffoldPreparationFaultPoint faultPoint,
            MoveCreatedDirectoryState expectedInterruptedState,
            bool expectIdentity)
        {
            var root = FileService.GetTempDirectory(
                "content-move-markerless-directory-retry-root");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(root, "destination", "Author", "Book");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            var interruptedService = new AudiobookContentMoveService(
                _provider.GetRequiredService<
                    ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<
                    IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new FailOnceAtTargetScaffoldPreparationPoint(faultPoint));

            await Assert.ThrowsAsync<IOException>(() =>
                interruptedService.MoveContentsAsync(
                    request,
                    CancellationToken.None));

            var factory = _provider.GetRequiredService<
                IDbContextFactory<ListenArrDbContext>>();
            string interruptedPath;
            await using (var db = await factory.CreateDbContextAsync())
            {
                var directories = await db.MoveJobCreatedDirectories
                    .AsNoTracking()
                    .Where(directory => directory.MoveJobId == request.JobId)
                    .OrderBy(directory => directory.Id)
                    .ToListAsync();
                var interrupted = Assert.Single(
                    directories,
                    directory => Directory.Exists(directory.Path));
                Assert.Equal(expectedInterruptedState, interrupted.State);
                Assert.Equal(
                    expectIdentity,
                    !string.IsNullOrWhiteSpace(
                        interrupted.DirectoryObjectIdentity));
                interruptedPath = interrupted.Path;
            }
            Assert.Empty(Directory.EnumerateFileSystemEntries(interruptedPath));
            AssertNoListenarrArtifacts(root);

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var result = await service.MoveContentsAsync(
                request,
                CancellationToken.None);
            await service.FinalizeMoveAsync(
                request,
                result,
                CancellationToken.None);
            await service.CleanupCompletedMoveArtifactsAsync(
                request,
                result,
                CancellationToken.None);

            Assert.False(Directory.Exists(source));
            Assert.Equal("audio", await File.ReadAllTextAsync(
                Path.Join(target, "book.m4b")));
            AssertNoListenarrArtifacts(root);
            await using var verification = await factory.CreateDbContextAsync();
            var recovered = await verification.MoveJobCreatedDirectories
                .AsNoTracking()
                .SingleAsync(directory =>
                    directory.MoveJobId == request.JobId
                    && directory.Path == interruptedPath);
            Assert.Equal(MoveCreatedDirectoryState.Retained, recovered.State);
            Assert.False(string.IsNullOrWhiteSpace(
                recovered.DirectoryObjectIdentity));
        }

        [Fact]
        public async Task MoveContentsAsync_TargetContentChangedAfterDeleteAuthorization_DoesNotDeleteSource()
        {
            var root = FileService.GetTempDirectory(
                "content-move-markerless-target-change-before-delete-root");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            var sourceFile = await FileService.GetFileAsync(
                source,
                "book.m4b",
                "audio");
            var target = Path.Join(root, "destination", "Book");
            var targetFile = Path.Join(target, "book.m4b");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            var service = new AudiobookContentMoveService(
                _provider.GetRequiredService<
                    ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<
                    IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new MutateFileAfterDeleteAuthorization(targetFile, "other"));

            await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(
                    request,
                    CancellationToken.None));

            Assert.True(File.Exists(sourceFile));
            Assert.Equal("audio", await File.ReadAllTextAsync(sourceFile));
            Assert.Equal("other", await File.ReadAllTextAsync(targetFile));
            var factory = _provider.GetRequiredService<
                IDbContextFactory<ListenArrDbContext>>();
            await using var verification = await factory.CreateDbContextAsync();
            var entry = await verification.MoveJobEntries
                .AsNoTracking()
                .SingleAsync(candidate =>
                    candidate.MoveJobId == request.JobId
                    && candidate.EntryType == MoveJobEntryType.File);
            Assert.Equal(
                MoveJobEntryCleanupState.DeleteAuthorized,
                entry.CleanupState);
            AssertNoListenarrArtifacts(root);
        }

        [LinuxFact]
        public async Task MoveContentsAsync_SourceContentChangedAfterDeleteAuthorization_DoesNotDeleteSource()
        {
            var root = FileService.GetTempDirectory(
                "content-move-markerless-source-change-before-delete-root");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            var sourceFile = await FileService.GetFileAsync(
                source,
                "book.m4b",
                "audio");
            var target = Path.Join(root, "destination", "Book");
            var targetFile = Path.Join(target, "book.m4b");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            var service = new AudiobookContentMoveService(
                _provider.GetRequiredService<
                    ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<
                    IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new MutateFileAfterDeleteAuthorization(sourceFile, "other"));

            await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(
                    request,
                    CancellationToken.None));

            Assert.True(File.Exists(sourceFile));
            Assert.Equal("other", await File.ReadAllTextAsync(sourceFile));
            Assert.Equal("audio", await File.ReadAllTextAsync(targetFile));
            var factory = _provider.GetRequiredService<
                IDbContextFactory<ListenArrDbContext>>();
            await using var verification = await factory.CreateDbContextAsync();
            var entry = await verification.MoveJobEntries
                .AsNoTracking()
                .SingleAsync(candidate =>
                    candidate.MoveJobId == request.JobId
                    && candidate.EntryType == MoveJobEntryType.File);
            Assert.Equal(
                MoveJobEntryCleanupState.DeleteAuthorized,
                entry.CleanupState);
            AssertNoListenarrArtifacts(root);
        }

        [Fact]
        public async Task MoveContentsAsync_MarkerlessRetryAfterSourceDeleteBeforeStateUpdate_Completes()
        {
            await AssertMarkerlessSourceCleanupRetryAsync(
                SourceCleanupFaultPoint
                    .AfterMarkerlessSourceFileDeleteBeforeStateUpdate,
                MoveJobEntryCleanupState.DeleteAuthorized);
        }

        [Fact]
        public async Task MoveContentsAsync_MarkerlessRetryAfterSourceDeleteStateUpdate_Completes()
        {
            await AssertMarkerlessSourceCleanupRetryAsync(
                SourceCleanupFaultPoint.AfterMarkerlessSourceFileStateUpdate,
                MoveJobEntryCleanupState.Deleted);
        }

        [LinuxFact]
        [System.Runtime.Versioning.SupportedOSPlatform("linux")]
        public async Task ResumeSourceCleanupAsync_InaccessibleRecreatedDeleteAuthorizedSource_DoesNotAdvanceDeleted()
        {
            var root = FileService.GetTempDirectory(
                "content-move-markerless-inaccessible-cleanup-root");
            var source = Path.Join(root, "source");
            var sourceDisc = Path.Join(source, "Disc");
            Directory.CreateDirectory(sourceDisc);
            var sourceFile = await FileService.GetFileAsync(
                sourceDisc,
                "book.m4b",
                "audio");
            var target = Path.Join(root, "destination", "Book");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            var interruptedService = new AudiobookContentMoveService(
                _provider.GetRequiredService<
                    ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<
                    IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new FailOnceAtSourceCleanupPoint(
                    SourceCleanupFaultPoint
                        .AfterMarkerlessSourceFileDeleteBeforeStateUpdate));

            await Assert.ThrowsAsync<IOException>(() =>
                interruptedService.MoveContentsAsync(
                    request,
                    CancellationToken.None));

            var factory = _provider.GetRequiredService<
                IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                var interruptedEntry = await db.MoveJobEntries
                    .AsNoTracking()
                    .SingleAsync(entry =>
                        entry.MoveJobId == request.JobId
                        && entry.EntryType == MoveJobEntryType.File);
                Assert.Equal(
                    MoveJobEntryCleanupState.DeleteAuthorized,
                    interruptedEntry.CleanupState);
            }
            Assert.False(File.Exists(sourceFile));

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var recovered = new AudiobookContentMoveResult(
                source,
                target,
                TargetInsideSource: false,
                SourceInsideTarget: false,
                SourceCleanupCompleted: false,
                SourceRetained: false,
                TargetPhysicalObjectIdentities:
                    new Dictionary<string, string>());

            await File.WriteAllTextAsync(sourceFile, "audio");
            var originalMode = File.GetUnixFileMode(sourceDisc);
            File.SetUnixFileMode(sourceDisc, UnixFileMode.None);
            try
            {
                // A privileged test process can bypass Unix directory permissions.
                // The Linux validation environment runs unprivileged and exercises
                // the access-denied branch that File.Exists used to collapse to false.
                if (!File.Exists(sourceFile))
                {
                    var exception = await Record.ExceptionAsync(() =>
                        service.ResumeSourceCleanupAsync(
                            request,
                            recovered,
                            CancellationToken.None));
                    Assert.NotNull(exception);
                    Assert.True(
                        exception is UnauthorizedAccessException
                            or IOException
                            or System.ComponentModel.Win32Exception,
                        exception.ToString());

                    await using var blockedDb =
                        await factory.CreateDbContextAsync();
                    var blockedEntry = await blockedDb.MoveJobEntries
                        .AsNoTracking()
                        .SingleAsync(entry =>
                            entry.MoveJobId == request.JobId
                            && entry.EntryType == MoveJobEntryType.File);
                    Assert.Equal(
                        MoveJobEntryCleanupState.DeleteAuthorized,
                        blockedEntry.CleanupState);
                }
            }
            finally
            {
                File.SetUnixFileMode(sourceDisc, originalMode);
            }

            await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.ResumeSourceCleanupAsync(
                    request,
                    recovered,
                    CancellationToken.None));

            await using var verification = await factory.CreateDbContextAsync();
            var finalEntry = await verification.MoveJobEntries
                .AsNoTracking()
                .SingleAsync(entry =>
                    entry.MoveJobId == request.JobId
                    && entry.EntryType == MoveJobEntryType.File);
            Assert.Equal(
                MoveJobEntryCleanupState.DeleteAuthorized,
                finalEntry.CleanupState);
            Assert.True(File.Exists(sourceFile));
            Assert.Equal(
                "audio",
                await File.ReadAllTextAsync(Path.Join(target, "Disc", "book.m4b")));
        }

        private async Task AssertMarkerlessSourceCleanupRetryAsync(
            SourceCleanupFaultPoint faultPoint,
            MoveJobEntryCleanupState expectedInterruptedState)
        {
            var root = FileService.GetTempDirectory(
                "content-move-markerless-cleanup-retry-root");
            var source = Path.Join(root, "source");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "first.m4b", "first");
            await FileService.GetFileAsync(source, "second.m4b", "second");
            var target = Path.Join(root, "destination", "Book");
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: root,
                executionProtocolVersion:
                    MoveExecutionProtocol.MarkerlessDatabaseState);
            var interruptedService = new AudiobookContentMoveService(
                _provider.GetRequiredService<
                    ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<
                    IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new FailOnceAtSourceCleanupPoint(faultPoint));

            await Assert.ThrowsAsync<IOException>(() =>
                interruptedService.MoveContentsAsync(
                    request,
                    CancellationToken.None));

            var factory = _provider.GetRequiredService<
                IDbContextFactory<ListenArrDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                var sourceEntries = await db.MoveJobEntries
                    .AsNoTracking()
                    .Where(entry => entry.MoveJobId == request.JobId)
                    .Where(entry => entry.EntryType == MoveJobEntryType.File)
                    .OrderBy(entry => entry.Id)
                    .ToListAsync();
                var interruptedEntry = Assert.Single(
                    sourceEntries,
                    entry => entry.CleanupState == expectedInterruptedState);
                Assert.False(File.Exists(Path.Join(
                    source,
                    interruptedEntry.RelativePath)));
                Assert.Single(
                    sourceEntries,
                    entry => entry.CleanupState
                        == MoveJobEntryCleanupState.Pending);
            }
            AssertNoListenarrArtifacts(root);

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var result = await service.MoveContentsAsync(
                request,
                CancellationToken.None);
            await service.FinalizeMoveAsync(
                request,
                result,
                CancellationToken.None);
            await service.CleanupCompletedMoveArtifactsAsync(
                request,
                result,
                CancellationToken.None);

            Assert.False(Directory.Exists(source));
            Assert.Equal("first", await File.ReadAllTextAsync(
                Path.Join(target, "first.m4b")));
            Assert.Equal("second", await File.ReadAllTextAsync(
                Path.Join(target, "second.m4b")));
            AssertNoListenarrArtifacts(root);
        }

        [Fact]
        public async Task MoveContentsAsync_OwnedSourceMovesWithoutPublishingOwnershipSidecars()
        {
            var source = FileService.GetTempDirectory("content-move-owned-source");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"content-move-owned-source-dst-{Guid.NewGuid():N}");
            var ownershipStore = _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>();
            _ = await ownershipStore.RecordCreatedAsync(
                new LibraryDirectoryOwnershipClaim(
                    source,
                    FileSystemPathSemantics.CurrentHostDefault,
                    "test"));
            var service = new AudiobookContentMoveService(
                _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                directoryOwnershipStore: ownershipStore);
            var request = await CreateLeasedMoveRequestAsync(source, target);

            await service.MoveContentsAsync(request, CancellationToken.None);

            Assert.False(Directory.Exists(source));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
            var resolution = await ownershipStore.ResolveOwnedAsync(
                source,
                FileSystemPathSemantics.CurrentHostDefault);
            Assert.Equal(LibraryDirectoryOwnershipResolutionState.Unowned, resolution.State);
        }

        [Fact]
        public async Task OwnedEmptyTarget_IsRevalidatedAcrossMoveFinalizationAndArtifactCleanup()
        {
            var source = FileService.GetTempDirectory("content-move-owned-target-src");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = FileService.GetTempDirectory("content-move-owned-target-dst");
            var ownershipStore = _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>();
            _ = await ownershipStore.RecordCreatedAsync(
                new LibraryDirectoryOwnershipClaim(
                    target,
                    FileSystemPathSemantics.CurrentHostDefault,
                    "test"));
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = await CreateLeasedMoveRequestAsync(source, target);

            var result = await service.MoveContentsAsync(request, CancellationToken.None);
            await service.FinalizeMoveAsync(request, result, CancellationToken.None);
            await service.CleanupCompletedMoveArtifactsAsync(
                request,
                result,
                CancellationToken.None);

            Assert.False(Directory.Exists(source));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
            var resolution = await ownershipStore.ResolveOwnedAsync(
                target,
                FileSystemPathSemantics.CurrentHostDefault);
            Assert.Equal(LibraryDirectoryOwnershipResolutionState.Owned, resolution.State);
        }

        [Fact]
        public async Task OwnedTargetGenerationChangedAfterPublication_BlocksSourceCleanup()
        {
            var source = FileService.GetTempDirectory("content-move-owned-target-tamper-src");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = FileService.GetTempDirectory("content-move-owned-target-tamper-dst");
            var ownershipStore = _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>();
            await ownershipStore.RecordCreatedAsync(
                new LibraryDirectoryOwnershipClaim(
                    target,
                    FileSystemPathSemantics.CurrentHostDefault,
                    "test"));
            var service = new AudiobookContentMoveService(
                _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new ReplaceTargetDirectoryAfterPublish(target),
                directoryOwnershipStore: ownershipStore);
            var request = await CreateLeasedMoveRequestAsync(source, target);

            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.Contains("changed physical generation", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Join(source, "book.m4b")));
            Assert.False(File.Exists(Path.Join(target, "book.m4b")));
            Assert.True(File.Exists(Path.Join(target + ".original", "book.m4b")));
        }

        private async Task ClaimOwnedDirectoriesAsync(params string[] directories)
        {
            var ownershipStore = _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>();
            foreach (var directory in directories)
            {
                await ownershipStore.RecordCreatedAsync(
                    new LibraryDirectoryOwnershipClaim(
                        directory,
                        FileSystemPathSemantics.CurrentHostDefault,
                        "test"));
            }
        }

        private async Task PersistFileManifestAsync(
            Guid jobId,
            string relativePath,
            string sourceFile)
        {
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    await File.ReadAllBytesAsync(sourceFile)));
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var existing = await db.MoveJobEntries
                .Where(entry => entry.MoveJobId == jobId)
                .ToListAsync();
            db.MoveJobEntries.RemoveRange(existing.Where(entry =>
                !MoveManifestIdentity.IsBoundaryAuthorization(entry)));
            db.MoveJobEntries.Add(new MoveJobEntry
            {
                MoveJobId = jobId,
                RelativePath = relativePath,
                EntryType = MoveJobEntryType.File,
                Length = new FileInfo(sourceFile).Length,
                LastWriteTimeUtc = File.GetLastWriteTimeUtc(sourceFile),
                Sha256 = hash
            });
            await db.SaveChangesAsync();
        }

        private async Task<AudiobookContentMoveRequest> CreateLeasedMoveRequestAsync(
            string source,
            string target,
            Guid? jobId = null,
            bool deleteEmptySource = true,
            FileSystemPathSemantics? sourceSemantics = null,
            FileSystemPathSemantics? targetSemantics = null,
            string? sourceCleanupBoundary = null,
            int executionProtocolVersion =
                MoveExecutionProtocol.Current)
        {
            var id = jobId ?? Guid.NewGuid();
            var effectiveTargetSemantics =
                targetSemantics
                ?? sourceSemantics
                ?? FileSystemPathSemantics.CurrentHostDefault;
            var effectiveSourceSemantics =
                sourceSemantics ?? FileSystemPathSemantics.CurrentHostDefault;
            var sourceBoundary = IsTestFilesystemRoot(source, effectiveSourceSemantics)
                ? Path.GetFullPath(source)
                : FindMoveTargetBoundary(source, effectiveSourceSemantics);
            var targetBoundary = FindMoveTargetBoundary(target, effectiveTargetSemantics);
            var directoryIdentityResolver = _provider
                .GetRequiredService<IDirectoryObjectIdentityResolver>();
            var sourceDirectoryIdentity = await directoryIdentityResolver
                .ResolveAsync(sourceBoundary);
            Assert.True(
                sourceDirectoryIdentity.IsAvailable,
                sourceDirectoryIdentity.UnavailableReason);
            var targetDirectoryIdentity = await directoryIdentityResolver
                .ResolveAsync(targetBoundary);
            Assert.True(
                targetDirectoryIdentity.IsAvailable,
                targetDirectoryIdentity.UnavailableReason);
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var job = new MoveJob
            {
                Id = id,
                AudiobookId = 1,
                RequestedPath = target,
                SourcePath = source,
                Status = MoveJobStatus.Running,
                LeaseOwner = TestLeaseOwner,
                LeaseGeneration = 1,
                LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
                ActiveDeduplicationKey = $"test:{id:N}",
                IdentityKeyVersion = MoveManifestIdentity.Version,
                ExecutionProtocolVersion = executionProtocolVersion,
                Entries =
                [
                    MoveManifestIdentity.CreateSourceBoundaryAuthorization(
                        sourceDirectoryIdentity.Version!.Value,
                        sourceDirectoryIdentity.Value!),
                    MoveManifestIdentity.CreateTargetBoundaryAuthorization(
                        targetDirectoryIdentity.Version!.Value,
                        targetDirectoryIdentity.Value!)
                ]
            };
            job.SetSourceIdentity(new PathIdentitySnapshot(
                effectiveSourceSemantics.Syntax,
                effectiveSourceSemantics.CaseSensitivity,
                FileSystemCaseSensitivityMode.Auto,
                sourceBoundary));
            job.SetTargetIdentity(new PathIdentitySnapshot(
                effectiveTargetSemantics.Syntax,
                effectiveTargetSemantics.CaseSensitivity,
                FileSystemCaseSensitivityMode.Auto,
                targetBoundary));
            db.MoveJobs.Add(job);
            await db.SaveChangesAsync();
            if (!IsTestFilesystemRoot(
                    source,
                    sourceSemantics ?? FileSystemPathSemantics.CurrentHostDefault))
            {
                await PersistCurrentSourceManifestAsync(id, source);
            }

            return new AudiobookContentMoveRequest(
                source,
                target,
                id,
                deleteEmptySource,
                sourceSemantics ?? FileSystemPathSemantics.CurrentHostDefault,
                effectiveTargetSemantics,
                LeaseToken(1),
                sourceCleanupBoundary);
        }

        private async Task AuthorizeExistingMoveJobTargetAsync(
            Guid jobId,
            string target,
            FileSystemPathSemantics? targetSemantics = null)
        {
            var semantics = targetSemantics ?? FileSystemPathSemantics.CurrentHostDefault;
            var targetBoundary = FindMoveTargetBoundary(target, semantics);
            var targetDirectoryIdentity = await _provider
                .GetRequiredService<IDirectoryObjectIdentityResolver>()
                .ResolveAsync(targetBoundary);
            Assert.True(
                targetDirectoryIdentity.IsAvailable,
                targetDirectoryIdentity.UnavailableReason);

            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var job = await db.MoveJobs
                .Include(candidate => candidate.Entries)
                .SingleAsync(candidate => candidate.Id == jobId);
            job.IdentityKeyVersion = MoveManifestIdentity.Version;
            job.SetTargetIdentity(new PathIdentitySnapshot(
                semantics.Syntax,
                semantics.CaseSensitivity,
                FileSystemCaseSensitivityMode.Auto,
                targetBoundary));
            if (!job.Entries.Any(MoveManifestIdentity.IsTargetBoundaryAuthorization))
            {
                job.Entries.Add(MoveManifestIdentity.CreateTargetBoundaryAuthorization(
                    targetDirectoryIdentity.Version!.Value,
                    targetDirectoryIdentity.Value!));
            }
            await db.SaveChangesAsync();
        }

        private async Task ClearPersistedManifestAsync(Guid jobId)
        {
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var entries = await db.MoveJobEntries
                .Where(entry => entry.MoveJobId == jobId)
                .ToListAsync();
            db.MoveJobEntries.RemoveRange(entries.Where(entry =>
                !MoveManifestIdentity.IsBoundaryAuthorization(entry)));
            await db.SaveChangesAsync();
        }

        private string FindMoveTargetBoundary(
            string targetPath,
            FileSystemPathSemantics targetSemantics)
        {
            var target = Path.GetFullPath(targetPath);
            var managedRoot = Path.GetFullPath(FileService.GetTempPath());
            if (IsTestFilesystemRoot(target, targetSemantics))
            {
                // Endpoint-root tests must reach the production endpoint guard without
                // trying to enroll the host filesystem root as a side effect of setup.
                return managedRoot;
            }

            if (FileSystemPathIdentity.IsSameOrInside(
                    target,
                    managedRoot,
                    targetSemantics))
            {
                // Production jobs authorize the configured library/output boundary,
                // not an already-existing content destination inside that boundary.
                return managedRoot;
            }

            var current = Path.GetDirectoryName(target);
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

        private static bool IsTestFilesystemRoot(
            string path,
            FileSystemPathSemantics semantics)
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrWhiteSpace(root)
                && FileSystemPathIdentity.AreEquivalent(path, root, semantics);
        }

        private async Task PersistCurrentSourceManifestAsync(
            Guid jobId,
            string source)
        {
            if (!Directory.Exists(source))
            {
                return;
            }

            var entries = new List<MoveJobEntry>();
            var pending = new Stack<string>();
            pending.Push(source);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    var relativePath = Path.GetRelativePath(source, path);
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        entries.Add(new MoveJobEntry
                        {
                            MoveJobId = jobId,
                            RelativePath = relativePath,
                            EntryType = MoveJobEntryType.Directory,
                            LastWriteTimeUtc = Directory.GetLastWriteTimeUtc(path)
                        });
                        pending.Push(path);
                        continue;
                    }

                    var bytes = await File.ReadAllBytesAsync(path);
                    entries.Add(new MoveJobEntry
                    {
                        MoveJobId = jobId,
                        RelativePath = relativePath,
                        EntryType = MoveJobEntryType.File,
                        Length = bytes.LongLength,
                        LastWriteTimeUtc = File.GetLastWriteTimeUtc(path),
                        Sha256 = Convert.ToHexString(
                            System.Security.Cryptography.SHA256.HashData(bytes))
                    });
                }
            }

            if (entries.Count == 0)
            {
                return;
            }

            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            db.MoveJobEntries.AddRange(entries);
            await db.SaveChangesAsync();
        }

        private sealed class FailingMarkRemovedOwnershipStore(
            ILibraryDirectoryOwnershipStore inner) : ILibraryDirectoryOwnershipStore
        {
            public Task<LibraryDirectoryOwnership> RecordCreatedAsync(
                LibraryDirectoryOwnershipClaim claim,
                CancellationToken cancellationToken = default) =>
                inner.RecordCreatedAsync(claim, cancellationToken);

            public Task<IReadOnlyList<LibraryDirectoryOwnership>> EnsureCreatedHierarchyAsync(
                string destinationDirectory,
                string managedBoundary,
                FileSystemPathSemantics semantics,
                string creationWorkflow,
                Guid? creationOperationId = null,
                int? audiobookId = null,
                CancellationToken cancellationToken = default) =>
                inner.EnsureCreatedHierarchyAsync(
                    destinationDirectory,
                    managedBoundary,
                    semantics,
                    creationWorkflow,
                    creationOperationId,
                    audiobookId,
                    cancellationToken);

            public Task<LibraryDirectoryOwnershipResolution> ResolveOwnedAsync(
                string path,
                FileSystemPathSemantics semantics,
                CancellationToken cancellationToken = default) =>
                inner.ResolveOwnedAsync(path, semantics, cancellationToken);

            public Task<IReadOnlyList<LibraryDirectoryOwnership>> GetOwnedWithinAsync(
                string basePath,
                FileSystemPathSemantics semantics,
                CancellationToken cancellationToken = default) =>
                inner.GetOwnedWithinAsync(basePath, semantics, cancellationToken);

            public Task<bool> TryRetireReplacedByMarkerlessMoveAsync(
                string path,
                FileSystemPathSemantics semantics,
                Guid moveJobId,
                string replacementDirectoryObjectIdentity,
                CancellationToken cancellationToken = default) =>
                inner.TryRetireReplacedByMarkerlessMoveAsync(
                    path,
                    semantics,
                    moveJobId,
                    replacementDirectoryObjectIdentity,
                    cancellationToken);

            public Task BeginRemovalAsync(
                long ownershipId,
                string expectedOwnershipKey,
                CancellationToken cancellationToken = default) =>
                inner.BeginRemovalAsync(
                    ownershipId,
                    expectedOwnershipKey,
                    cancellationToken);

            public Task RetainAsync(
                long ownershipId,
                string expectedOwnershipKey,
                string? reason = null,
                CancellationToken cancellationToken = default) =>
                inner.RetainAsync(
                    ownershipId,
                    expectedOwnershipKey,
                    reason,
                    cancellationToken);

            public Task MarkRemovedAsync(
                long ownershipId,
                string expectedOwnershipKey,
                CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("Injected ownership-state persistence failure.");
        }

        private static void AssertNoListenarrArtifacts(string root)
        {
            Assert.DoesNotContain(
                Directory.EnumerateFileSystemEntries(
                    root,
                    "*",
                    SearchOption.AllDirectories),
                path =>
                {
                    var name = Path.GetFileName(path);
                    return name.StartsWith(".listenarr-", StringComparison.Ordinal)
                        || name.Contains(".listenarr-", StringComparison.Ordinal)
                        || name.Contains(".tmp-", StringComparison.Ordinal);
                });
        }

        private sealed class FailAfterPublishedOnce : IMoveFaultInjector
        {
            private int _failed;

            public Task AfterPublishedAsync(
                Guid jobId,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Interlocked.Exchange(ref _failed, 1) == 0)
                {
                    throw new IOException(
                        "Injected markerless interruption after publication.");
                }
                return Task.CompletedTask;
            }
        }

        private sealed class DisableMarkerlessFileRename : IMoveFaultInjector
        {
            public bool AllowMarkerlessFileRename => false;
        }

        private sealed class NativeRenameUnsupported(int nativeErrorCode)
            : IMoveFaultInjector
        {
            public bool AllowMarkerlessFileRename => true;
            public int? MarkerlessNativeRenameErrorForTest => nativeErrorCode;
        }

        private sealed class NativeRenameFirstThenUnsupported : IMoveFaultInjector
        {
            private int _attempt;

            public bool AllowMarkerlessFileRename => true;
            public int? MarkerlessNativeRenameErrorForTest =>
                Interlocked.Increment(ref _attempt) == 1 ? null : 22;
        }

        private sealed class NativeRenamePublishedBeforeError(int nativeErrorCode)
            : IMoveFaultInjector
        {
            public bool AllowMarkerlessFileRename => true;
            public int? MarkerlessNativeRenameErrorForTest => nativeErrorCode;
            public bool MarkerlessNativeRenamePublishesBeforeErrorForTest => true;
        }

        private sealed class CreateTargetAfterNativeRenameFailure(string targetFile)
            : IMoveFaultInjector
        {
            private int _created;

            public bool AllowMarkerlessFileRename => true;
            public int? MarkerlessNativeRenameErrorForTest => 22;

            public void OnCopyMutation(
                Guid jobId,
                CopyMutationFaultPoint faultPoint)
            {
                if (faultPoint
                        != CopyMutationFaultPoint.AfterMarkerlessNativeRenameFailureBeforeObservation
                    || Interlocked.Exchange(ref _created, 1) != 0)
                {
                    return;
                }

                File.WriteAllText(targetFile, "foreign");
            }
        }

        private sealed class ReplaceSourceAfterNativeRenameFallbackAuthorized(string sourceFile)
            : IMoveFaultInjector
        {
            private int _replaced;

            public bool AllowMarkerlessFileRename => true;
            public int? MarkerlessNativeRenameErrorForTest => 22;

            public void OnCopyMutation(
                Guid jobId,
                CopyMutationFaultPoint faultPoint)
            {
                if (faultPoint
                        != CopyMutationFaultPoint.AfterMarkerlessNativeRenameFallbackAuthorized
                    || Interlocked.Exchange(ref _replaced, 1) != 0)
                {
                    return;
                }

                File.Delete(sourceFile);
                File.WriteAllText(sourceFile, "replacement");
            }
        }

        private sealed class MutateBeforeMarkerlessNativeRename(
            string sourceFile,
            DateTime originalLastWriteTimeUtc) : IMoveFaultInjector
        {
            private int _mutated;

            public bool AllowMarkerlessFileRename => true;

            public void OnCopyMutation(
                Guid jobId,
                CopyMutationFaultPoint faultPoint)
            {
                if (faultPoint != CopyMutationFaultPoint.BeforeMarkerlessNativeRenameMutation
                    || Interlocked.Exchange(ref _mutated, 1) != 0)
                {
                    return;
                }

                File.WriteAllText(sourceFile, "other");
                File.SetLastWriteTimeUtc(sourceFile, originalLastWriteTimeUtc);
            }
        }

        private sealed class FailOnceAfterMarkerlessNativeRename : IMoveFaultInjector
        {
            private int _failed;

            public bool AllowMarkerlessFileRename => true;

            public void OnCopyMutation(
                Guid jobId,
                CopyMutationFaultPoint faultPoint)
            {
                if (faultPoint == CopyMutationFaultPoint
                        .AfterMarkerlessNativeRenameBeforeStateUpdate
                    && Interlocked.Exchange(ref _failed, 1) == 0)
                {
                    throw new IOException(
                        "Injected markerless native-rename interruption before state commit.");
                }
            }
        }

        private sealed class FailOnceAtCopyMutationPoint(
            CopyMutationFaultPoint expectedPoint) : IMoveFaultInjector
        {
            private int _failed;

            public void OnCopyMutation(
                Guid jobId,
                CopyMutationFaultPoint faultPoint)
            {
                if (faultPoint == expectedPoint
                    && Interlocked.Exchange(ref _failed, 1) == 0)
                {
                    throw new IOException(
                        $"Injected markerless target-file interruption at {faultPoint}.");
                }
            }
        }

        private sealed class FailMarkerlessMetadataPreservationOnce
            : IMoveFaultInjector
        {
            private int _triggered;

            public bool Triggered => Volatile.Read(ref _triggered) != 0;

            public void OnCopyMutation(
                Guid jobId,
                CopyMutationFaultPoint faultPoint)
            {
                if (faultPoint
                        == CopyMutationFaultPoint.BeforeMarkerlessMetadataPreservation
                    && Interlocked.Exchange(ref _triggered, 1) == 0)
                {
                    throw new IOException(
                        "Injected non-fatal markerless metadata preservation failure.");
                }
            }
        }

        private sealed class ReplaceMarkerlessTargetAfterWrite(
            string targetFile,
            DateTime replacementTimestamp) : IMoveFaultInjector
        {
            private int _replaced;

            public string DisplacedPath { get; } = targetFile + ".displaced";

            public bool Replaced => Volatile.Read(ref _replaced) != 0;

            public void OnCopyMutation(
                Guid jobId,
                CopyMutationFaultPoint faultPoint)
            {
                if (faultPoint != CopyMutationFaultPoint
                        .AfterMarkerlessFileWriteBeforePublishedState
                    || Interlocked.Exchange(ref _replaced, 1) != 0)
                {
                    return;
                }

                File.Move(targetFile, DisplacedPath);
                File.WriteAllText(targetFile, "replacement");
                File.SetLastWriteTimeUtc(targetFile, replacementTimestamp);
            }
        }

        private sealed class ForceCrossVolumeMoveFaultInjector : IMoveFaultInjector
        {
            public bool ForceCrossVolumeForTest => true;
        }

        private sealed class AlwaysCurrentMoveCleanupPolicyResolver
            : IMoveSourceCleanupPolicyResolver
        {
            public Task<MoveSourceCleanupAuthorization> ResolveAsync(
                string sourcePath,
                string targetPath,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<bool> IsCurrentAsync(
                MoveSourceCleanupAuthorization authorization,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(true);
        }

        private sealed class FailOnceDuringCrossVolumeRetention : IMoveFaultInjector
        {
            private int _failed;

            public bool ForceCrossVolumeForTest => true;

            public void OnSourceRetentionMutation(
                Guid jobId,
                SourceRetentionFaultPoint faultPoint)
            {
                if (faultPoint == SourceRetentionFaultPoint.AfterEntryStateUpdate
                    && Interlocked.Exchange(ref _failed, 1) == 0)
                {
                    throw new IOException(
                        "Injected interruption after source retention persistence.");
                }
            }
        }

        private sealed class FailOnceAtTargetScaffoldPreparationPoint(
            TargetScaffoldPreparationFaultPoint expectedPoint) : IMoveFaultInjector
        {
            private int _failed;

            public void OnTargetScaffoldPreparation(
                Guid jobId,
                TargetScaffoldPreparationFaultPoint faultPoint)
            {
                if (faultPoint == expectedPoint
                    && Interlocked.Exchange(ref _failed, 1) == 0)
                {
                    throw new IOException(
                        $"Injected markerless target-directory interruption at {faultPoint}.");
                }
            }
        }

        private sealed class MutateFileAfterDeleteAuthorization(
            string path,
            string content) : IMoveFaultInjector
        {
            private int _mutated;

            public void OnSourceCleanupMutation(
                Guid jobId,
                SourceCleanupFaultPoint faultPoint)
            {
                if (faultPoint !=
                        SourceCleanupFaultPoint.AfterMarkerlessSourceDeleteAuthorizedState
                    || Interlocked.Exchange(ref _mutated, 1) != 0)
                {
                    return;
                }

                File.WriteAllText(path, content);
            }
        }

        private sealed class FailOnceAtSourceCleanupPoint(
            SourceCleanupFaultPoint expectedPoint) : IMoveFaultInjector
        {
            private int _failed;

            public void OnSourceCleanupMutation(
                Guid jobId,
                SourceCleanupFaultPoint faultPoint)
            {
                if (faultPoint == expectedPoint
                    && Interlocked.Exchange(ref _failed, 1) == 0)
                {
                    throw new IOException(
                        $"Injected markerless source-cleanup interruption at {faultPoint}.");
                }
            }
        }

        private sealed class AddSourceFileAfterPublish(string source) : IMoveFaultInjector
        {
            public Task AfterPublishedAsync(Guid jobId, CancellationToken cancellationToken) =>
                File.WriteAllTextAsync(
                    Path.Join(source, "arrived-late.txt"),
                    "new content",
                    cancellationToken);
        }

        private sealed class AddTargetFileAfterPublish(string target) : IMoveFaultInjector
        {
            public Task AfterPublishedAsync(Guid jobId, CancellationToken cancellationToken) =>
                File.WriteAllTextAsync(
                    Path.Join(target, "arrived-late.txt"),
                    "new target content",
                    cancellationToken);
        }

        private sealed class ReplaceTargetDirectoryAfterPublish(string target) : IMoveFaultInjector
        {
            public Task AfterPublishedAsync(Guid jobId, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Move(target, target + ".original");
                Directory.CreateDirectory(target);
                return Task.CompletedTask;
            }
        }

        private sealed class AddFileBeforeSourceAncestorDelete(string directory) : IMoveFaultInjector
        {
            private bool _added;

            public void OnMoveFinalization(Guid jobId, MoveFinalizationFaultPoint faultPoint)
            {
                if (!_added && faultPoint == MoveFinalizationFaultPoint.BeforeSourceAncestorDelete)
                {
                    File.WriteAllText(Path.Join(directory, "arrived-late.txt"), "late content");
                    _added = true;
                }
            }
        }
    }
}
