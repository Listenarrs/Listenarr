using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving
{
    [Trait("Name", "MoveJobProcessor_FileReferenceRewriteTests")]
    [Trait("Category", "BackgroundWorkers")]
    public class MoveJobProcessor_FileReferenceRewriteTests : BaseTests
    {
        private const string LeaseOwner = "test-worker";

        [Fact]
        public async Task ProcessJobAsync_BroadBasePath_MovesOnlyTrackedBookAndRewritesReferences()
        {
            var libraryRoot = FileService.GetTempDirectory(
                "move-processor-broad-base-root");
            var authorPath = Path.Join(libraryRoot, "Shared Author");
            var source = Path.Join(authorPath, "Book One");
            var sibling = Path.Join(authorPath, "Book Two");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(sibling);
            var sourceFile = await FileService.GetFileAsync(
                source,
                "Book One.m4b",
                "owned audio");
            var siblingFile = await FileService.GetFileAsync(
                sibling,
                "Book Two.m4b",
                "foreign audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"move-processor-broad-base-dst-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Book One",
                BasePath = authorPath
            });
            await AddTrackedFileAsync(audiobook, sourceFile, libraryRoot);

            var (queue, job) = await EnqueueProductionManifestMoveAsync(
                audiobook,
                target);
            await _provider.GetRequiredService<IMoveJobProcessor>()
                .ProcessJobAsync(job, CancellationToken.None);

            var completed = Assert.IsType<MoveJob>(
                await queue.GetJobAsync(job.Id));
            Assert.True(
                completed.Status == MoveJobStatus.Completed,
                completed.Error ?? $"Unexpected status: {completed.Status}");
            Assert.False(Directory.Exists(source));
            Assert.Equal("foreign audio", await File.ReadAllTextAsync(siblingFile));
            Assert.Equal(
                "owned audio",
                await File.ReadAllTextAsync(Path.Join(target, "Book One.m4b")));
            var persisted = Assert.IsType<Audiobook>(
                await _audiobookRepository.GetByIdSnapshotAsync(audiobook.Id));
            Assert.Equal(target, persisted.BasePath);
            var tracked = Assert.Single(
                await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
            Assert.Equal(Path.Join(target, "Book One.m4b"), tracked.Path);
        }

        [Fact]
        public async Task ProcessJobAsync_NullBasePath_UsesTrackedManifestAndPublishesTargetBasePath()
        {
            var libraryRoot = FileService.GetTempDirectory(
                "move-processor-null-base-root");
            var source = Path.Join(libraryRoot, "Metadata Only Book");
            Directory.CreateDirectory(source);
            var sourceFile = await FileService.GetFileAsync(
                source,
                "Metadata Only Book.m4b",
                "owned audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"move-processor-null-base-dst-{Guid.NewGuid():N}");
            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Metadata Only Book",
                BasePath = null
            });
            await AddTrackedFileAsync(audiobook, sourceFile, libraryRoot);

            var (queue, job) = await EnqueueProductionManifestMoveAsync(
                audiobook,
                target);
            await _provider.GetRequiredService<IMoveJobProcessor>()
                .ProcessJobAsync(job, CancellationToken.None);

            var completed = Assert.IsType<MoveJob>(
                await queue.GetJobAsync(job.Id));
            Assert.True(
                completed.Status == MoveJobStatus.Completed,
                completed.Error ?? $"Unexpected status: {completed.Status}");
            Assert.False(Directory.Exists(source));
            Assert.Equal(
                "owned audio",
                await File.ReadAllTextAsync(
                    Path.Join(target, "Metadata Only Book.m4b")));
            var persisted = Assert.IsType<Audiobook>(
                await _audiobookRepository.GetByIdSnapshotAsync(audiobook.Id));
            Assert.Equal(target, persisted.BasePath);
        }

        [Fact]
        public async Task ProcessJobAsync_PhysicalMove_RewritesTrackedAudiobookFilePaths()
        {
            var source = FileService.GetTempDirectory("move-processor-file-reference-src");
            var bookPath = await FileService.GetFileAsync(source, "book.m4b", "audio");
            var extras = Path.Join(source, "extras");
            Directory.CreateDirectory(extras);
            var chapterPath = await FileService.GetFileAsync(extras, "chapter2.mp3", "chapter audio");
            var target = Path.Join(
                FileService.GetTempPath(),
                $"move-processor-file-reference-dst-{Guid.NewGuid():N}");

            var audiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Move Processor File References",
                BasePath = source,
                FilePath = source
            });
            await AddTrackedFileAsync(
                audiobook,
                bookPath,
                FileService.GetTempPath());
            await AddTrackedFileAsync(
                audiobook,
                chapterPath,
                FileService.GetTempPath());
            var originalPhysicalIdentities = (await _audiobookFileRepository
                    .GetByAudiobookIdAsync(audiobook.Id))
                .ToDictionary(
                    file => file.Path!,
                    file => file.PhysicalObjectIdentity!,
                    StringComparer.Ordinal);
            var (queue, job) = await EnqueueProductionManifestMoveAsync(
                audiobook,
                target);
            var jobId = job.Id;

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job!, CancellationToken.None);

            var completedJob = Assert.IsType<MoveJob>(
                await queue.GetJobAsync(jobId));
            Assert.Equal(MoveJobStatus.Completed, completedJob.Status);
            Assert.True(File.Exists(Path.Join(target, "book.m4b")));
            Assert.True(File.Exists(Path.Join(target, "extras", "chapter2.mp3")));

            using var verificationScope = _provider.CreateScope();
            var repository = verificationScope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            var updatedAudiobook = Assert.IsType<Audiobook>(
                await repository.GetByIdAsync(audiobook.Id));
            Assert.Equal(Path.GetFullPath(target), updatedAudiobook.BasePath);
            Assert.Equal(Path.GetFullPath(target), updatedAudiobook.FilePath);
            var updatedFiles = Assert.IsAssignableFrom<ICollection<AudiobookFile>>(
                updatedAudiobook.Files);
            var movedBookPath = Path.Join(target, "book.m4b");
            var movedChapterPath = Path.Join(target, "extras", "chapter2.mp3");
            var movedBook = Assert.Single(updatedFiles, file => file.Path == movedBookPath);
            var movedChapter = Assert.Single(updatedFiles, file => file.Path == movedChapterPath);
            Assert.Equal(GetPhysicalObjectIdentity(movedBookPath), movedBook.PhysicalObjectIdentity);
            Assert.Equal(GetPhysicalObjectIdentity(movedChapterPath), movedChapter.PhysicalObjectIdentity);
            Assert.Equal(originalPhysicalIdentities[bookPath], movedBook.PhysicalObjectIdentity);
            Assert.Equal(originalPhysicalIdentities[chapterPath], movedChapter.PhysicalObjectIdentity);
            Assert.DoesNotContain(
                updatedFiles,
                file => file.Path?.StartsWith(source, StringComparison.Ordinal) == true);
        }

        private async Task<(IMoveQueueService Queue, MoveJob Job)> EnqueueProductionManifestMoveAsync(
            Audiobook audiobook,
            string target)
        {
            var manifest = await _provider
                .GetRequiredService<IMoveSourceManifestService>()
                .BuildAsync(audiobook);
            var targetResolution = await _provider
                .GetRequiredService<IFileSystemSemanticsResolver>()
                .ResolveAsync(target);
            Assert.Equal(PathIdentityState.Valid, targetResolution.State);
            var targetBoundary = FileService.GetTempPath();
            var targetIdentity = PathIdentitySnapshot.FromResolution(
                targetResolution.Semantics,
                FileSystemCaseSensitivityMode.Auto,
                targetBoundary,
                target);
            var targetDirectoryIdentity = await _provider
                .GetRequiredService<IDirectoryObjectIdentityResolver>()
                .ResolveAsync(targetBoundary);
            Assert.True(
                targetDirectoryIdentity.IsAvailable,
                targetDirectoryIdentity.UnavailableReason);
            var queue = _provider.GetRequiredService<IMoveQueueService>();
            var jobId = await queue.EnqueueMoveAsync(new MoveEnqueueCommand(
                audiobook.Id,
                manifest.SourceRoot,
                manifest.SourceIdentity,
                manifest.Entries,
                target,
                targetIdentity,
                targetDirectoryIdentity.Version!.Value,
                targetDirectoryIdentity.Value!,
                DeleteEmptySource: true));
            var job = Assert.IsType<MoveJob>(
                await queue.GetJobAsync(jobId));
            await PrepareJobForProcessingAsync(queue, job);
            return (queue, job);
        }

        private async Task AddTrackedFileAsync(
            Audiobook audiobook,
            string path,
            string boundary)
        {
            var semantics = FileSystemPathSemantics.CurrentHostDefault;
            var identity = AudiobookFilePathIdentity.CreateValid(
                path,
                semantics,
                FileSystemCaseSensitivityMode.Auto,
                boundary);
            var tracked = AudiobookFile.CreateUnresolved(path);
            tracked.AudiobookId = audiobook.Id;
            tracked.ApplyPathIdentity(path, identity);
            if (File.Exists(path))
            {
                using var parent = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                    Path.GetDirectoryName(path)!,
                    createMissing: false);
                using var file = parent.OpenExistingFileForStableRead(
                    Path.GetFileName(path));
                tracked.ApplyPhysicalObjectIdentity(
                    file.GetObjectIdentity(),
                    DateTime.UtcNow);
            }
            await _audiobookFileRepository.AddAsync(tracked);
        }

        private static string GetPhysicalObjectIdentity(string path)
        {
            using var parent = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                Path.GetDirectoryName(path)!,
                createMissing: false);
            using var file = parent.OpenExistingFileForStableRead(Path.GetFileName(path));
            return file.GetObjectIdentity();
        }

        private static async Task PrepareJobForProcessingAsync(IMoveQueueService queue, MoveJob job)
        {
            var leaseGeneration = Assert.IsType<int>(
                await queue.TryClaimJobAsync(job.Id, LeaseOwner));
            job.LeaseOwner = LeaseOwner;
            job.LeaseGeneration = leaseGeneration;
        }
    }
}
