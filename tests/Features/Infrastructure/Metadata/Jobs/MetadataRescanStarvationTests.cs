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
    [Trait("Name", "MetadataRescanStarvationTests")]
    [Trait("Category", "Infrastructure")]
    public sealed class MetadataRescanStarvationTests : BaseTests
    {
        private const int BatchSize = 20;

        [Fact]
        public async Task RunCycleAsync_TwentyUnsuccessfulLowerIds_DoNotStarveTwentyFirstCandidate()
        {
            var extractedPaths = new List<string>();
            string healthyPath = string.Empty;
            var metadataService = new Mock<IMetadataService>();
            metadataService.Setup(service => service.ExtractFileMetadataAsync(It.IsAny<MetadataFileSource>()))
                .Returns<MetadataFileSource>(source =>
                {
                    lock (extractedPaths)
                    {
                        extractedPaths.Add(source.PublicPath);
                    }

                    // Extraction failures are swallowed by the file service and
                    // reported as "not updated", so the row stays a candidate.
                    return Task.FromResult(source.PublicPath == healthyPath
                        ? new AudioMetadata
                        {
                            Duration = TimeSpan.FromSeconds(90),
                            Format = "m4b",
                            SampleRate = 44100
                        }
                        : null);
                });
            Init(builder => builder.WithSingleton(metadataService.Object));

            var directory = FileService.GetTempDirectory("metadata-rescan-unsuccessful-low-ids");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("A Princess of Mars")
                .WithBasePath(directory)
                .Build());
            for (var index = 0; index < BatchSize; index++)
            {
                var failingPath = await FileService.GetFileAsync(directory, $"part-{index:D2}.m4b", "audio");
                await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                    .WithAudiobook(audiobook)
                    .WithPath(failingPath)
                    .Build());
            }
            healthyPath = await FileService.GetFileAsync(directory, "part-20.m4b", "audio");
            var healthyFile = await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(healthyPath)
                .Build());

            var processor = CreateProcessor();
            await processor.RunCycleAsync(CancellationToken.None);
            await processor.RunCycleAsync(CancellationToken.None);

            var persisted = await ReadFileAsync(healthyFile.Id);
            Assert.Equal(90, persisted.DurationSeconds);
            Assert.Equal(44100, persisted.SampleRate);
            Assert.Equal(BatchSize + 1, extractedPaths.Count);
            Assert.Equal(BatchSize + 1, extractedPaths.Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public async Task RunCycleAsync_MissingLowerIdFiles_DoNotTakeBatchSlots()
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

            var directory = FileService.GetTempDirectory("metadata-rescan-missing-low-ids");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("A Princess of Mars")
                .WithBasePath(directory)
                .Build());
            for (var index = 0; index < BatchSize; index++)
            {
                await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                    .WithAudiobook(audiobook)
                    .WithPath(Path.Join(directory, "absent", $"part-{index:D2}.m4b"))
                    .Build());
            }
            var presentPath = await FileService.GetFileAsync(directory, "part-20.m4b", "audio");
            var presentFile = await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(presentPath)
                .Build());

            await CreateProcessor().RunCycleAsync(CancellationToken.None);

            var persisted = await ReadFileAsync(presentFile.Id);
            Assert.Equal(90, persisted.DurationSeconds);
            metadataService.Verify(service => service.ExtractFileMetadataAsync(
                It.IsAny<MetadataFileSource>()), Times.Once);
        }

        [Fact]
        public async Task RunCycleAsync_RefreshThatLeavesRowACandidate_IsNotRepeatedNextCycle()
        {
            var metadataService = new Mock<IMetadataService>();
            metadataService.Setup(service => service.ExtractFileMetadataAsync(It.IsAny<MetadataFileSource>()))
                .ReturnsAsync(new AudioMetadata
                {
                    Duration = TimeSpan.FromSeconds(90),
                    Format = "m4b"
                });
            Init(builder => builder.WithSingleton(metadataService.Object));

            var audioPath = await FileService.GetFileAsync(
                FileService.GetTempDirectory("metadata-rescan-still-candidate"),
                "part-00.m4b",
                "audio");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("A Princess of Mars")
                .WithBasePath(Path.GetDirectoryName(audioPath)!)
                .Build());
            var file = await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(audioPath)
                .Build());

            var processor = CreateProcessor();
            await processor.RunCycleAsync(CancellationToken.None);
            await processor.RunCycleAsync(CancellationToken.None);

            // No sample rate was extracted, so the refreshed row still matches
            // the missing-metadata predicate.
            var persisted = await ReadFileAsync(file.Id);
            Assert.Equal(90, persisted.DurationSeconds);
            Assert.Null(persisted.SampleRate);
            metadataService.Verify(service => service.ExtractFileMetadataAsync(
                It.IsAny<MetadataFileSource>()), Times.Once);
        }

        private MetadataRescanProcessor CreateProcessor()
        {
            return new MetadataRescanProcessor(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                _provider.GetRequiredService<IAudiobookOperationCoordinator>(),
                _provider.GetRequiredService<IMoveQueueService>(),
                NullLogger<MetadataRescanProcessor>.Instance);
        }

        private async Task<AudiobookFile> ReadFileAsync(int fileId)
        {
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using var verification = await factory.CreateDbContextAsync();
            return await verification.AudiobookFiles.SingleAsync(candidate => candidate.Id == fileId);
        }
    }
}
