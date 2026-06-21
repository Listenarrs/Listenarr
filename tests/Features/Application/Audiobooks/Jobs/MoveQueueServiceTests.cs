/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Listenarr.Tests.Mocks;

namespace Listenarr.Tests.Features.Application.Audiobooks.Jobs
{
    public class MoveQueueServiceTests
    {
        [Fact]
        public async Task UpdateJobStatus_PersistsAndUpdatesInMemory()
        {
            var dbOpts = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase("test_db_movejob_" + Guid.NewGuid().ToString("N"))
                .Options;
            var db = new ListenArrDbContext(dbOpts);

            var logger = new NullLogger<MoveQueueService>();
            var persistence = new Mock<IMoveQueuePersistence>();
            persistence.Setup(store => store.GetActiveByKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string key, CancellationToken _) =>
                    db.MoveJobs.SingleOrDefault(job => job.ActiveDeduplicationKey == key));
            persistence.Setup(store => store.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, CancellationToken _) => db.MoveJobs.Find(id));
            persistence.Setup(store => store.AddAsync(It.IsAny<MoveJob>(), It.IsAny<CancellationToken>()))
                .Returns(async (MoveJob job, CancellationToken ct) =>
                {
                    db.MoveJobs.Add(job);
                    await db.SaveChangesAsync(ct);
                });
            persistence.Setup(store => store.UpdateStatusAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .Returns(async (Guid id, string status, string? error, DateTimeOffset updatedAt, CancellationToken ct) =>
                {
                    var persisted = await db.MoveJobs.FindAsync([id], ct);
                    if (persisted == null) return;
                    persisted.Status = status;
                    persisted.Error = error;
                    persisted.UpdatedAt = updatedAt.UtcDateTime;
                    await db.SaveChangesAsync(ct);
                });

            var svc = new MoveQueueService(
                logger,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System);

            // Enqueue a job (creates DB entry)
            var jobId = await svc.EnqueueMoveAsync(1, "C:\\dest\\path", "C:\\src\\path");

            // Initially the job should be queued
            var job1 = await svc.GetJobAsync(jobId);
            Assert.NotNull(job1);
            Assert.Equal("Queued", job1!.Status);

            // Update status to Processing
            await svc.UpdateJobStatusAsync(jobId, "Processing");
            var job2 = await svc.GetJobAsync(jobId);
            Assert.NotNull(job2);
            Assert.Equal("Processing", job2!.Status);

            // Verify persisted in DB
            var dbJob = await db.MoveJobs.FindAsync(jobId);
            Assert.NotNull(dbJob);
            Assert.Equal("Processing", dbJob!.Status);
        }

        [Fact]
        public async Task EnqueueMoveAsync_ConcurrentDuplicates_ReturnSingleJob()
        {
            var jobs = new List<MoveJob>();
            var sync = new object();
            var persistence = new Mock<IMoveQueuePersistence>();
            persistence.Setup(store => store.GetActiveByKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string key, CancellationToken _) =>
                {
                    lock (sync)
                    {
                        return jobs.SingleOrDefault(job => job.ActiveDeduplicationKey == key);
                    }
                });
            persistence.Setup(store => store.AddAsync(It.IsAny<MoveJob>(), It.IsAny<CancellationToken>()))
                .Returns((MoveJob job, CancellationToken _) =>
                {
                    lock (sync)
                    {
                        jobs.Add(job);
                    }

                    return Task.CompletedTask;
                });
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System);

            var ids = await Task.WhenAll(
                Enumerable.Range(0, 16)
                    .Select(_ => service.EnqueueMoveAsync(7, @"C:\Library\Book\")));

            Assert.Single(ids.Distinct());
            Assert.Single(jobs);
            persistence.Verify(
                store => store.AddAsync(It.IsAny<MoveJob>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task TerminalStatus_ReleasesDeduplicationKey_ForLaterMove()
        {
            var jobs = new List<MoveJob>();
            var persistence = CreateInMemoryPersistence(jobs);
            var service = new MoveQueueService(
                NullLogger<MoveQueueService>.Instance,
                persistence.Object,
                new NoopHubBroadcaster(),
                TimeProvider.System);

            var firstId = await service.EnqueueMoveAsync(9, "/library/book");
            await service.UpdateJobStatusAsync(firstId, "Completed");
            var secondId = await service.EnqueueMoveAsync(9, "/library/book/");

            Assert.NotEqual(firstId, secondId);
            Assert.Equal(2, jobs.Count);
            Assert.Null(jobs.Single(job => job.Id == firstId).ActiveDeduplicationKey);
            Assert.NotNull(jobs.Single(job => job.Id == secondId).ActiveDeduplicationKey);
        }

        private static Mock<IMoveQueuePersistence> CreateInMemoryPersistence(List<MoveJob> jobs)
        {
            var persistence = new Mock<IMoveQueuePersistence>();
            persistence.Setup(store => store.GetActiveByKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string key, CancellationToken _) =>
                    jobs.SingleOrDefault(job => job.ActiveDeduplicationKey == key));
            persistence.Setup(store => store.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, CancellationToken _) => jobs.SingleOrDefault(job => job.Id == id));
            persistence.Setup(store => store.AddAsync(It.IsAny<MoveJob>(), It.IsAny<CancellationToken>()))
                .Returns((MoveJob job, CancellationToken _) =>
                {
                    jobs.Add(job);
                    return Task.CompletedTask;
                });
            persistence.Setup(store => store.UpdateStatusAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<DateTimeOffset>(),
                    It.IsAny<CancellationToken>()))
                .Returns((Guid id, string status, string? error, DateTimeOffset updatedAt, CancellationToken _) =>
                {
                    var job = jobs.Single(candidate => candidate.Id == id);
                    job.Status = status;
                    job.Error = error;
                    job.UpdatedAt = updatedAt.UtcDateTime;
                    if (status is not ("Queued" or "Processing"))
                    {
                        job.ActiveDeduplicationKey = null;
                    }

                    return Task.CompletedTask;
                });
            return persistence;
        }
    }
}
