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
        public async Task RunCycleAsync_DoesNotCleanupMovedDownloadWithoutImportProof()
        {
            var client = await CreateDownloadClientConfiguration();
            var download = await AddMovedDownloadAsync(client, canBeRemoved: true);

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
            var download = await AddMovedDownloadAsync(client, canBeRemoved: true);
            await AddCompletedImportJobAsync(download);

            await _provider.GetRequiredService<IMovedDownloadCleanupProcessor>()
                .RunCycleAsync(CancellationToken.None);

            Assert.NotNull(await _downloadRepository.GetByIdAsync(download.Id));
            Assert.Equal(0, _gateway.GetCallCount(nameof(_gateway.RemoveAsync)));
            var history = await GetCleanupHistoryAsync(download.Id);
            Assert.DoesNotContain(history.Records, entry => entry.EventType == HistoryEvents.CleanupRequested);
        }

        [Fact]
        public async Task RunCycleAsync_PolicyNoneRetainsRecordBeforeImportProofValidation()
        {
            var client = await CreateRemovableClientAsync("none");
            _gateway.RemoveResult = true;
            var download = await AddMovedDownloadAsync(client, canBeRemoved: true);

            await _provider.GetRequiredService<IMovedDownloadCleanupProcessor>()
                .RunCycleAsync(CancellationToken.None);

            Assert.NotNull(await _downloadRepository.GetByIdAsync(download.Id));
            Assert.Equal(0, _gateway.GetCallCount(nameof(_gateway.RemoveAsync)));
            var history = await GetCleanupHistoryAsync(download.Id);
            Assert.DoesNotContain(history.Records, entry => entry.EventType == HistoryEvents.CleanupRequested);
            Assert.DoesNotContain(history.Records, entry => entry.EventType == HistoryEvents.CleanupFailed);
        }

        [Fact]
        public async Task MovedDownloadCleanup_Nzbget_RemovesClientHistoryOnlyForMovedDownloads()
        {
            var client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithType("nzbget")
                .Build());
            client.RemoveCompletedDownloads = "remove";
            await _downloadClientConfigurationRepository.SaveAsync(client);
            _gateway.RemoveResult = true;

            var failedDownload = new DownloadBuilder()
                .WithDownloadClientConfiguration(client)
                .WithClientDownloadId("failed-history")
                .Build();
            failedDownload.Status = DownloadStatus.Failed;
            failedDownload.Metadata["CanBeRemoved"] = true;
            await _downloadRepository.AddAsync(failedDownload);
            await AddCompletedImportJobAsync(failedDownload);

            await _provider.GetRequiredService<IMovedDownloadCleanupProcessor>()
                .RunCycleAsync(CancellationToken.None);

            Assert.NotNull(await _downloadRepository.GetByIdAsync(failedDownload.Id));
            Assert.Equal(0, _gateway.GetCallCount(nameof(_gateway.RemoveAsync)));

            var movedDownload = await AddMovedDownloadAsync(
                client,
                canBeRemoved: true,
                clientDownloadId: "moved-history");
            await AddCompletedImportJobAsync(movedDownload);

            await _provider.GetRequiredService<IMovedDownloadCleanupProcessor>()
                .RunCycleAsync(CancellationToken.None);

            Assert.Null(await _downloadRepository.GetByIdAsync(movedDownload.Id));
            Assert.Equal(1, _gateway.GetCallCount(nameof(_gateway.RemoveAsync)));
        }

        [Fact]
        public async Task RunCycleAsync_SuccessfulRemovalDeletesOperationalRecordButRetainsHistory()
        {
            var client = await CreateDownloadClientConfiguration();
            client.RemoveCompletedDownloads = "remove";
            await _downloadClientConfigurationRepository.SaveAsync(client);
            _gateway.RemoveResult = true;
            var download = await AddMovedDownloadAsync(
                client,
                canBeRemoved: true,
                clientDownloadId: "external-1");
            await AddCompletedImportJobAsync(download, correlationId: "cleanup-success");

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
            var download = await AddMovedDownloadAsync(
                client,
                canBeRemoved: true,
                clientDownloadId: "external-failure");
            await AddCompletedImportJobAsync(download, completedAt: DateTime.UtcNow.AddHours(-30));

            await _provider.GetRequiredService<IMovedDownloadCleanupProcessor>()
                .RunCycleAsync(CancellationToken.None);

            Assert.NotNull(await _downloadRepository.GetByIdAsync(download.Id));
        }

        [Fact]
        public async Task RunCycleAsync_AllowsCleanupWhenLastImportedAtExistsWithoutProcessingJob()
        {
            var client = await CreateRemovableClientAsync("remove");
            _gateway.RemoveResult = true;
            var download = await AddMovedDownloadAsync(client, canBeRemoved: true);
            download.LastImportedAt = DateTime.UtcNow.AddDays(-1);
            await _downloadRepository.UpdateAsync(download);

            await _provider.GetRequiredService<IMovedDownloadCleanupProcessor>()
                .RunCycleAsync(CancellationToken.None);

            Assert.Null(await _downloadRepository.GetByIdAsync(download.Id));
            var history = await GetCleanupHistoryAsync(download.Id);
            Assert.Contains(history.Records, entry => DetailValue(entry, "ImportProof") == "LastImportedAt");
        }

        [Fact]
        public async Task RunCycleAsync_AllowsCleanupWhenImportedHistoryExistsWithoutProcessingJob()
        {
            var client = await CreateRemovableClientAsync("remove");
            _gateway.RemoveResult = true;
            var download = await AddMovedDownloadAsync(client, canBeRemoved: true);
            await AddImportedHistoryAsync(download, "import-history-proof");

            await _provider.GetRequiredService<IMovedDownloadCleanupProcessor>()
                .RunCycleAsync(CancellationToken.None);

            Assert.Null(await _downloadRepository.GetByIdAsync(download.Id));
            var history = await GetCleanupHistoryAsync(download.Id);
            Assert.Contains(history.Records, entry => DetailValue(entry, "ImportProof") == "ImportedHistory");
        }

        [Fact]
        public async Task RunCycleAsync_AllowsCleanupAfterProcessingJobRetentionWhenImportedHistoryExists()
        {
            var client = await CreateRemovableClientAsync("remove");
            _gateway.RemoveResult = true;
            var download = await AddMovedDownloadAsync(client, canBeRemoved: true);
            await AddImportedHistoryAsync(download, "retained-import-history");

            // The completed processing job has already been removed by retention cleanup.
            Assert.Empty(await _downloadProcessingJobRepository.GetByDownloadIdAsync(download.Id));

            await _provider.GetRequiredService<IMovedDownloadCleanupProcessor>()
                .RunCycleAsync(CancellationToken.None);

            Assert.Null(await _downloadRepository.GetByIdAsync(download.Id));
            Assert.Equal(1, _gateway.GetCallCount(nameof(_gateway.RemoveAsync)));
        }

        [Fact]
        public async Task RunCycleAsync_AllowsCleanupWhenLegacyDownloadHistoryShowsImported()
        {
            var client = await CreateRemovableClientAsync("remove");
            _gateway.RemoveResult = true;
            var download = await AddMovedDownloadAsync(client, canBeRemoved: true);
            await AddLegacyImportedDownloadHistoryAsync(download);

            await _provider.GetRequiredService<IMovedDownloadCleanupProcessor>()
                .RunCycleAsync(CancellationToken.None);

            Assert.Null(await _downloadRepository.GetByIdAsync(download.Id));
            var history = await GetCleanupHistoryAsync(download.Id);
            Assert.Contains(history.Records, entry => DetailValue(entry, "ImportProof") == "LegacyDownloadHistory");
        }

        [Fact]
        public async Task RunCycleAsync_AllowsNonDestructiveCleanupForOldLegacyMovedState()
        {
            var client = await CreateRemovableClientAsync("remove");
            _gateway.RemoveResult = true;
            var download = await AddMovedDownloadAsync(client, canBeRemoved: true);
            await AddGrabbedHistoryAsync(download, DateTime.UtcNow.AddDays(-8));

            await _provider.GetRequiredService<IMovedDownloadCleanupProcessor>()
                .RunCycleAsync(CancellationToken.None);

            Assert.Null(await _downloadRepository.GetByIdAsync(download.Id));
            Assert.False(_gateway.LastRemoveDeleteFiles);
            var history = await GetCleanupHistoryAsync(download.Id);
            Assert.Contains(history.Records, entry => DetailValue(entry, "ImportProof") == "LegacyMovedState");
        }

        [Fact]
        public async Task RunCycleAsync_DowngradesDeleteFilesForLegacyMovedState()
        {
            var client = await CreateRemovableClientAsync("remove_and_delete");
            _gateway.RemoveResult = true;
            var download = await AddMovedDownloadAsync(client, canBeRemoved: true);
            await AddGrabbedHistoryAsync(download, DateTime.UtcNow.AddDays(-8));

            await _provider.GetRequiredService<IMovedDownloadCleanupProcessor>()
                .RunCycleAsync(CancellationToken.None);

            Assert.Null(await _downloadRepository.GetByIdAsync(download.Id));
            Assert.Equal(1, _gateway.GetCallCount(nameof(_gateway.RemoveAsync)));
            Assert.False(_gateway.LastRemoveDeleteFiles);
        }

        [Fact]
        public async Task RunCycleAsync_DowngradesDeleteFilesWhenCompatibilityImportRetainedSource()
        {
            var client = await CreateRemovableClientAsync("remove_and_delete");
            _gateway.RemoveResult = true;
            var download = await AddMovedDownloadAsync(client, canBeRemoved: true);
            await AddCompletedImportJobAsync(
                download,
                sourceRetained: true);

            await _provider.GetRequiredService<IMovedDownloadCleanupProcessor>()
                .RunCycleAsync(CancellationToken.None);

            Assert.Null(await _downloadRepository.GetByIdAsync(download.Id));
            Assert.Equal(1, _gateway.GetCallCount(nameof(_gateway.RemoveAsync)));
            Assert.False(_gateway.LastRemoveDeleteFiles);
            var history = await GetCleanupHistoryAsync(download.Id);
            Assert.Contains(history.Records, entry =>
                DetailValue(entry, "SourceRetained") == bool.TrueString);
        }

        [Fact]
        public async Task RunCycleAsync_DowngradesDeleteFilesAfterJobRetentionWhenSourceWasRetained()
        {
            var client = await CreateRemovableClientAsync("remove_and_delete");
            _gateway.RemoveResult = true;
            var audiobook = await CreateAudiobook();
            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(audiobook)
                .WithDownloadClientConfiguration(client)
                .WithStatus(DownloadStatus.ImportPending)
                .Build());
            download.SetMetadata("CanBeRemoved", true);
            await _downloadRepository.UpdateAsync(download);

            var job = new DownloadProcessingJobBuilder()
                .WithDownload(download)
                .WithStatus(ProcessingJobStatus.Processing)
                .Build();
            job.SetCheckpoint("FilesImported");
            job.SetCheckpoint("ClientMarkedImported");
            job.SetCheckpoint("ScanEnqueued", Guid.NewGuid().ToString());
            job.JobData[Download.SourceRetainedMetadataKey] = true;
            await _downloadProcessingJobRepository.AddAsync(job);

            await _provider.GetRequiredService<IImportFinalizationService>().FinalizeAsync(
                job.Id,
                download.Id,
                audiobook.Id,
                audiobook.Title ?? download.Title,
                client.Id,
                "retained-import-after-job-retention",
                sourceRetained: true);

            var completedJob = (await _downloadProcessingJobRepository.GetByIdAsync(job.Id))!;
            completedJob.CompletedAt = DateTime.UtcNow.AddDays(-8);
            await _downloadProcessingJobRepository.UpdateAsync(completedJob);
            await _provider.GetRequiredService<IDownloadProcessingJobService>()
                .CleanupOldJobsAsync(retentionDays: 7);

            Assert.Empty(await _downloadProcessingJobRepository.GetByDownloadIdAsync(download.Id));

            var persistedDownload = (await _downloadRepository.GetByIdAsync(download.Id))!;
            persistedDownload.Metadata!.Remove(Download.SourceRetainedMetadataKey);
            await _downloadRepository.UpdateAsync(persistedDownload);

            await _provider.GetRequiredService<IMovedDownloadCleanupProcessor>()
                .RunCycleAsync(CancellationToken.None);

            Assert.Null(await _downloadRepository.GetByIdAsync(download.Id));
            Assert.False(_gateway.LastRemoveDeleteFiles);
            var history = await GetCleanupHistoryAsync(download.Id);
            Assert.Contains(history.Records, entry =>
                DetailValue(entry, "SourceRetentionKnown") == bool.TrueString &&
                DetailValue(entry, Download.SourceRetainedMetadataKey) == bool.TrueString);
        }

        [Fact]
        public async Task RunCycleAsync_DowngradesDeleteFilesAfterJobRetentionWhenDispositionIsUnknown()
        {
            var client = await CreateRemovableClientAsync("remove_and_delete");
            _gateway.RemoveResult = true;
            var download = await AddMovedDownloadAsync(client, canBeRemoved: true);
            download.LastImportedAt = DateTime.UtcNow.AddDays(-8);
            await _downloadRepository.UpdateAsync(download);

            await _provider.GetRequiredService<IMovedDownloadCleanupProcessor>()
                .RunCycleAsync(CancellationToken.None);

            Assert.False(_gateway.LastRemoveDeleteFiles);
            var history = await GetCleanupHistoryAsync(download.Id);
            Assert.Contains(history.Records, entry =>
                DetailValue(entry, "SourceRetentionKnown") == bool.FalseString &&
                DetailValue(entry, Download.SourceRetainedMetadataKey) == null);
        }

        [Fact]
        public async Task RunCycleAsync_AllowsDeleteFilesAfterJobRetentionWhenSourceWasRemoved()
        {
            var client = await CreateRemovableClientAsync("remove_and_delete");
            _gateway.RemoveResult = true;
            var download = await AddMovedDownloadAsync(client, canBeRemoved: true);
            download.LastImportedAt = DateTime.UtcNow.AddDays(-8);
            download.SetMetadata(Download.SourceRetainedMetadataKey, false);
            await _downloadRepository.UpdateAsync(download);

            await _provider.GetRequiredService<IMovedDownloadCleanupProcessor>()
                .RunCycleAsync(CancellationToken.None);

            Assert.True(_gateway.LastRemoveDeleteFiles);
        }

        [Fact]
        public async Task RunCycleAsync_RecoversRetainedSourceFromImportedHistory()
        {
            var client = await CreateRemovableClientAsync("remove_and_delete");
            _gateway.RemoveResult = true;
            var download = await AddMovedDownloadAsync(client, canBeRemoved: true);
            await AddImportedHistoryAsync(download, "retained-import-history", sourceRetained: true);

            await _provider.GetRequiredService<IMovedDownloadCleanupProcessor>()
                .RunCycleAsync(CancellationToken.None);

            Assert.False(_gateway.LastRemoveDeleteFiles);
        }

        [Fact]
        public async Task RunCycleAsync_BlocksRecentMovedWithoutImportProof()
        {
            var client = await CreateRemovableClientAsync("remove");
            _gateway.RemoveResult = true;
            var download = await AddMovedDownloadAsync(client, canBeRemoved: true);
            await AddGrabbedHistoryAsync(download, DateTime.UtcNow.AddHours(-1));

            await _provider.GetRequiredService<IMovedDownloadCleanupProcessor>()
                .RunCycleAsync(CancellationToken.None);

            Assert.NotNull(await _downloadRepository.GetByIdAsync(download.Id));
            Assert.Equal(0, _gateway.GetCallCount(nameof(_gateway.RemoveAsync)));
        }

        [Fact]
        public async Task RunCycleAsync_DoesNotOverrideExplicitCanBeRemovedFalse()
        {
            var client = await CreateRemovableClientAsync("remove");
            _gateway.RemoveResult = true;
            var download = await AddMovedDownloadAsync(client, canBeRemoved: false);
            await AddGrabbedHistoryAsync(download, DateTime.UtcNow.AddDays(-8));

            await _provider.GetRequiredService<IMovedDownloadCleanupProcessor>()
                .RunCycleAsync(CancellationToken.None);

            Assert.NotNull(await _downloadRepository.GetByIdAsync(download.Id));
            Assert.Equal(0, _gateway.GetCallCount(nameof(_gateway.RemoveAsync)));
            var history = await GetCleanupHistoryAsync(download.Id);
            Assert.DoesNotContain(history.Records, entry => entry.EventType == HistoryEvents.CleanupRequested);
        }

        private async Task<DownloadClientConfiguration> CreateRemovableClientAsync(string policy)
        {
            var client = await CreateDownloadClientConfiguration();
            client.RemoveCompletedDownloads = policy;
            return await _downloadClientConfigurationRepository.SaveAsync(client);
        }

        private async Task<Download> AddMovedDownloadAsync(
            DownloadClientConfiguration client,
            bool? canBeRemoved,
            string clientDownloadId = "external-1")
        {
            var download = new DownloadBuilder()
                .WithDownloadClientConfiguration(client)
                .WithCompletedStatus(DateTime.UtcNow.AddMinutes(-5))
                .WithClientDownloadId(clientDownloadId)
                .Build();
            download.Status = DownloadStatus.Moved;
            if (canBeRemoved.HasValue)
            {
                download.Metadata["CanBeRemoved"] = canBeRemoved.Value;
            }

            return await _downloadRepository.AddAsync(download);
        }

        private async Task AddCompletedImportJobAsync(
            Download download,
            string? correlationId = null,
            DateTime? completedAt = null,
            bool sourceRetained = false)
        {
            var job = new DownloadProcessingJobBuilder()
                .WithDownload(download)
                .WithCompleted(completedAt ?? DateTime.UtcNow)
                .Build();
            if (!string.IsNullOrWhiteSpace(correlationId))
            {
                job.JobData["CorrelationId"] = correlationId;
            }
            job.JobData["SourceRetained"] = sourceRetained;

            await _downloadProcessingJobRepository.AddAsync(job);
        }

        private Task AddImportedHistoryAsync(
            Download download,
            string correlationId,
            bool? sourceRetained = null) =>
            _historyRepository.AddAsync(new History
            {
                AudiobookId = download.AudiobookId,
                AudiobookTitle = download.Title,
                SourceTitle = download.Title,
                DownloadId = download.Id.ToUpperInvariant(),
                DownloadClientId = download.DownloadClientId,
                EventType = HistoryEvents.Imported,
                Outcome = HistoryOutcome.Succeeded,
                Source = "Test",
                Message = "Import completed",
                Timestamp = DateTime.UtcNow.AddDays(-1),
                CorrelationId = correlationId,
                Data = sourceRetained.HasValue
                    ? JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        [Download.SourceRetainedMetadataKey] = sourceRetained.Value
                    })
                    : null
            });

        private Task AddGrabbedHistoryAsync(Download download, DateTime timestamp) =>
            _historyRepository.AddAsync(new History
            {
                AudiobookId = download.AudiobookId,
                AudiobookTitle = download.Title,
                SourceTitle = download.Title,
                DownloadId = download.Id.ToUpperInvariant(),
                DownloadClientId = download.DownloadClientId,
                EventType = HistoryEvents.Grabbed,
                Outcome = HistoryOutcome.Succeeded,
                Source = "Test",
                Message = "Download grabbed",
                Timestamp = timestamp,
                CorrelationId = download.Id.ToUpperInvariant()
            });

        private async Task AddLegacyImportedDownloadHistoryAsync(Download download)
        {
            var db = _provider.GetRequiredService<ListenArrDbContext>();
            db.DownloadHistories.Add(new DownloadHistory
            {
                DownloadId = download.Id,
                EventType = DownloadHistoryEventType.Imported,
                Status = DownloadItemStatus.Completed,
                EventDate = DateTime.UtcNow.AddDays(-1),
                ImportedAt = DateTime.UtcNow.AddDays(-1),
                WasImported = true,
                DownloadClientId = download.DownloadClientId,
                Title = download.Title
            });
            await db.SaveChangesAsync();
        }

        private Task<HistoryPage> GetCleanupHistoryAsync(string downloadId) =>
            _historyRepository.QueryAsync(new HistoryQuery
            {
                DownloadId = downloadId.ToUpperInvariant(),
                Limit = 100
            });

        private static string? DetailValue(History entry, string key)
        {
            if (string.IsNullOrWhiteSpace(entry.Data)) return null;
            using var document = JsonDocument.Parse(entry.Data);
            return document.RootElement.TryGetProperty(key, out var value)
                ? value.ToString()
                : null;
        }
    }
}
