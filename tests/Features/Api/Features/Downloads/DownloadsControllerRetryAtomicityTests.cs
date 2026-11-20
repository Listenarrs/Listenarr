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
