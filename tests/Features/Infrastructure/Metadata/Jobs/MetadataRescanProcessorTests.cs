/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Metadata.Jobs
{
    [Trait("Area", "Metadata")]
    [Trait("Name", "MetadataRescanProcessorTests")]
    [Trait("Category", "Infrastructure")]
    public sealed class MetadataRescanProcessorTests : BaseTests
    {
        [Fact]
        public async Task RunCycleAsync_UnresolvedMoveExecution_SkipsCandidateBeforeExtraction()
        {
            var metadataService = new Mock<IMetadataService>(MockBehavior.Strict);
            Init(builder => builder.WithSingleton(metadataService.Object));
            var audioPath = await FileService.GetFileAsync(
                FileService.GetTempDirectory("metadata-rescan-unresolved-move"),
                "book.m4b",
                "audio");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Metadata Move Fence")
                .WithBasePath(Path.GetDirectoryName(audioPath)!)
                .Build());
            var file = await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(audioPath)
                .Build());
            await MoveJobTestFactory.SeedUnresolvedExecutionAsync(
                _provider,
                audiobook.Id,
                audiobook.BasePath!,
                Path.Join(FileService.GetTempPath(), $"metadata-move-target-{Guid.NewGuid():N}"));

            var processor = new MetadataRescanProcessor(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                _provider.GetRequiredService<IAudiobookOperationCoordinator>(),
                _provider.GetRequiredService<IMoveQueueService>(),
                NullLogger<MetadataRescanProcessor>.Instance);
            await processor.RunCycleAsync(CancellationToken.None);

            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using var verification = await factory.CreateDbContextAsync();
            var persisted = await verification.AudiobookFiles.SingleAsync(candidate => candidate.Id == file.Id);
            Assert.Null(persisted.DurationSeconds);
            Assert.Null(persisted.Format);
            metadataService.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task RunCycleAsync_ForeignHostStoredPath_SkipsBeforePinning()
        {
            var metadataService = new Mock<IMetadataService>(MockBehavior.Strict);
            Init(builder => builder.WithSingleton(metadataService.Object));
            var foreignBasePath = OperatingSystem.IsWindows()
                ? "/server/mnt/drive/Audiobooks/Imported"
                : @"C:\server\Audiobooks\Imported";
            var foreignFilePath = OperatingSystem.IsWindows()
                ? "/server/mnt/drive/Audiobooks/Imported/book.m4b"
                : @"C:\server\Audiobooks\Imported\book.m4b";
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Copied Database")
                .WithBasePath(foreignBasePath)
                .Build());
            var file = await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(foreignFilePath)
                .Build());

            var processor = new MetadataRescanProcessor(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                _provider.GetRequiredService<IAudiobookOperationCoordinator>(),
                _provider.GetRequiredService<IMoveQueueService>(),
                NullLogger<MetadataRescanProcessor>.Instance);

            await processor.RunCycleAsync(CancellationToken.None);

            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using var verification = await factory.CreateDbContextAsync();
            var persisted = await verification.AudiobookFiles.SingleAsync(candidate => candidate.Id == file.Id);
            Assert.Null(persisted.DurationSeconds);
            Assert.Null(persisted.Format);
            metadataService.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task RunCycleAsync_KnownForeignCandidates_DoNotStarveHostCandidate()
        {
            var metadataService = new Mock<IMetadataService>();
            metadataService.Setup(service => service.ExtractFileMetadataAsync(It.IsAny<MetadataFileSource>()))
                .ReturnsAsync(new AudioMetadata
                {
                    Duration = TimeSpan.FromSeconds(90),
                    Format = "m4b",
                    SampleRate = 44100
                });
            Init(builder => builder.WithSingleton(metadataService.Object));

            var foreignSyntax = OperatingSystem.IsWindows()
                ? FileSystemPathSyntax.Unix
                : FileSystemPathSyntax.Windows;
            var foreignBasePath = OperatingSystem.IsWindows()
                ? "/server/mnt/drive/Audiobooks/Imported"
                : @"C:\server\Audiobooks\Imported";
            var foreignAudiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Copied Foreign Database")
                .WithBasePath(foreignBasePath)
                .Build());
            for (var index = 0; index < 25; index++)
            {
                var foreignPath = OperatingSystem.IsWindows()
                    ? $"/server/mnt/drive/Audiobooks/Imported/foreign-{index:D2}.m4b"
                    : $@"C:\server\Audiobooks\Imported\foreign-{index:D2}.m4b";
                var foreignFile = AudiobookFile.CreateUnresolved(foreignPath);
                foreignFile.AudiobookId = foreignAudiobook.Id;
                foreignFile.ApplyPathIdentity(
                    foreignPath,
                    AudiobookFilePathIdentity.CreateUnavailable(
                        foreignPath,
                        foreignSyntax,
                        FileSystemCaseSensitivityMode.Auto,
                        foreignBasePath,
                        "The persisted path belongs to a different host filesystem syntax."));
                await _audiobookFileRepository.AddAsync(foreignFile);
            }

            var localPath = await FileService.GetFileAsync(
                FileService.GetTempDirectory("metadata-rescan-host-candidate"),
                "book.m4b",
                "audio");
            var localAudiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Host Candidate")
                .WithBasePath(Path.GetDirectoryName(localPath)!)
                .Build());
            var localFile = await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(localAudiobook)
                .WithPath(localPath)
                .Build());

            var processor = new MetadataRescanProcessor(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                _provider.GetRequiredService<IAudiobookOperationCoordinator>(),
                _provider.GetRequiredService<IMoveQueueService>(),
                NullLogger<MetadataRescanProcessor>.Instance);

            await processor.RunCycleAsync(CancellationToken.None);

            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using var verification = await factory.CreateDbContextAsync();
            var persisted = await verification.AudiobookFiles.SingleAsync(candidate => candidate.Id == localFile.Id);
            Assert.Equal(90, persisted.DurationSeconds);
            Assert.Equal("m4b", persisted.Format);
            Assert.Equal(44100, persisted.SampleRate);
            metadataService.Verify(service => service.ExtractFileMetadataAsync(
                It.IsAny<MetadataFileSource>()), Times.Once);
        }

        [Fact]
        public async Task RunCycleAsync_PathChangesDuringExtraction_DiscardsStaleMetadataResult()
        {
            var extractionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseExtraction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var metadataService = new Mock<IMetadataService>();
            metadataService.Setup(service => service.ExtractFileMetadataAsync(It.IsAny<MetadataFileSource>()))
                .Returns(async () =>
                {
                    extractionStarted.SetResult();
                    await releaseExtraction.Task;
                    return new AudioMetadata
                    {
                        Duration = TimeSpan.FromSeconds(321),
                        Format = "m4b"
                    };
                });
            Init(builder => builder.WithSingleton(metadataService.Object));

            var oldPath = await FileService.GetFileAsync(
                FileService.GetTempDirectory("metadata-rescan-stale-old"),
                "book.m4b",
                "old audio");
            var newPath = await FileService.GetFileAsync(
                FileService.GetTempDirectory("metadata-rescan-stale-new"),
                "book.m4b",
                "new audio");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Stale Metadata Extraction")
                .WithBasePath(Path.GetDirectoryName(oldPath)!)
                .Build());
            var file = await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(oldPath)
                .Build());

            var processor = new MetadataRescanProcessor(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                _provider.GetRequiredService<IAudiobookOperationCoordinator>(),
                _provider.GetRequiredService<IMoveQueueService>(),
                NullLogger<MetadataRescanProcessor>.Instance);
            var cycle = processor.RunCycleAsync(CancellationToken.None);
            await extractionStarted.Task;

            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using (var moveContext = await factory.CreateDbContextAsync())
            {
                var movedFile = await moveContext.AudiobookFiles.SingleAsync(candidate => candidate.Id == file.Id);
                movedFile.Path = newPath;
                await moveContext.SaveChangesAsync();
            }

            releaseExtraction.SetResult();
            await cycle;

            await using var verification = await factory.CreateDbContextAsync();
            var persisted = await verification.AudiobookFiles.SingleAsync(candidate => candidate.Id == file.Id);
            Assert.Equal(newPath, persisted.Path);
            Assert.Null(persisted.DurationSeconds);
            Assert.Null(persisted.Format);
        }

        [Fact]
        public async Task RunCycleAsync_FileGenerationReplacedDuringExtraction_DoesNotApplyMetadataToReplacement()
        {
            var replacementSucceeded = false;
            string audioPath = string.Empty;
            var metadataService = new Mock<IMetadataService>();
            metadataService.Setup(service => service.ExtractFileMetadataAsync(It.IsAny<MetadataFileSource>()))
                .Returns<MetadataFileSource>(source =>
                {
                    var displaced = Path.Join(
                        Path.GetDirectoryName(audioPath)!,
                        $"original-{Guid.NewGuid():N}.m4b");
                    try
                    {
                        File.Move(audioPath, displaced);
                        File.WriteAllText(audioPath, "replacement audio");
                        replacementSucceeded = true;
                    }
                    catch (IOException)
                    {
                        // Windows holds a stable handle that denies replacement and writes.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Some Windows filesystems report the sharing denial as access denied.
                    }

                    return Task.FromResult<AudioMetadata?>(new AudioMetadata
                    {
                        Duration = TimeSpan.FromSeconds(321),
                        Format = "metadata-from-original"
                    });
                });
            Init(builder => builder.WithSingleton(metadataService.Object));

            audioPath = await FileService.GetFileAsync(
                FileService.GetTempDirectory("metadata-rescan-generation-race"),
                "book.m4b",
                "original audio");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Metadata Generation Race")
                .WithBasePath(Path.GetDirectoryName(audioPath)!)
                .Build());
            var pending = new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(audioPath)
                .Build();
            string originalPhysicalIdentity;
            using (var lease = PinnedAudiobookFileRegistrationLease.Open(audioPath))
            {
                originalPhysicalIdentity = lease.PhysicalObjectIdentity;
                pending.ApplyPhysicalObjectIdentity(
                    originalPhysicalIdentity,
                    DateTime.UtcNow);
            }
            var file = await _audiobookFileRepository.AddAsync(pending);

            var processor = new MetadataRescanProcessor(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                _provider.GetRequiredService<IAudiobookOperationCoordinator>(),
                _provider.GetRequiredService<IMoveQueueService>(),
                NullLogger<MetadataRescanProcessor>.Instance);
            await processor.RunCycleAsync(CancellationToken.None);

            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using var verification = await factory.CreateDbContextAsync();
            var persisted = await verification.AudiobookFiles.SingleAsync(
                candidate => candidate.Id == file.Id);
            Assert.Equal(originalPhysicalIdentity, persisted.PhysicalObjectIdentity);
            if (replacementSucceeded)
            {
                Assert.Null(persisted.DurationSeconds);
                Assert.Null(persisted.Format);
                Assert.Equal("replacement audio", await File.ReadAllTextAsync(audioPath));
            }
            else
            {
                Assert.Equal(321, persisted.DurationSeconds);
                Assert.Equal("metadata-from-original", persisted.Format);
                Assert.Equal("original audio", await File.ReadAllTextAsync(audioPath));
            }
        }

        [Fact]
        public async Task RunCycleAsync_PartialMetadataRefresh_PreservesExistingValidFields()
        {
            var metadataService = new Mock<IMetadataService>();
            metadataService.Setup(service => service.ExtractFileMetadataAsync(It.IsAny<MetadataFileSource>()))
                .ReturnsAsync(new AudioMetadata
                {
                    Duration = TimeSpan.FromSeconds(222),
                    Format = "refreshed-format",
                    SampleRate = 48000
                });
            Init(builder => builder.WithSingleton(metadataService.Object));

            var audioPath = await FileService.GetFileAsync(
                FileService.GetTempDirectory("metadata-rescan-partial"),
                "book.m4b",
                "audio");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Partial Metadata")
                .WithBasePath(Path.GetDirectoryName(audioPath)!)
                .Build());
            var pending = AudiobookFile.CreateUnresolved(audioPath);
            pending.AudiobookId = audiobook.Id;
            pending.DurationSeconds = 111;
            pending.Format = "existing-format";
            pending.Codec = "existing-codec";
            pending.Bitrate = 64000;
            pending.SampleRate = null;
            pending.Channels = 2;
            var file = await _audiobookFileRepository.AddAsync(pending);

            var processor = new MetadataRescanProcessor(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                _provider.GetRequiredService<IAudiobookOperationCoordinator>(),
                _provider.GetRequiredService<IMoveQueueService>(),
                NullLogger<MetadataRescanProcessor>.Instance);
            await processor.RunCycleAsync(CancellationToken.None);

            using var verificationScope = _provider.CreateScope();
            var verificationRepository = verificationScope.ServiceProvider
                .GetRequiredService<IAudiobookFileRepository>();
            var persisted = await verificationRepository.GetByIdAsync(file.Id);
            Assert.NotNull(persisted);
            Assert.Equal(222, persisted.DurationSeconds);
            Assert.Equal("refreshed-format", persisted.Format);
            Assert.Equal(48000, persisted.SampleRate);
            Assert.Equal("existing-codec", persisted.Codec);
            Assert.Equal(64000, persisted.Bitrate);
            Assert.Equal(2, persisted.Channels);
            Assert.False(string.IsNullOrWhiteSpace(
                persisted.PhysicalObjectIdentity));
        }

        [Theory]
        [InlineData(FileSystemCaseSensitivityMode.Sensitive, false)]
        [InlineData(FileSystemCaseSensitivityMode.Insensitive, true)]
        public async Task RunCycleAsync_NonAudioFile_ClearsLegacyFilePathUsingResolvedRootSemantics(
            FileSystemCaseSensitivityMode caseSensitivityMode,
            bool shouldClearLegacyPath)
        {
            var rootPath = FileService.GetTempDirectory("metadata-rescan-root");
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Metadata Root")
                .WithPath(rootPath)
                .WithCaseSensitivityMode(caseSensitivityMode)
                .WithIsDefault()
                .Build());
            var audiobookPath = Path.Join(rootPath, "CaseBook", "book.txt");
            var filePath = Path.Join(rootPath, "casebook", "book.txt");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Case Book")
                .WithFilePath(audiobookPath)
                .Build());
            var file = await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(filePath)
                .Build());

            var processor = new MetadataRescanProcessor(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                _provider.GetRequiredService<IAudiobookOperationCoordinator>(),
                _provider.GetRequiredService<IMoveQueueService>(),
                NullLogger<MetadataRescanProcessor>.Instance);
            await processor.RunCycleAsync(CancellationToken.None);

            using var verificationScope = _provider.CreateScope();
            var verificationAudiobooks = verificationScope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            var verificationFiles = verificationScope.ServiceProvider.GetRequiredService<IAudiobookFileRepository>();
            var updated = await verificationAudiobooks.GetByIdAsync(audiobook.Id);
            var removed = await verificationFiles.GetByIdAsync(file.Id);
            Assert.Null(removed);
            if (shouldClearLegacyPath)
            {
                Assert.Null(updated?.FilePath);
                Assert.Null(updated?.FileSize);
            }
            else
            {
                Assert.Equal(audiobookPath, updated?.FilePath);
            }
        }

        [Fact]
        public async Task RunCycleAsync_NestedRootUsesMostSpecificSemanticsForLegacyPathCleanup()
        {
            var outerRoot = FileService.GetTempDirectory("metadata-rescan-nested-outer");
            var innerRoot = Path.Join(outerRoot, "Sensitive Library");
            Directory.CreateDirectory(innerRoot);
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("A Outer")
                .WithPath(outerRoot)
                .WithCaseSensitivityMode(FileSystemCaseSensitivityMode.Insensitive)
                .Build());
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Z Inner")
                .WithPath(innerRoot)
                .WithCaseSensitivityMode(FileSystemCaseSensitivityMode.Sensitive)
                .Build());
            var audiobookPath = Path.Join(innerRoot, "CaseBook", "book.txt");
            var filePath = Path.Join(innerRoot, "casebook", "book.txt");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Nested Case Book")
                .WithFilePath(audiobookPath)
                .Build());
            var file = await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(filePath)
                .Build());

            var processor = new MetadataRescanProcessor(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                _provider.GetRequiredService<IAudiobookOperationCoordinator>(),
                _provider.GetRequiredService<IMoveQueueService>(),
                NullLogger<MetadataRescanProcessor>.Instance);
            await processor.RunCycleAsync(CancellationToken.None);

            using var verificationScope = _provider.CreateScope();
            var verificationAudiobooks = verificationScope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            var verificationFiles = verificationScope.ServiceProvider.GetRequiredService<IAudiobookFileRepository>();
            var updated = await verificationAudiobooks.GetByIdAsync(audiobook.Id);
            var removed = await verificationFiles.GetByIdAsync(file.Id);
            Assert.Null(removed);
            Assert.Equal(audiobookPath, updated?.FilePath);
        }
    }
}
