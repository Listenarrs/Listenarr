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
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Api.Features.Downloads
{
    // Retrying a blocked import writes two rows: the download goes back to ImportPending and a
    // processing job is queued for it. The worker picks up any pending job it can see, so the job
    // must not become visible unless the download is eligible as well. These run on SQLite rather
    // than the in-memory provider because only a relational provider has the transaction that
    // makes the pair atomic, and a trigger is how a write is made to fail partway through.
    [Trait("Area", "DownloadsApi")]
    [Trait("Name", "DownloadsControllerRetryAtomicityTests")]
    [Trait("Category", "DownloadsController")]
    public class DownloadsControllerRetryAtomicityTests : BaseTests
    {
        private const string FailDownloadUpdates =
            "CREATE TRIGGER fail_download_update BEFORE UPDATE ON Downloads " +
            "BEGIN SELECT RAISE(ABORT, 'injected write failure'); END;";

        private const string FailJobInserts =
            "CREATE TRIGGER fail_job_insert BEFORE INSERT ON DownloadProcessingJobs " +
            "BEGIN SELECT RAISE(ABORT, 'injected write failure'); END;";

        private const string FailJobUpdates =
            "CREATE TRIGGER fail_job_update BEFORE UPDATE ON DownloadProcessingJobs " +
            "BEGIN SELECT RAISE(ABORT, 'injected write failure'); END;";

        private readonly SqliteFactory _factory;

        public DownloadsControllerRetryAtomicityTests()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite($"Data Source={Path.Join(FileService.GetTempPath(), "retry-import.db")};Pooling=False")
                .Options;
            _factory = new SqliteFactory(options);
            Init(builder => builder.WithSingleton<IDbContextFactory<ListenArrDbContext>>(_factory)
                .WithMocks(ServiceDescriptor.Scoped<ListenArrDbContext>(_ => _factory.CreateDbContext())));
        }

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            await using var setup = await _factory.CreateDbContextAsync();
            await setup.Database.EnsureCreatedAsync();
        }

        [Fact]
        [Trait("Method", "RetryBlockedImport")]
        [Trait("Scenario", "A failed download write leaves no runnable job behind")]
        public async Task RetryBlockedImport_WhenTheDownloadWriteFails_QueuesNoJob()
        {
            // The reported failure: the job was committed first, the download write then failed,
            // the endpoint answered 500, and the worker went on to fail the committed job because
            // the download it pointed at still read ImportBlocked.
            await AddBlockedDownloadAsync("d-retry-download-write-fails");
            await InstallTriggerAsync(FailDownloadUpdates);

            var result = await MockUtils.CreateDownloadsController(_provider)
                .RetryBlockedImport("d-retry-download-write-fails");

            Assert.Equal(500, Assert.IsType<ObjectResult>(result).StatusCode);
            Assert.Empty(await _downloadProcessingJobRepository.GetByDownloadIdAsync("d-retry-download-write-fails"));
            Assert.Equal(
                DownloadStatus.ImportBlocked,
                (await _downloadRepository.GetByIdAsync("d-retry-download-write-fails"))!.Status);
        }

        [Fact]
        [Trait("Method", "RetryBlockedImport")]
        [Trait("Scenario", "A failed job write leaves the download blocked")]
        public async Task RetryBlockedImport_WhenTheJobWriteFails_LeavesTheDownloadBlocked()
        {
            // The other half of both-or-neither. An unblocked download with no job is the stranded
            // ImportPending this endpoint exists to recover, so a failed job write must not
            // produce one either.
            await AddBlockedDownloadAsync("d-retry-job-write-fails");
            await InstallTriggerAsync(FailJobInserts);

            var result = await MockUtils.CreateDownloadsController(_provider)
                .RetryBlockedImport("d-retry-job-write-fails");

            Assert.Equal(500, Assert.IsType<ObjectResult>(result).StatusCode);
            Assert.Empty(await _downloadProcessingJobRepository.GetByDownloadIdAsync("d-retry-job-write-fails"));
            var download = await _downloadRepository.GetByIdAsync("d-retry-job-write-fails");
            Assert.Equal(DownloadStatus.ImportBlocked, download!.Status);
            Assert.Equal(3, download.ImportAttempts);
        }

        [Fact]
        [Trait("Method", "RetryBlockedImport")]
        [Trait("Scenario", "Without an injected failure both writes land on SQLite")]
        public async Task RetryBlockedImport_WithoutAFailure_CommitsTheDownloadAndTheJob()
        {
            // The control. Without it, the two tests above would also pass against an endpoint
            // that never wrote anything on this provider.
            await AddBlockedDownloadAsync("d-retry-commits");

            var ok = Assert.IsType<OkObjectResult>(await MockUtils.CreateDownloadsController(_provider)
                .RetryBlockedImport("d-retry-commits"));

            var jobId = ok.Value!.GetType().GetProperty("jobId")?.GetValue(ok.Value)?.ToString();
            var queued = await _downloadProcessingJobRepository.GetActiveByDownloadIdAsync("d-retry-commits");
            Assert.NotNull(queued);
            Assert.Equal(jobId, queued!.Id);
            Assert.Equal(ProcessingJobStatus.Pending, queued.Status);

            var download = await _downloadRepository.GetByIdAsync("d-retry-commits");
            Assert.Equal(DownloadStatus.ImportPending, download!.Status);
            Assert.Equal(0, download.ImportAttempts);
        }

        [Fact]
        [Trait("Method", "RetryBlockedImport")]
        [Trait("Scenario", "A failed update of the reused job leaves the download blocked")]
        public async Task RetryBlockedImport_WhenReusingThePreviousJobFails_LeavesTheDownloadBlocked()
        {
            // The reuse path updates the newest job and the download in one save. Failing the job
            // half must roll the download half back, or the download reads ImportPending while its
            // job is still the failed one. The pre-fix code did not take this path (it inserted a
            // job and saved the download apart), so this guards the new path and is not a
            // failing-before test.
            await AddBlockedDownloadAsync("d-retry-reuse-fails");
            await AddJobAsync("j-retry-reuse-fails", "d-retry-reuse-fails", ProcessingJobStatus.Failed);
            await InstallTriggerAsync(FailJobUpdates);

            var result = await MockUtils.CreateDownloadsController(_provider)
                .RetryBlockedImport("d-retry-reuse-fails");

            Assert.Equal(500, Assert.IsType<ObjectResult>(result).StatusCode);
            var download = await _downloadRepository.GetByIdAsync("d-retry-reuse-fails");
            Assert.Equal(DownloadStatus.ImportBlocked, download!.Status);
            Assert.Equal(3, download.ImportAttempts);
            Assert.Equal(
                ProcessingJobStatus.Failed,
                (await _downloadProcessingJobRepository.GetByDownloadIdAsync("d-retry-reuse-fails")).Single().Status);
        }

        [Fact]
        [Trait("Method", "RetryBlockedImport")]
        [Trait("Scenario", "Reusing the previous job commits both writes without a failure")]
        public async Task RetryBlockedImport_ReusingThePreviousJobWithoutAFailure_CommitsBoth()
        {
            // The control for the reuse test above: the same setup minus the trigger lands both
            // writes, so the blocked download there is the rollback and not a path that never saves.
            await AddBlockedDownloadAsync("d-retry-reuse-commits");
            await AddJobAsync("j-retry-reuse-commits", "d-retry-reuse-commits", ProcessingJobStatus.Failed);

            Assert.IsType<OkObjectResult>(await MockUtils.CreateDownloadsController(_provider)
                .RetryBlockedImport("d-retry-reuse-commits"));

            Assert.Equal(
                DownloadStatus.ImportPending,
                (await _downloadRepository.GetByIdAsync("d-retry-reuse-commits"))!.Status);
            Assert.Equal(
                ProcessingJobStatus.Pending,
                (await _downloadProcessingJobRepository.GetByDownloadIdAsync("d-retry-reuse-commits")).Single().Status);
        }

        [Fact]
        [Trait("Method", "RetryBlockedImport")]
        [Trait("Scenario", "A failed download write beside an existing active job leaves the download blocked")]
        public async Task RetryBlockedImport_WhenTheDownloadOnlySaveFails_LeavesTheDownloadBlocked()
        {
            // An active job already exists, so only the download is saved. The job is not part of
            // the write and must come through untouched. Like the reuse test this path is new, so
            // it guards rather than fails before.
            await AddBlockedDownloadAsync("d-retry-active-fails");
            await AddJobAsync("j-retry-active-fails", "d-retry-active-fails", ProcessingJobStatus.Pending);
            await InstallTriggerAsync(FailDownloadUpdates);

            var result = await MockUtils.CreateDownloadsController(_provider)
                .RetryBlockedImport("d-retry-active-fails");

            Assert.Equal(500, Assert.IsType<ObjectResult>(result).StatusCode);
            Assert.Equal(
                DownloadStatus.ImportBlocked,
                (await _downloadRepository.GetByIdAsync("d-retry-active-fails"))!.Status);
            var job = (await _downloadProcessingJobRepository.GetByDownloadIdAsync("d-retry-active-fails")).Single();
            Assert.Equal("j-retry-active-fails", job.Id);
            Assert.Equal(ProcessingJobStatus.Pending, job.Status);
        }

        [Fact]
        [Trait("Method", "RetryBlockedImport")]
        [Trait("Scenario", "Beside an existing active job the download-only save commits without a failure")]
        public async Task RetryBlockedImport_WithAnActiveJobAndNoFailure_UnblocksAndLeavesTheJob()
        {
            // The control for the download-only failure test above.
            await AddBlockedDownloadAsync("d-retry-active-commits");
            await AddJobAsync("j-retry-active-commits", "d-retry-active-commits", ProcessingJobStatus.Pending);

            Assert.IsType<OkObjectResult>(await MockUtils.CreateDownloadsController(_provider)
                .RetryBlockedImport("d-retry-active-commits"));

            Assert.Equal(
                DownloadStatus.ImportPending,
                (await _downloadRepository.GetByIdAsync("d-retry-active-commits"))!.Status);
            var job = (await _downloadProcessingJobRepository.GetByDownloadIdAsync("d-retry-active-commits")).Single();
            Assert.Equal("j-retry-active-commits", job.Id);
            Assert.Equal(ProcessingJobStatus.Pending, job.Status);
        }

        private async Task AddJobAsync(string jobId, string downloadId, ProcessingJobStatus status)
        {
            await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithId(jobId)
                .WithDownload(new DownloadBuilder().WithId(downloadId).Build())
                .WithStatus(status)
                .Build());
        }

        private async Task AddBlockedDownloadAsync(string id)
        {
            await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithId(id)
                .WithStartDate(DateTime.UtcNow.AddMinutes(-1))
                .WithBlockedStatus("RepeatedFailure")
                .WithImportAttempts(3)
                .WithBlockMessage("still failing")
                .Build());
        }

        private async Task InstallTriggerAsync(string trigger)
        {
            await using var db = await _factory.CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync(trigger);
        }

        private sealed class SqliteFactory(DbContextOptions<ListenArrDbContext> options)
            : IDbContextFactory<ListenArrDbContext>
        {
            public ListenArrDbContext CreateDbContext() => new(options);

            public Task<ListenArrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(CreateDbContext());
        }
    }
}
