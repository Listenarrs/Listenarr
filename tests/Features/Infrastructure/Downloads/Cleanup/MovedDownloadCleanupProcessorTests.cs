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
using Listenarr.Tests.Mocks;

namespace Listenarr.Tests.Features.Infrastructure.Downloads.Cleanup
{
    public sealed class MovedDownloadCleanupProcessorTests : BaseTests
    {
        private readonly DownloadClientGatewayMock _gateway = new();

        public override Task InitializeAsync()
        {
            _services.AddSingleton<IDownloadClientGateway>(_gateway);
            _services.AddSingleton<IMovedDownloadCleanupProcessor, MovedDownloadCleanupProcessor>();
            Init();
            return Task.CompletedTask;
        }

        [Fact]
        public async Task RunCycleAsync_DoesNotCleanupMovedDownloadWithoutCompletedImportJob()
        {
            var client = await CreateDownloadClientConfiguration();
            var download = new DownloadBuilder()
                .WithDownloadClientConfiguration(client)
                .WithStatus(DownloadStatus.Moved)
                .WithCompletedStatus(DateTime.UtcNow.AddMinutes(-5))
                .Build();
            download.Status = DownloadStatus.Moved;
            download.Metadata["CanBeRemoved"] = true;
            await _downloadRepository.AddAsync(download);

            await _provider.GetRequiredService<IMovedDownloadCleanupProcessor>()
                .RunCycleAsync(CancellationToken.None);

            Assert.NotNull(await _downloadRepository.GetByIdAsync(download.Id));
            Assert.Equal(0, _gateway.GetCallCount(nameof(_gateway.RemoveAsync)));
        }

        [Fact]
        public async Task RunCycleAsync_NonePolicyRetainsImportedOperationalRecord()
        {
            var client = await CreateDownloadClientConfiguration();
            client.RemoveCompletedDownloads = "none";
            await _downloadClientConfigurationRepository.SaveAsync(client);
            var download = new DownloadBuilder()
                .WithDownloadClientConfiguration(client)
                .WithStatus(DownloadStatus.Moved)
                .WithCompletedStatus(DateTime.UtcNow.AddMinutes(-5))
                .Build();
            download.Status = DownloadStatus.Moved;
            download.Metadata["CanBeRemoved"] = true;
            await _downloadRepository.AddAsync(download);
            await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithDownload(download)
                .WithCompleted(DateTime.UtcNow)
                .Build());

            await _provider.GetRequiredService<IMovedDownloadCleanupProcessor>()
                .RunCycleAsync(CancellationToken.None);

            Assert.NotNull(await _downloadRepository.GetByIdAsync(download.Id));
            Assert.Equal(0, _gateway.GetCallCount(nameof(_gateway.RemoveAsync)));
        }

        [Fact]
        public async Task RunCycleAsync_SuccessfulRemovalDeletesOperationalRecordButRetainsHistory()
        {
            var client = await CreateDownloadClientConfiguration();
            client.RemoveCompletedDownloads = "remove";
            await _downloadClientConfigurationRepository.SaveAsync(client);
            _gateway.RemoveResult = true;
            var download = new DownloadBuilder()
                .WithDownloadClientConfiguration(client)
                .WithCompletedStatus(DateTime.UtcNow.AddMinutes(-5))
                .WithClientDownloadId("external-1")
                .Build();
            download.Status = DownloadStatus.Moved;
            download.Metadata["CanBeRemoved"] = true;
            await _downloadRepository.AddAsync(download);
            var job = new DownloadProcessingJobBuilder()
                .WithDownload(download)
                .WithCompleted(DateTime.UtcNow)
                .Build();
            job.JobData["CorrelationId"] = "cleanup-success";
            await _downloadProcessingJobRepository.AddAsync(job);

            await _provider.GetRequiredService<IMovedDownloadCleanupProcessor>()
                .RunCycleAsync(CancellationToken.None);

            Assert.Null(await _downloadRepository.GetByIdAsync(download.Id));
            var history = await _historyRepository.QueryAsync(new HistoryQuery
            {
                CorrelationId = "cleanup-success"
            });
            Assert.Contains(history.Records, entry => entry.EventType == HistoryEvents.CleanupRequested);
            Assert.Contains(history.Records, entry => entry.EventType == HistoryEvents.CleanupSucceeded);
        }

        [Fact]
        public async Task RunCycleAsync_FailedRemovalNeverDeletesOperationalRecord()
        {
            var client = await CreateDownloadClientConfiguration();
            client.RemoveCompletedDownloads = "remove_and_delete";
            await _downloadClientConfigurationRepository.SaveAsync(client);
            _gateway.RemoveResult = false;
            var download = new DownloadBuilder()
                .WithDownloadClientConfiguration(client)
                .WithCompletedStatus(DateTime.UtcNow.AddHours(-30))
                .WithClientDownloadId("external-failure")
                .Build();
            download.Status = DownloadStatus.Moved;
            download.Metadata["CanBeRemoved"] = true;
            await _downloadRepository.AddAsync(download);
            await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithDownload(download)
                .WithCompleted(DateTime.UtcNow.AddHours(-30))
                .Build());

            await _provider.GetRequiredService<IMovedDownloadCleanupProcessor>()
                .RunCycleAsync(CancellationToken.None);

            Assert.NotNull(await _downloadRepository.GetByIdAsync(download.Id));
        }
    }
}
