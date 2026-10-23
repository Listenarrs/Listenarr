/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using System.Text.Json;
using Listenarr.Domain.Notifications;
using Listenarr.Infrastructure.Downloads.Import;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Downloads.Import
{
    public sealed class ImportFinalizationServiceTests : BaseTests
    {
        [Fact]
        public async Task FinalizeAsync_RequiresEveryPostImportCheckpoint()
        {
            var audiobook = await CreateAudiobook();
            var client = await CreateDownloadClientConfiguration();
            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(audiobook)
                .WithDownloadClientConfiguration(client)
                .WithStatus(DownloadStatus.ImportPending)
                .Build());
            var job = new DownloadProcessingJobBuilder()
                .WithDownload(download)
                .WithStatus(ProcessingJobStatus.Processing)
                .Build();
            job.SetCheckpoint("FilesImported");
            await _downloadProcessingJobRepository.AddAsync(job);

            var service = _provider.GetRequiredService<IImportFinalizationService>();

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.FinalizeAsync(
                job.Id, download.Id, audiobook.Id, audiobook.Title ?? download.Title,
                client.Id, "finalization-checkpoints", sourceRetained: null));

            Assert.Equal(DownloadStatus.ImportPending, (await _downloadRepository.GetByIdAsync(download.Id))!.Status);
            Assert.Equal(ProcessingJobStatus.Processing, (await _downloadProcessingJobRepository.GetByIdAsync(job.Id))!.Status);
        }

        [Fact]
        public async Task FinalizeAsync_CommitsDownloadJobAndHistoryIdempotently()
        {
            var audiobook = await CreateAudiobook();
            var client = await CreateDownloadClientConfiguration();
            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(audiobook)
                .WithDownloadClientConfiguration(client)
                .WithStatus(DownloadStatus.ImportPending)
                .Build());
            var job = new DownloadProcessingJobBuilder()
                .WithDownload(download)
                .WithStatus(ProcessingJobStatus.Processing)
                .Build();
            job.SetCheckpoint("FilesImported");
            job.SetCheckpoint("ClientMarkedImported");
            job.SetCheckpoint("ScanEnqueued", Guid.NewGuid().ToString());
            await _downloadProcessingJobRepository.AddAsync(job);

            var service = _provider.GetRequiredService<IImportFinalizationService>();
            await service.FinalizeAsync(
                job.Id, download.Id, audiobook.Id, audiobook.Title ?? download.Title,
                client.Id, "finalization-success", sourceRetained: true);
            await service.FinalizeAsync(
                job.Id, download.Id, audiobook.Id, audiobook.Title ?? download.Title,
                client.Id, "finalization-success", sourceRetained: true);

            var finalizedDownload = (await _downloadRepository.GetByIdAsync(download.Id))!;
            Assert.Equal(DownloadStatus.Moved, finalizedDownload.Status);
            Assert.Equal(
                bool.TrueString,
                finalizedDownload.GetMetadataString(Download.SourceRetainedMetadataKey));
            Assert.Equal(ProcessingJobStatus.Completed, (await _downloadProcessingJobRepository.GetByIdAsync(job.Id))!.Status);
            var page = await _historyRepository.QueryAsync(new HistoryQuery
            {
                CorrelationId = "finalization-success"
            });
            var imported = Assert.Single(page.Records, entry =>
                entry.EventType == HistoryEvents.Imported &&
                entry.Outcome == HistoryOutcome.Succeeded);
            using var details = JsonDocument.Parse(imported.Data!);
            Assert.True(details.RootElement.GetProperty("SourceRetentionKnown").GetBoolean());
            Assert.True(details.RootElement.GetProperty(Download.SourceRetainedMetadataKey).GetBoolean());
        }

        [Fact]
        public async Task FinalizeAsync_WhenUpgrade_FiresBookImportedAndBookUpgraded()
        {
            var (service, notifier, job, download, audiobook, client) = await SeedFinalizableJobWithSpyAsync();

            await service.FinalizeAsync(
                job.Id, download.Id, audiobook.Id, audiobook.Title ?? download.Title,
                client.Id, "finalization-upgrade", sourceRetained: false, wasUpgrade: true);

            notifier.Verify(n => n.NotifyAsync(
                NotificationTriggers.BookImported, It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Once);
            notifier.Verify(n => n.NotifyAsync(
                NotificationTriggers.BookUpgraded, It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task FinalizeAsync_WhenFirstImport_DoesNotFireBookUpgraded()
        {
            var (service, notifier, job, download, audiobook, client) = await SeedFinalizableJobWithSpyAsync();

            await service.FinalizeAsync(
                job.Id, download.Id, audiobook.Id, audiobook.Title ?? download.Title,
                client.Id, "finalization-first-import", sourceRetained: false, wasUpgrade: false);

            notifier.Verify(n => n.NotifyAsync(
                NotificationTriggers.BookImported, It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Once);
            notifier.Verify(n => n.NotifyAsync(
                NotificationTriggers.BookUpgraded, It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        private async Task<(
            ImportFinalizationService service,
            Mock<IBookLifecycleNotifier> notifier,
            DownloadProcessingJob job,
            Download download,
            Audiobook audiobook,
            DownloadClientConfiguration client)> SeedFinalizableJobWithSpyAsync()
        {
            var audiobook = await CreateAudiobook();
            var client = await CreateDownloadClientConfiguration();
            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(audiobook)
                .WithDownloadClientConfiguration(client)
                .WithStatus(DownloadStatus.ImportPending)
                .Build());
            var job = new DownloadProcessingJobBuilder()
                .WithDownload(download)
                .WithStatus(ProcessingJobStatus.Processing)
                .Build();
            job.SetCheckpoint("FilesImported");
            job.SetCheckpoint("ClientMarkedImported");
            job.SetCheckpoint("ScanEnqueued", Guid.NewGuid().ToString());
            await _downloadProcessingJobRepository.AddAsync(job);

            var notifier = new Mock<IBookLifecycleNotifier>();
            notifier
                .Setup(n => n.NotifyAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var dbFactory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            var service = new ImportFinalizationService(dbFactory, notifier.Object);
            return (service, notifier, job, download, audiobook, client);
        }
    }
}
