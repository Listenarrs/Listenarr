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

namespace Listenarr.Tests.Features.Infrastructure.Downloads.Processing
{
    public sealed class DownloadProcessingJobCleanupProcessorTests : BaseTests
    {
        [Fact]
        public async Task RunCycleAsync_RemovesOldTerminalJobsAndKeepsRecentOrActiveJobs()
        {
            // Arrange
            var oldCompletedAt = DateTime.UtcNow.AddDays(-8);
            var recentCompletedAt = DateTime.UtcNow.AddDays(-1);

            await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithId("old-completed")
                .WithCompleted(oldCompletedAt)
                .Build());
            await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithId("old-failed")
                .WithFailed(oldCompletedAt)
                .Build());
            await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithId("recent-completed")
                .WithCompleted(recentCompletedAt)
                .Build());
            await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithId("recent-failed")
                .WithFailed(recentCompletedAt)
                .Build());

            // These active jobs are intentionally older than retention. Cleanup must use terminal
            // status plus CompletedAt, not age alone, or it could delete work still in progress.
            await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithId("old-pending")
                .WithPending(oldCompletedAt)
                .Build());
            await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithId("old-processing")
                .WithProcessing(oldCompletedAt)
                .Build());
            await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithId("old-retry")
                .WithStatus(ProcessingJobStatus.Retry)
                .WithCreatedAt(oldCompletedAt)
                .Build());

            // Act
            await _provider.GetRequiredService<IDownloadProcessingJobCleanupProcessor>()
                .RunCycleAsync(CancellationToken.None);

            // Assert
            Assert.Null(await _downloadProcessingJobRepository.GetByIdAsync("old-completed"));
            Assert.Null(await _downloadProcessingJobRepository.GetByIdAsync("old-failed"));
            Assert.NotNull(await _downloadProcessingJobRepository.GetByIdAsync("recent-completed"));
            Assert.NotNull(await _downloadProcessingJobRepository.GetByIdAsync("recent-failed"));
            Assert.NotNull(await _downloadProcessingJobRepository.GetByIdAsync("old-pending"));
            Assert.NotNull(await _downloadProcessingJobRepository.GetByIdAsync("old-processing"));
            Assert.NotNull(await _downloadProcessingJobRepository.GetByIdAsync("old-retry"));
        }

        [Fact]
        public async Task RunCycleAsync_RemovesMoreThanOneCleanupBatch()
        {
            // Arrange
            var oldCompletedAt = DateTime.UtcNow.AddDays(-8);
            const int oldTerminalJobCount = 1205;

            for (var i = 0; i < oldTerminalJobCount; i++)
            {
                await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                    .WithId($"old-completed-{i}")
                    .WithCompleted(oldCompletedAt)
                    .Build());
            }

            await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithId("recent-completed")
                .WithCompleted(DateTime.UtcNow.AddHours(-1))
                .Build());
            await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithId("old-pending")
                .WithPending(oldCompletedAt)
                .Build());
            await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithId("old-retry")
                .WithStatus(ProcessingJobStatus.Retry)
                .WithCreatedAt(oldCompletedAt)
                .Build());

            // Act
            await _provider.GetRequiredService<IDownloadProcessingJobCleanupProcessor>()
                .RunCycleAsync(CancellationToken.None);

            // Assert
            var remainingJobs = await _downloadProcessingJobRepository.GetRecentAsync(oldTerminalJobCount + 3);
            Assert.DoesNotContain(remainingJobs, job => job.Id.StartsWith("old-completed-", StringComparison.Ordinal));
            Assert.NotNull(await _downloadProcessingJobRepository.GetByIdAsync("recent-completed"));
            Assert.NotNull(await _downloadProcessingJobRepository.GetByIdAsync("old-pending"));
            Assert.NotNull(await _downloadProcessingJobRepository.GetByIdAsync("old-retry"));
        }

        [Fact]
        public async Task RunCycleAsync_WhenNoEligibleJobs_DoesNothing()
        {
            // Arrange
            await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithId("recent-completed")
                .WithCompleted(DateTime.UtcNow.AddHours(-1))
                .Build());
            await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithId("active-pending")
                .WithPending(DateTime.UtcNow.AddDays(-30))
                .Build());

            // Act
            await _provider.GetRequiredService<IDownloadProcessingJobCleanupProcessor>()
                .RunCycleAsync(CancellationToken.None);

            // Assert
            Assert.NotNull(await _downloadProcessingJobRepository.GetByIdAsync("recent-completed"));
            Assert.NotNull(await _downloadProcessingJobRepository.GetByIdAsync("active-pending"));
        }
    }
}
