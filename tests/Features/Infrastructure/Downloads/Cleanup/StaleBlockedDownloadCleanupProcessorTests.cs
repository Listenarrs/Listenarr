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

namespace Listenarr.Tests.Features.Infrastructure.Downloads.Cleanup
{
    [Trait("Name", nameof(StaleBlockedDownloadCleanupProcessorTests))]
    [Trait("Category", "Downloads")]
    public sealed class StaleBlockedDownloadCleanupProcessorTests : BaseTests
    {
        private readonly Mock<IDownloadRepository> _downloads = new();
        private readonly Mock<IAudiobookRepository> _audiobooks = new();
        private readonly Mock<IAudiobookFileRepository> _files = new();

        private StaleBlockedDownloadCleanupProcessor CreateSut()
        {
            var provider = new Mock<IServiceProvider>();
            provider.Setup(p => p.GetService(typeof(IDownloadRepository))).Returns(_downloads.Object);
            provider.Setup(p => p.GetService(typeof(IAudiobookRepository))).Returns(_audiobooks.Object);
            provider.Setup(p => p.GetService(typeof(IAudiobookFileRepository))).Returns(_files.Object);
            var scope = new Mock<IServiceScope>();
            scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
            var scopeFactory = new Mock<IServiceScopeFactory>();
            scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);
            return new StaleBlockedDownloadCleanupProcessor(
                scopeFactory.Object,
                Mock.Of<ILogger<StaleBlockedDownloadCleanupProcessor>>());
        }

        private void GivenDownloads(params Download[] downloads) =>
            _downloads.Setup(r => r.GetAllAsync()).ReturnsAsync(downloads.ToList());

        private void GivenAudiobook(int id, Audiobook? audiobook) =>
            _audiobooks.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(audiobook);

        private void GivenFiles(int audiobookId, int count) =>
            _files.Setup(r => r.GetByAudiobookIdAsync(audiobookId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Enumerable.Range(0, count).Select(_ => new AudiobookFile()).ToList());

        [Fact]
        public async Task RunCycleAsync_ReapsBlockedDownload_WithNoAssociatedAudiobook()
        {
            var download = new DownloadBuilder().WithId("d-noaudio").WithBlockedStatus("blocked").Build();
            GivenDownloads(download);

            await CreateSut().RunCycleAsync(CancellationToken.None);

            _downloads.Verify(r => r.RemoveAsync("d-noaudio"), Times.Once);
        }

        [Fact]
        public async Task RunCycleAsync_ReapsBlockedDownload_WhenAudiobookNoLongerExists()
        {
            var download = new DownloadBuilder().WithId("d-deleted").WithBlockedStatus("blocked")
                .WithAudiobookId(401).Build();
            GivenDownloads(download);
            GivenAudiobook(401, null);

            await CreateSut().RunCycleAsync(CancellationToken.None);

            _downloads.Verify(r => r.RemoveAsync("d-deleted"), Times.Once);
        }

        [Fact]
        public async Task RunCycleAsync_ReapsBlockedDownload_WhenAudiobookAlreadyHasFiles()
        {
            var download = new DownloadBuilder().WithId("d-hasfiles").WithBlockedStatus("blocked")
                .WithAudiobookId(380).Build();
            GivenDownloads(download);
            GivenAudiobook(380, new AudiobookBuilder().WithId(380).Build());
            GivenFiles(380, 1);

            await CreateSut().RunCycleAsync(CancellationToken.None);

            _downloads.Verify(r => r.RemoveAsync("d-hasfiles"), Times.Once);
        }

        [Fact]
        public async Task RunCycleAsync_LeavesBlockedDownload_WhenAudiobookExistsWithNoFiles()
        {
            var download = new DownloadBuilder().WithId("d-unresolved").WithBlockedStatus("blocked")
                .WithAudiobookId(500).Build();
            GivenDownloads(download);
            GivenAudiobook(500, new AudiobookBuilder().WithId(500).Build());
            GivenFiles(500, 0);

            await CreateSut().RunCycleAsync(CancellationToken.None);

            _downloads.Verify(r => r.RemoveAsync(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task RunCycleAsync_IgnoresDownloads_ThatAreNotImportBlocked()
        {
            // A Moved download whose audiobook is gone would meet the "deleted" reap condition,
            // but it must be ignored because it is not ImportBlocked.
            var moved = new DownloadBuilder().WithId("d-moved").WithStatus(DownloadStatus.Moved)
                .WithAudiobookId(401).Build();
            GivenDownloads(moved);
            GivenAudiobook(401, null);

            await CreateSut().RunCycleAsync(CancellationToken.None);

            _downloads.Verify(r => r.RemoveAsync(It.IsAny<string>()), Times.Never);
        }
    }
}
