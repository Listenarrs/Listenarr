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
        public async Task MoveContentsAsync_UnmarkedJobShapedTempDirectory_IsPreservedAndRequiresAttention()
        {
            var source = FileService.GetTempDirectory("content-move-partial-src");
            var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "complete audio");
            var target = Path.Join(FileService.GetTempPath(), $"content-move-partial-dst-{Guid.NewGuid():N}");
            var jobId = Guid.NewGuid();
            var targetParent = Path.GetDirectoryName(target)!;
            var tempName = Path.Join(targetParent, Path.GetFileName(target) + ".tmp-" + jobId.ToString("N"));
            Directory.CreateDirectory(tempName);
            var unrelatedFile = Path.Join(tempName, "book.m4b");
            await File.WriteAllTextAsync(unrelatedFile, "unrelated bytes");

            var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.Contains("ownership marker", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(sourceFile));
            Assert.Equal("complete audio", await File.ReadAllTextAsync(sourceFile));
            Assert.True(Directory.Exists(tempName));
            Assert.Equal("unrelated bytes", await File.ReadAllTextAsync(unrelatedFile));
            Assert.False(Directory.Exists(target));
        }

        [Fact]
        public async Task MoveContentsAsync_DirectCopyMarkerWithoutManifest_BlocksRecovery()
        {
            var source = FileService.GetTempDirectory("content-move-direct-retry-src");
            await FileService.GetFileAsync(source, "book.m4b", "complete audio");
            var target = FileService.GetTempDirectory("content-move-direct-retry-dst");
            await FileService.GetFileAsync(target, "book.m4b", "partial");
            var jobId = Guid.NewGuid();
            await WriteRecoveryMarkerAsync(
                target,
                jobId,
                source,
                target,
                "copy-started");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
            await ClearPersistedManifestAsync(jobId);
            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.Contains("without a persisted tracked-file manifest", exception.Message);
            Assert.True(Directory.Exists(source));
            Assert.Equal("partial", await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
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

            Assert.Contains("unrelated content", ex.Message);
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
            Assert.True(File.Exists(result.RecoveryMarkerPath));
            await service.CleanupCompletedMoveArtifactsAsync(request, result, CancellationToken.None);
            Assert.False(File.Exists(result.RecoveryMarkerPath));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task MoveContents_SourceAtManagedRoot_PreservesRootEnrollmentMarker()
        {
            var source = FileService.GetTempDirectory("content-move-managed-root-source");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var enrollment = await new DirectoryObjectIdentityResolver().ResolveAsync(source);
            Assert.True(enrollment.IsAvailable, enrollment.UnavailableReason);
            var enrollmentMarker = Path.Join(source, ManagedDirectoryEnrollment.FileName);
            var originalEnrollment = await File.ReadAllTextAsync(enrollmentMarker);
            var target = Path.Join(
                FileService.GetTempPath(),
                $"content-move-managed-root-target-{Guid.NewGuid():N}");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = await CreateLeasedMoveRequestAsync(
                source,
                target,
                sourceCleanupBoundary: source);
            var result = await service.MoveContentsAsync(request, CancellationToken.None);

            Assert.True(File.Exists(enrollmentMarker));
            Assert.Equal(originalEnrollment, await File.ReadAllTextAsync(enrollmentMarker));
            Assert.False(File.Exists(Path.Join(target, ManagedDirectoryEnrollment.FileName)));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));

            await service.FinalizeMoveAsync(request, result, CancellationToken.None);
            await service.CleanupCompletedMoveArtifactsAsync(
                request,
                result,
                CancellationToken.None);

            Assert.True(File.Exists(enrollmentMarker));
            Assert.Equal(originalEnrollment, await File.ReadAllTextAsync(enrollmentMarker));
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
            Assert.Single(Directory.EnumerateFileSystemEntries(oldTitle));
            Assert.True(File.Exists(Path.Join(oldTitle, LibraryDirectoryOwnershipMarker.FileName)));
            Assert.True(File.Exists(result.RecoveryMarkerPath));

            await service.FinalizeMoveAsync(request, result, CancellationToken.None);

            Assert.False(Directory.Exists(oldTitle));
            Assert.True(File.Exists(result.RecoveryMarkerPath));
            await service.CleanupCompletedMoveArtifactsAsync(request, result, CancellationToken.None);
            Assert.False(File.Exists(result.RecoveryMarkerPath));
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

            Assert.True(File.Exists(result.RecoveryMarkerPath));
            Assert.True(Directory.Exists(oldTitle));
            Assert.False(Directory.Exists(source));
            await service.CleanupCompletedMoveArtifactsAsync(request, result, CancellationToken.None);
            Assert.False(File.Exists(result.RecoveryMarkerPath));
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
            LibraryDirectoryOwnershipMarker.DeleteInsideMarker(ownership, oldTitle);
            Directory.Delete(oldTitle, false);
            Assert.True(Directory.Exists(author));
            Assert.False(Directory.Exists(oldTitle));

            await service.FinalizeMoveAsync(request, result, CancellationToken.None);

            Assert.False(Directory.Exists(author));
            Assert.True(Directory.Exists(sourceRoot));
            Assert.True(File.Exists(result.RecoveryMarkerPath));
            await service.CleanupCompletedMoveArtifactsAsync(request, result, CancellationToken.None);
            Assert.False(File.Exists(result.RecoveryMarkerPath));
        }

        [Fact]
        public async Task FinalizeMove_LiveRemovingPathWithoutInsideMarkerRequiresAttention()
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
            LibraryDirectoryOwnershipMarker.DeleteInsideMarker(ownership, oldTitle);
            Assert.True(Directory.Exists(oldTitle));
            Assert.Empty(Directory.EnumerateFileSystemEntries(oldTitle));

            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.FinalizeMoveAsync(request, result, CancellationToken.None));

            Assert.Contains("could not be proven safe", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(oldTitle));
            Assert.Empty(Directory.EnumerateFileSystemEntries(oldTitle));
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var persisted = await db.LibraryDirectoryOwnerships.SingleAsync(
                candidate => candidate.Id == ownership.Id);
            Assert.Equal(LibraryDirectoryOwnershipState.Removing, persisted.State);
            Assert.Equal(ownershipKey, persisted.PathOwnershipKey);
        }

        [Fact]
        public async Task FinalizeMove_RetryAfterOwnedParentQuarantined_ResumesDeletion()
        {
            var sourceRoot = FileService.GetTempDirectory("content-move-finalize-quarantine-retry-root");
            var oldTitle = Path.Join(sourceRoot, "Author", "Old Title");
            var source = Path.Join(oldTitle, "test");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            await ClaimOwnedDirectoriesAsync(oldTitle);
            var target = Path.Join(
                FileService.GetTempPath(),
                $"content-move-finalize-quarantine-retry-dst-{Guid.NewGuid():N}");

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
            var quarantinePath = LibraryDirectoryOwnershipRemoval.GetQuarantinePath(ownership);
            Directory.Move(oldTitle, quarantinePath);

            await service.FinalizeMoveAsync(request, result, CancellationToken.None);

            Assert.False(Directory.Exists(oldTitle));
            Assert.False(Directory.Exists(quarantinePath));
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var persisted = await db.LibraryDirectoryOwnerships.SingleAsync(
                candidate => candidate.Id == ownership.Id);
            Assert.Equal(LibraryDirectoryOwnershipState.Removed, persisted.State);
            Assert.Null(persisted.PathOwnershipKey);
        }

        [Fact]
        public async Task FinalizeMove_RecreatedOriginalBesideQuarantineRequiresAttention()
        {
            var sourceRoot = FileService.GetTempDirectory("content-move-finalize-quarantine-recreated-root");
            var oldTitle = Path.Join(sourceRoot, "Author", "Old Title");
            var source = Path.Join(oldTitle, "test");
            Directory.CreateDirectory(source);
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            await ClaimOwnedDirectoriesAsync(oldTitle);
            var target = Path.Join(
                FileService.GetTempPath(),
                $"content-move-finalize-quarantine-recreated-dst-{Guid.NewGuid():N}");

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
            var quarantinePath = LibraryDirectoryOwnershipRemoval.GetQuarantinePath(ownership);
            Directory.Move(oldTitle, quarantinePath);
            Directory.CreateDirectory(oldTitle);
            await File.WriteAllTextAsync(Path.Join(oldTitle, "user-content.txt"), "keep");

            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.FinalizeMoveAsync(request, result, CancellationToken.None));

            Assert.Contains("both the owned directory", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Join(oldTitle, "user-content.txt")));
            Assert.True(Directory.Exists(quarantinePath));
        }

        [Fact]
        public async Task FinalizeMove_MarkRemovedFailure_PreservesSiblingProofForRetry()
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
            var siblingMarker = LibraryDirectoryOwnershipMarker.GetMarkerPaths(ownership)
                .Single(path => !FileSystemPathIdentity.IsSameOrInside(
                    path,
                    oldTitle,
                    FileSystemPathSemantics.CurrentHostDefault));
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            var failingService = new AudiobookContentMoveService(
                NullLogger<AudiobookContentMoveService>.Instance,
                factory,
                TimeProvider.System,
                directoryOwnershipStore: new FailingMarkRemovedOwnershipStore(ownershipStore));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                failingService.FinalizeMoveAsync(request, result, CancellationToken.None));

            Assert.False(Directory.Exists(oldTitle));
            Assert.True(File.Exists(siblingMarker));
            await using (var failedDb = await factory.CreateDbContextAsync())
            {
                var interrupted = await failedDb.LibraryDirectoryOwnerships.AsNoTracking()
                    .SingleAsync(candidate => candidate.Id == ownership.Id);
                Assert.Equal(LibraryDirectoryOwnershipState.Removing, interrupted.State);
                Assert.Equal(ownershipKey, interrupted.PathOwnershipKey);
            }

            await normalService.FinalizeMoveAsync(request, result, CancellationToken.None);

            Assert.False(File.Exists(siblingMarker));
            await using var recoveredDb = await factory.CreateDbContextAsync();
            var recovered = await recoveredDb.LibraryDirectoryOwnerships.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == ownership.Id);
            Assert.Equal(LibraryDirectoryOwnershipState.Removed, recovered.State);
            Assert.Null(recovered.PathOwnershipKey);
        }

        [Fact]
        public async Task FinalizeMove_LiveRemovingPathWithNewContentRequiresAttention()
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
            LibraryDirectoryOwnershipMarker.DeleteInsideMarker(ownership, oldTitle);
            await File.WriteAllTextAsync(Path.Join(oldTitle, "arrived-late.txt"), "keep");

            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.FinalizeMoveAsync(request, result, CancellationToken.None));

            Assert.Contains("could not be proven safe", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(oldTitle));
            Assert.True(File.Exists(Path.Join(oldTitle, "arrived-late.txt")));
            var interrupted = await ownershipStore.ResolveOwnedAsync(
                oldTitle,
                FileSystemPathSemantics.CurrentHostDefault);
            Assert.Equal(LibraryDirectoryOwnershipState.Removing, interrupted.Ownership?.State);
            Assert.Equal(ownershipKey, interrupted.Ownership?.PathOwnershipKey);
        }

        [Fact]
        public async Task FinalizeMove_MissingRemovedDirectoryWithoutSiblingProofRequiresAttention()
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
            foreach (var markerPath in LibraryDirectoryOwnershipMarker.GetMarkerPaths(ownership))
            {
                File.Delete(markerPath);
            }
            Directory.Delete(oldTitle, false);

            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.FinalizeMoveAsync(request, result, CancellationToken.None));

            Assert.Contains("sibling ownership proof", exception.Message, StringComparison.OrdinalIgnoreCase);
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var persisted = await db.LibraryDirectoryOwnerships.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == ownership.Id);
            Assert.Equal(LibraryDirectoryOwnershipState.Removing, persisted.State);
            Assert.Equal(ownershipKey, persisted.PathOwnershipKey);
        }

        [Fact]
        public async Task FinalizeMove_OwnershipMarkerMissing_PreservesDirectoryAndRequiresAttention()
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
            File.Delete(Path.Join(oldTitle, LibraryDirectoryOwnershipMarker.FileName));

            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.FinalizeMoveAsync(request, result, CancellationToken.None));

            Assert.Contains("ownership proof is unavailable", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(oldTitle));
            Assert.True(File.Exists(result.RecoveryMarkerPath));
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

            Assert.True(File.Exists(result.RecoveryMarkerPath));
            await service.CleanupCompletedMoveArtifactsAsync(request, result, CancellationToken.None);
            Assert.False(File.Exists(result.RecoveryMarkerPath));
            Assert.True(Directory.Exists(sourceParent));
            Assert.True(File.Exists(Path.Join(sourceParent, "keep.txt")));
        }

        [Fact]
        public async Task MoveContentsAsync_RetryAfterEmptySourceQuarantineCompletesSafely()
        {
            var source = FileService.GetTempDirectory("content-move-source-root-quarantine");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"content-move-source-root-quarantine-dst-{Guid.NewGuid():N}");
            var request = await CreateLeasedMoveRequestAsync(source, target);
            var failingService = new AudiobookContentMoveService(
                _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new InterruptAfterEmptySourceQuarantine(source, recreateSource: false));

            await Assert.ThrowsAsync<IOException>(() => failingService.MoveContentsAsync(
                request,
                CancellationToken.None));

            var quarantinedSource = Path.Join(
                Path.GetDirectoryName(source)!,
                $".listenarr-quarantine-{request.JobId:N}",
                ".listenarr-empty-source.state",
                "source.claim");
            Assert.False(Directory.Exists(source));
            Assert.True(Directory.Exists(quarantinedSource));

            var recoveryService = _provider.GetRequiredService<AudiobookContentMoveService>();
            var recovered = Assert.IsType<AudiobookContentMoveResult>(
                await recoveryService.GetRecoverableMoveAsync(
                    request,
                    CancellationToken.None));
            await recoveryService.ResumeSourceCleanupAsync(
                request,
                recovered,
                CancellationToken.None);

            Assert.False(Directory.Exists(source));
            Assert.False(Directory.Exists(quarantinedSource));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task MoveContentsAsync_RetryWithRestoredSourceAndEmptyCleanupState_CompletesSafely()
        {
            var source = FileService.GetTempDirectory("content-move-source-restored-state-retry");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"content-move-source-restored-state-retry-dst-{Guid.NewGuid():N}");
            var request = await CreateLeasedMoveRequestAsync(source, target);
            var failingService = new AudiobookContentMoveService(
                _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new InterruptAfterEmptySourceQuarantine(source, recreateSource: false));

            await Assert.ThrowsAsync<IOException>(() => failingService.MoveContentsAsync(
                request,
                CancellationToken.None));

            var statePath = Path.Join(
                Path.GetDirectoryName(source)!,
                $".listenarr-quarantine-{request.JobId:N}",
                ".listenarr-empty-source.state");
            var claimPath = Path.Join(statePath, "source.claim");
            Assert.False(Directory.Exists(source));
            Assert.True(Directory.Exists(claimPath));
            Assert.Empty(Directory.EnumerateFileSystemEntries(claimPath));
            Directory.Move(claimPath, source);
            Assert.True(Directory.Exists(source));
            Assert.Empty(Directory.EnumerateFileSystemEntries(source));
            Assert.Empty(Directory.EnumerateFileSystemEntries(statePath));

            var recoveryService = _provider.GetRequiredService<AudiobookContentMoveService>();
            var recovered = Assert.IsType<AudiobookContentMoveResult>(
                await recoveryService.GetRecoverableMoveAsync(
                    request,
                    CancellationToken.None));
            await recoveryService.ResumeSourceCleanupAsync(
                request,
                recovered,
                CancellationToken.None);

            Assert.False(Directory.Exists(source));
            Assert.False(Directory.Exists(statePath));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task MoveContentsAsync_EmptySourceStateNativeDeleteFailure_RemainsRecoverable()
        {
            var source = FileService.GetTempDirectory("content-move-source-state-delete-retry");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"content-move-source-state-delete-retry-dst-{Guid.NewGuid():N}");
            var request = await CreateLeasedMoveRequestAsync(source, target);
            var failingService = new AudiobookContentMoveService(
                _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new FailEmptySourceStateDeleteOnce());

            var exception = await Assert.ThrowsAsync<IOException>(() => failingService.MoveContentsAsync(
                request,
                CancellationToken.None));

            Assert.IsType<System.ComponentModel.Win32Exception>(exception.InnerException);
            var statePath = Path.Join(
                Path.GetDirectoryName(source)!,
                $".listenarr-quarantine-{request.JobId:N}",
                ".listenarr-empty-source.state");
            Assert.False(Directory.Exists(source));
            Assert.True(Directory.Exists(statePath));
            Assert.Empty(Directory.EnumerateFileSystemEntries(statePath));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));

            var recoveryService = _provider.GetRequiredService<AudiobookContentMoveService>();
            var recovered = Assert.IsType<AudiobookContentMoveResult>(
                await recoveryService.GetRecoverableMoveAsync(
                    request,
                    CancellationToken.None));
            await recoveryService.ResumeSourceCleanupAsync(
                request,
                recovered,
                CancellationToken.None);

            Assert.False(Directory.Exists(source));
            Assert.False(Directory.Exists(statePath));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task MoveContentsAsync_RecreatedSourceDuringQuarantineIsPreserved()
        {
            var source = FileService.GetTempDirectory("content-move-recreated-source-root");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"content-move-recreated-source-root-dst-{Guid.NewGuid():N}");
            var request = await CreateLeasedMoveRequestAsync(source, target);
            var failingService = new AudiobookContentMoveService(
                _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new InterruptAfterEmptySourceQuarantine(source, recreateSource: true));

            await Assert.ThrowsAsync<IOException>(() => failingService.MoveContentsAsync(
                request,
                CancellationToken.None));

            var quarantinedSource = Path.Join(
                Path.GetDirectoryName(source)!,
                $".listenarr-quarantine-{request.JobId:N}",
                ".listenarr-empty-source.state",
                "source.claim");
            Assert.True(Directory.Exists(source));
            Assert.True(Directory.Exists(quarantinedSource));

            var recoveryService = _provider.GetRequiredService<AudiobookContentMoveService>();
            var recovered = Assert.IsType<AudiobookContentMoveResult>(
                await recoveryService.GetRecoverableMoveAsync(
                    request,
                    CancellationToken.None));
            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                recoveryService.ResumeSourceCleanupAsync(
                    request,
                    recovered,
                    CancellationToken.None));

            Assert.Contains("both", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(source));
            Assert.True(Directory.Exists(quarantinedSource));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
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

            Assert.Contains("contains files", ex.Message);
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

            Assert.Contains("contains files", ex.Message);
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
        public async Task MoveContentsAsync_OwnedSourceMarkersAreRetiredAndNeverPublished()
        {
            var source = FileService.GetTempDirectory("content-move-owned-source");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"content-move-owned-source-dst-{Guid.NewGuid():N}");
            var ownershipStore = _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>();
            var ownership = await ownershipStore.RecordCreatedAsync(
                new LibraryDirectoryOwnershipClaim(
                    source,
                    FileSystemPathSemantics.CurrentHostDefault,
                    "test"));
            var service = new AudiobookContentMoveService(
                _provider.GetRequiredService<ILogger<AudiobookContentMoveService>>(),
                _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>(),
                TimeProvider.System,
                new AllowAtomicRenameInjector(),
                directoryOwnershipStore: ownershipStore);
            var request = await CreateLeasedMoveRequestAsync(source, target);

            await service.MoveContentsAsync(request, CancellationToken.None);

            Assert.False(Directory.Exists(source));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
            Assert.False(File.Exists(Path.Join(target, LibraryDirectoryOwnershipMarker.FileName)));
            Assert.Empty(Directory.EnumerateFiles(
                Path.GetDirectoryName(target)!,
                $".listenarr-directory-owner-{ownership.OwnershipToken}.json",
                SearchOption.TopDirectoryOnly));
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
            var ownership = await ownershipStore.RecordCreatedAsync(
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
            Assert.True(File.Exists(Path.Join(target, LibraryDirectoryOwnershipMarker.FileName)));
            LibraryDirectoryOwnershipMarker.Validate(ownership, target);
            var resolution = await ownershipStore.ResolveOwnedAsync(
                target,
                FileSystemPathSemantics.CurrentHostDefault);
            Assert.Equal(LibraryDirectoryOwnershipResolutionState.Owned, resolution.State);
        }

        [Fact]
        public async Task OwnedTargetMarkerChangedAfterPublication_BlocksSourceCleanup()
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
                new TamperTargetOwnershipAfterPublish(target),
                directoryOwnershipStore: ownershipStore);
            var request = await CreateLeasedMoveRequestAsync(source, target);

            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.Contains("ownership marker changed", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Join(source, "book.m4b")));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task MoveContentsAsync_UnclaimedDirectoryOwnershipMarkerBlocksMove()
        {
            var source = FileService.GetTempDirectory("content-move-unclaimed-owner-marker");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            await File.WriteAllTextAsync(
                Path.Join(source, LibraryDirectoryOwnershipMarker.FileName),
                "foreign marker");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"content-move-unclaimed-owner-marker-dst-{Guid.NewGuid():N}");
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = await CreateLeasedMoveRequestAsync(source, target);

            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.Contains("reserved Listenarr recovery artifact", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Join(source, "book.m4b")));
            Assert.False(Directory.Exists(target));
        }

        [Fact]
        public async Task LegacyCopyCompleteMarker_WithoutManifest_NeverAuthorizesDeletion()
        {
            var source = FileService.GetTempDirectory("content-move-legacy-marker-src");
            await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = FileService.GetTempDirectory("content-move-legacy-marker-dst");
            await FileService.GetFileAsync(target, "book.m4b", "audio");
            var jobId = Guid.NewGuid();
            var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
            await File.WriteAllTextAsync(
                Path.Join(target, $".listenarr-move-{jobId:N}.pending"),
                "copy-complete");
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();

            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.GetRecoverableMoveAsync(request));

            Assert.Contains("obsolete pre-release", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Join(source, "book.m4b")));
            Assert.True(File.Exists(Path.Join(target, $".listenarr-move-{jobId:N}.pending")));
        }

        [Fact]
        public async Task AtomicRenameMarker_RecoversBeforePhasePersistence()
        {
            var source = FileService.GetTempDirectory("content-move-atomic-recovery-src");
            var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"content-move-atomic-recovery-dst-{Guid.NewGuid():N}");
            var jobId = Guid.NewGuid();
            var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
            await PersistFileManifestAsync(jobId, "book.m4b", sourceFile);
            await File.WriteAllTextAsync(
                Path.Join(source, $".listenarr-move-{jobId:N}.pending"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    Version = 1,
                    JobId = jobId,
                    Source = Path.GetFullPath(source),
                    Target = Path.GetFullPath(target),
                    Stage = "atomic-rename-complete"
                }));
            Directory.Move(source, target);

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var result = await service.GetRecoverableMoveAsync(request);

            Assert.NotNull(result);
            Assert.True(result.SourceCleanupCompleted);
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
        }

        [Fact]
        public async Task GetRecoverableMoveAsync_PropagatesCancellationDuringManifestVerification()
        {
            var source = FileService.GetTempDirectory("content-move-cancel-recovery-src");
            var target = FileService.GetTempDirectory("content-move-cancel-recovery-dst");
            var destination = await FileService.GetFileAsync(target, "book.m4b", "audio");
            var jobId = Guid.NewGuid();
            await WriteRecoveryMarkerAsync(
                target,
                jobId,
                source,
                target,
                "copy-complete");
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(destination)));
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
                    LeaseOwner = TestLeaseOwner,
                    LeaseGeneration = 1,
                    LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
                    ActiveDeduplicationKey = $"test:{jobId:N}"
                });
                db.MoveJobEntries.Add(new MoveJobEntry
                {
                    MoveJobId = jobId,
                    RelativePath = "book.m4b",
                    EntryType = MoveJobEntryType.File,
                    Length = new FileInfo(destination).Length,
                    Sha256 = hash,
                    CopyState = MoveJobEntryCopyState.Verified
                });
                await db.SaveChangesAsync();
            }

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.GetRecoverableMoveAsync(
                    new AudiobookContentMoveRequest(
                        source,
                        target,
                        jobId,
                        true,
                        FileSystemPathSemantics.CurrentHostDefault,
                        FileSystemPathSemantics.CurrentHostDefault,
                        LeaseToken(1)),
                    cancellation.Token));
        }

        [Fact]
        public async Task ResumeSourceCleanup_VerifiedQuarantine_ConvergesAfterCrash()
        {
            var source = FileService.GetTempDirectory("content-move-quarantine-src");
            var target = FileService.GetTempDirectory("content-move-quarantine-dst");
            var jobId = Guid.NewGuid();
            var quarantineRoot = Path.Join(
                Path.GetDirectoryName(source)!,
                $".listenarr-quarantine-{jobId:N}");
            Directory.CreateDirectory(quarantineRoot);
            await WriteQuarantineOwnershipMarkerAsync(
                quarantineRoot,
                jobId,
                source,
                target);
            var destination = Path.Join(target, "book.m4b");
            var quarantineFile = Path.Join(quarantineRoot, "book.m4b");
            await File.WriteAllTextAsync(destination, "verified audio");
            await File.WriteAllTextAsync(quarantineFile, "verified audio");
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(destination)));
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
                    LeaseOwner = TestLeaseOwner,
                    LeaseGeneration = 1,
                    LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
                    ActiveDeduplicationKey = $"test:{jobId:N}"
                });
                db.MoveJobEntries.Add(new MoveJobEntry
                {
                    MoveJobId = jobId,
                    RelativePath = "book.m4b",
                    EntryType = MoveJobEntryType.File,
                    Length = new FileInfo(destination).Length,
                    Sha256 = hash,
                    CopyState = MoveJobEntryCopyState.Verified,
                    CleanupState = MoveJobEntryCleanupState.Quarantined
                });
                await db.SaveChangesAsync();
            }

            await AuthorizeExistingMoveJobTargetAsync(jobId, target);
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var resumed = await service.ResumeSourceCleanupAsync(
                new AudiobookContentMoveRequest(
                    source,
                    target,
                    jobId,
                    true,
                    FileSystemPathSemantics.CurrentHostDefault,
                    FileSystemPathSemantics.CurrentHostDefault,
                    LeaseToken(1)),
                new AudiobookContentMoveResult(
                    source,
                    target,
                    false,
                    false,
                    Path.Join(target, $".listenarr-move-{jobId:N}.pending"),
                    false,
                    new Dictionary<string, string>()),
                CancellationToken.None);

            Assert.True(resumed.SourceCleanupCompleted);
            Assert.False(File.Exists(quarantineFile));
            Assert.False(Directory.Exists(quarantineRoot));
            Assert.False(Directory.Exists(source));
            await using var verification = await factory.CreateDbContextAsync();
            var persistedEntries = await verification.MoveJobEntries.ToListAsync();
            Assert.Equal(
                MoveJobEntryCleanupState.Deleted,
                persistedEntries.Single(entry =>
                    !MoveManifestIdentity.IsTargetBoundaryAuthorization(entry)).CleanupState);
        }

        [Fact]
        public async Task ResumeSourceCleanup_DeletedQuarantine_ConvergesAfterCrash()
        {
            var source = FileService.GetTempDirectory("content-move-deleted-quarantine-src");
            var target = FileService.GetTempDirectory("content-move-deleted-quarantine-dst");
            var jobId = Guid.NewGuid();
            var destination = Path.Join(target, "book.m4b");
            await File.WriteAllTextAsync(destination, "verified audio");
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(destination)));
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
                    LeaseOwner = TestLeaseOwner,
                    LeaseGeneration = 1,
                    LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
                    ActiveDeduplicationKey = $"test:{jobId:N}"
                });
                db.MoveJobEntries.Add(new MoveJobEntry
                {
                    MoveJobId = jobId,
                    RelativePath = "book.m4b",
                    EntryType = MoveJobEntryType.File,
                    Length = new FileInfo(destination).Length,
                    Sha256 = hash,
                    CopyState = MoveJobEntryCopyState.Verified,
                    CleanupState = MoveJobEntryCleanupState.Quarantined
                });
                await db.SaveChangesAsync();
            }

            await AuthorizeExistingMoveJobTargetAsync(jobId, target);
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var resumed = await service.ResumeSourceCleanupAsync(
                new AudiobookContentMoveRequest(
                    source,
                    target,
                    jobId,
                    true,
                    FileSystemPathSemantics.CurrentHostDefault,
                    FileSystemPathSemantics.CurrentHostDefault,
                    LeaseToken(1)),
                new AudiobookContentMoveResult(
                    source,
                    target,
                    false,
                    false,
                    Path.Join(target, $".listenarr-move-{jobId:N}.pending"),
                    false,
                    new Dictionary<string, string>()),
                CancellationToken.None);

            Assert.True(resumed.SourceCleanupCompleted);
            Assert.False(Directory.Exists(source));
            await using var verification = await factory.CreateDbContextAsync();
            var persistedEntries = await verification.MoveJobEntries.ToListAsync();
            Assert.Equal(
                MoveJobEntryCleanupState.Deleted,
                persistedEntries.Single(entry =>
                    !MoveManifestIdentity.IsTargetBoundaryAuthorization(entry)).CleanupState);
        }

        [Fact]
        public async Task ResumeSourceCleanup_SourceAndQuarantineBothExist_BlocksCleanup()
        {
            var source = FileService.GetTempDirectory("content-move-ambiguous-quarantine-src");
            var target = FileService.GetTempDirectory("content-move-ambiguous-quarantine-dst");
            var jobId = Guid.NewGuid();
            var quarantineRoot = Path.Join(
                Path.GetDirectoryName(source)!,
                $".listenarr-quarantine-{jobId:N}");
            Directory.CreateDirectory(quarantineRoot);
            await WriteQuarantineOwnershipMarkerAsync(
                quarantineRoot,
                jobId,
                source,
                target);
            var sourceFile = Path.Join(source, "book.m4b");
            var destination = Path.Join(target, "book.m4b");
            var quarantineFile = Path.Join(quarantineRoot, "book.m4b");
            await File.WriteAllTextAsync(sourceFile, "verified audio");
            await File.WriteAllTextAsync(destination, "verified audio");
            await File.WriteAllTextAsync(quarantineFile, "verified audio");
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(destination)));
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
                    LeaseOwner = TestLeaseOwner,
                    LeaseGeneration = 1,
                    LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
                    ActiveDeduplicationKey = $"test:{jobId:N}"
                });
                db.MoveJobEntries.Add(new MoveJobEntry
                {
                    MoveJobId = jobId,
                    RelativePath = "book.m4b",
                    EntryType = MoveJobEntryType.File,
                    Length = new FileInfo(destination).Length,
                    Sha256 = hash,
                    CopyState = MoveJobEntryCopyState.Verified,
                    CleanupState = MoveJobEntryCleanupState.Quarantined
                });
                await db.SaveChangesAsync();
            }

            await AuthorizeExistingMoveJobTargetAsync(jobId, target);
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            await Assert.ThrowsAsync<MoveNeedsAttentionException>(() => service.ResumeSourceCleanupAsync(
                new AudiobookContentMoveRequest(
                    source,
                    target,
                    jobId,
                    true,
                    FileSystemPathSemantics.CurrentHostDefault,
                    FileSystemPathSemantics.CurrentHostDefault,
                    LeaseToken(1)),
                new AudiobookContentMoveResult(
                    source,
                    target,
                    false,
                    false,
                    Path.Join(target, $".listenarr-move-{jobId:N}.pending"),
                    false,
                    new Dictionary<string, string>()),
                CancellationToken.None));

            Assert.True(File.Exists(sourceFile));
            Assert.True(File.Exists(quarantineFile));
        }

        [Fact]
        public async Task MoveContentsAsync_CopyStartedMarkerOwnedByAnotherJob_BlocksRecovery()
        {
            var source = FileService.GetTempDirectory("content-move-wrong-marker-src");
            var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "verified audio");
            var target = FileService.GetTempDirectory("content-move-wrong-marker-dst");
            var jobId = Guid.NewGuid();
            await CreateLeasedMoveRequestAsync(source, target, jobId);
            await PersistFileManifestAsync(jobId, "book.m4b", sourceFile);
            await File.WriteAllTextAsync(
                Path.Join(target, $".listenarr-move-{jobId:N}.pending"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    Version = 1,
                    JobId = Guid.NewGuid(),
                    Source = source,
                    Target = target,
                    Stage = "copy-started"
                }));

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var request = new AudiobookContentMoveRequest(
                source,
                target,
                jobId,
                true,
                FileSystemPathSemantics.CurrentHostDefault,
                FileSystemPathSemantics.CurrentHostDefault,
                LeaseToken(1));

            await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.True(File.Exists(sourceFile));
        }

        [Fact]
        public async Task MoveContentsAsync_CopyStartedWithUnknownDestinationFile_BlocksRecovery()
        {
            var source = FileService.GetTempDirectory("content-move-unowned-target-src");
            var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "verified audio");
            var target = FileService.GetTempDirectory("content-move-unowned-target-dst");
            await FileService.GetFileAsync(target, "unrelated.txt", "not owned");
            var jobId = Guid.NewGuid();
            var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
            await PersistFileManifestAsync(jobId, "book.m4b", sourceFile);
            await WriteRecoveryMarkerAsync(
                target,
                jobId,
                source,
                target,
                "copy-started");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.Contains("unowned file", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(sourceFile));
            Assert.True(File.Exists(Path.Join(target, "unrelated.txt")));
        }

        [Fact]
        public async Task MoveContentsAsync_ValidOwnedPartial_PublishesFromPersistedManifest()
        {
            var source = FileService.GetTempDirectory("content-move-valid-partial-src");
            var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "verified audio");
            var target = FileService.GetTempDirectory("content-move-valid-partial-dst");
            var jobId = Guid.NewGuid();
            var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
            await PersistFileManifestAsync(jobId, "book.m4b", sourceFile);
            var partial = Path.Join(target, $"book.m4b.listenarr-{jobId:N}.partial");
            await File.WriteAllTextAsync(partial, "verified audio");
            await WriteRecoveryMarkerAsync(
                target,
                jobId,
                source,
                target,
                "copy-started");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            await service.MoveContentsAsync(request, CancellationToken.None);

            Assert.False(File.Exists(partial));
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
            Assert.Equal("verified audio", await File.ReadAllTextAsync(Path.Join(target, "book.m4b")));
            Assert.False(Directory.Exists(source));
        }

        [Fact]
        public async Task MoveContentsAsync_InvalidOwnedPartial_IsPreservedAndRequiresAttention()
        {
            var source = FileService.GetTempDirectory("content-move-invalid-partial-src");
            var sourceFile = await FileService.GetFileAsync(source, "book.m4b", "verified audio");
            var target = FileService.GetTempDirectory("content-move-invalid-partial-dst");
            var jobId = Guid.NewGuid();
            var request = await CreateLeasedMoveRequestAsync(source, target, jobId);
            await PersistFileManifestAsync(jobId, "book.m4b", sourceFile);
            var partial = Path.Join(target, $"book.m4b.listenarr-{jobId:N}.partial");
            await File.WriteAllTextAsync(partial, "invalid bytes");
            await WriteRecoveryMarkerAsync(
                target,
                jobId,
                source,
                target,
                "copy-started");

            var service = _provider.GetRequiredService<AudiobookContentMoveService>();
            var exception = await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.MoveContentsAsync(request, CancellationToken.None));

            Assert.Contains("partial file does not match", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("invalid bytes", await File.ReadAllTextAsync(partial));
            Assert.False(File.Exists(Path.Join(target, "book.m4b")));
            Assert.True(Directory.Exists(source));
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
                !MoveManifestIdentity.IsTargetBoundaryAuthorization(entry)));
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

        private static Task WriteQuarantineOwnershipMarkerAsync(
            string quarantineRoot,
            Guid jobId,
            string source,
            string target)
        {
            var marker = System.Text.Json.JsonSerializer.Serialize(new
            {
                Version = 1,
                ArtifactType = "quarantine-directory",
                JobId = jobId,
                Source = Path.GetFullPath(source),
                Target = Path.GetFullPath(target),
                DirectoryPath = Path.GetFullPath(quarantineRoot),
                OwnedArtifactType = (string?)null
            });
            return File.WriteAllTextAsync(
                Path.Join(quarantineRoot, ".listenarr-quarantine-owner.json"),
                marker);
        }

        private async Task<AudiobookContentMoveRequest> CreateLeasedMoveRequestAsync(
            string source,
            string target,
            Guid? jobId = null,
            bool deleteEmptySource = true,
            FileSystemPathSemantics? sourceSemantics = null,
            FileSystemPathSemantics? targetSemantics = null,
            string? sourceCleanupBoundary = null)
        {
            var id = jobId ?? Guid.NewGuid();
            var effectiveTargetSemantics =
                targetSemantics
                ?? sourceSemantics
                ?? FileSystemPathSemantics.CurrentHostDefault;
            var targetBoundary = FindMoveTargetBoundary(target, effectiveTargetSemantics);
            var targetDirectoryIdentity = await _provider
                .GetRequiredService<IDirectoryObjectIdentityResolver>()
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
                Entries =
                [
                    MoveManifestIdentity.CreateTargetBoundaryAuthorization(
                        targetDirectoryIdentity.Version!.Value,
                        targetDirectoryIdentity.Value!)
                ]
            };
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
                !MoveManifestIdentity.IsTargetBoundaryAuthorization(entry)));
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
                    if ((attributes & FileAttributes.ReparsePoint) != 0
                        || IsTestReservedMoveArtifact(Path.GetFileName(path)))
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

        private static bool IsTestReservedMoveArtifact(string name) =>
            name.StartsWith(".listenarr-move-", StringComparison.Ordinal)
            || name.StartsWith(".listenarr-quarantine-", StringComparison.Ordinal)
            || name.StartsWith(".listenarr-temporary-directory-", StringComparison.Ordinal)
            || string.Equals(name, ".listenarr-temp-owner.json", StringComparison.Ordinal)
            || string.Equals(name, ".listenarr-quarantine-owner.json", StringComparison.Ordinal)
            || string.Equals(name, LibraryDirectoryOwnershipMarker.FileName, StringComparison.Ordinal)
            || string.Equals(name, ManagedDirectoryEnrollment.FileName, StringComparison.Ordinal)
            || name.StartsWith(".listenarr-directory-owner-", StringComparison.Ordinal)
                && name.EndsWith(".json", StringComparison.Ordinal)
            || name.Contains(".listenarr-", StringComparison.Ordinal)
                && name.EndsWith(".partial", StringComparison.Ordinal);

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

        private sealed class InterruptAfterEmptySourceQuarantine(
            string source,
            bool recreateSource) : IMoveFaultInjector
        {
            private bool _interrupted;

            public void OnSourceCleanupMutation(
                Guid jobId,
                SourceCleanupFaultPoint faultPoint)
            {
                if (_interrupted
                    || faultPoint != SourceCleanupFaultPoint.AfterEmptySourceDirectoryQuarantine)
                {
                    return;
                }

                _interrupted = true;
                if (recreateSource)
                {
                    Directory.CreateDirectory(source);
                }

                throw new IOException("Injected interruption after source-root quarantine.");
            }
        }

        private sealed class FailEmptySourceStateDeleteOnce : IMoveFaultInjector
        {
            private bool _failed;

            public void OnSourceCleanupMutation(
                Guid jobId,
                SourceCleanupFaultPoint faultPoint)
            {
                if (_failed
                    || faultPoint != SourceCleanupFaultPoint.BeforeEmptySourceStateDelete)
                {
                    return;
                }

                _failed = true;
                throw new System.ComponentModel.Win32Exception(
                    145,
                    "Injected empty-source state retirement failure.");
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

        private sealed class AllowAtomicRenameInjector : IMoveFaultInjector
        {
            public bool AllowAtomicRename => true;
        }

        private sealed class TamperTargetOwnershipAfterPublish(string target) : IMoveFaultInjector
        {
            public async Task AfterPublishedAsync(Guid jobId, CancellationToken cancellationToken)
            {
                var markerPath = Path.Join(target, LibraryDirectoryOwnershipMarker.FileName);
                if (OperatingSystem.IsWindows())
                {
                    File.SetAttributes(markerPath, FileAttributes.Normal);
                }
                await File.WriteAllTextAsync(markerPath, "tampered", cancellationToken);
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
