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
using System.Data.Common;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Listenarr.Tests.Features.Application.Downloads.Processing
{
    // A retry request reads the download as blocked, decides what to save, and saves it. Another
    // retry for the same download can run to completion in between, so what the request read is
    // no longer true by the time it writes. These run on SQLite because the guard is a re-read
    // inside the save's transaction, which the in-memory provider does not have, and a
    // transaction-start hook is how the other retry is made to land at an exact point.
    [Trait("Name", "DownloadProcessingJobServiceRequeueRaceTests")]
    [Trait("Category", "DownloadProcessingJob")]
    public class DownloadProcessingJobServiceRequeueRaceTests : BaseTests
    {
        private readonly TransactionStartHook _hook = new();
        private readonly SqliteFactory _factory;

        public DownloadProcessingJobServiceRequeueRaceTests()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite($"Data Source={Path.Join(FileService.GetTempPath(), "requeue-race.db")};Pooling=False")
                .AddInterceptors(_hook)
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
        [Trait("Method", "RequeueAsync")]
        [Trait("Scenario", "A retry that read the download as blocked arrives after another retry imported it")]
        public async Task RequeueAsync_AfterAnotherRetryFinishedTheImport_LeavesTheImportAlone()
        {
            // The reported probe: this request read ImportBlocked, another retry was queued and
            // imported, and only then did this one reach the requeue. Applied as read, it moved
            // the Moved download back to ImportPending and put the completed job back on the queue.
            const string id = "d-requeue-after-import";
            await AddBlockedDownloadWithFailedJobAsync(id);
            var stale = (await _downloadRepository.FindAsync(id))!.Unblock();

            await RetryAndFinishTheImportAsync(id);
            var error = await Record.ExceptionAsync(() => Service().RequeueAsync(stale));

            await AssertTheImportIsUntouchedAsync(id);
            Assert.IsType<InvalidOperationException>(error);
        }

        [Fact]
        [Trait("Method", "RequeueAsync")]
        [Trait("Scenario", "Another retry imports the download after this retry read its jobs")]
        public async Task RequeueAsync_WhenTheImportFinishesBeforeTheSaveBegins_LeavesTheImportAlone()
        {
            // The narrower window: this request has already read the jobs and chosen the failed
            // one to reuse when the other retry imports. Only a re-read inside the save's
            // transaction sees that; one made any earlier passes and saves the stale decision.
            const string id = "d-requeue-import-at-save";
            await AddBlockedDownloadWithFailedJobAsync(id);
            var stale = (await _downloadRepository.FindAsync(id))!.Unblock();

            _hook.Arm(() => RetryAndFinishTheImportAsync(id));
            var error = await Record.ExceptionAsync(() => Service().RequeueAsync(stale));

            Assert.Equal(1, _hook.Fired);
            await AssertTheImportIsUntouchedAsync(id);
            Assert.IsType<InvalidOperationException>(error);
        }

        [Fact]
        [Trait("Method", "RequeueAsync")]
        [Trait("Scenario", "Another retry's job is already running when this retry saves")]
        public async Task RequeueAsync_WhenAnotherRetryJobIsRunning_ReturnsItAndLeavesItRunning()
        {
            // Pressing Retry twice. The first request's job is claimed by the worker before the
            // second saves. Saving the second's stale copy of the job would reset a running import
            // to Pending, and the worker would run it again.
            const string id = "d-requeue-running";
            await AddBlockedDownloadWithFailedJobAsync(id);
            var stale = (await _downloadRepository.FindAsync(id))!.Unblock();

            _hook.Arm(async () =>
            {
                await RetryAsync(id);
                var queued = await _downloadProcessingJobRepository.GetActiveByDownloadIdAsync(id);
                await _downloadProcessingJobRepository.UpdateAsync(queued!.MarkAsProcessing());
            });
            var jobId = await Service().RequeueAsync(stale);

            Assert.Equal(1, _hook.Fired);
            var job = (await _downloadProcessingJobRepository.GetByDownloadIdAsync(id)).Single();
            Assert.Equal(ProcessingJobStatus.Processing, job.Status);
            Assert.NotNull(job.StartedAt);
            Assert.Equal(job.Id, jobId);
            Assert.Equal(DownloadStatus.ImportPending, (await _downloadRepository.GetByIdAsync(id))!.Status);
        }

        [Fact]
        [Trait("Method", "RequeueAsync")]
        [Trait("Scenario", "With nothing landing in between, the delayed retry is applied")]
        public async Task RequeueAsync_WhenNothingChangesBeforeTheSave_RequeuesTheJob()
        {
            // The control. The same stale read and the same hook, doing nothing: the retry must
            // still go through, so the refusals above are the re-check and not a save that never
            // lands on this provider.
            const string id = "d-requeue-unchanged";
            await AddBlockedDownloadWithFailedJobAsync(id);
            var stale = (await _downloadRepository.FindAsync(id))!.Unblock();

            _hook.Arm(() => Task.CompletedTask);
            var jobId = await Service().RequeueAsync(stale);

            Assert.Equal(1, _hook.Fired);
            var job = (await _downloadProcessingJobRepository.GetByDownloadIdAsync(id)).Single();
            Assert.Equal(job.Id, jobId);
            Assert.Equal(ProcessingJobStatus.Pending, job.Status);
            Assert.Equal(DownloadStatus.ImportPending, (await _downloadRepository.GetByIdAsync(id))!.Status);
        }

        private IDownloadProcessingJobService Service() =>
            _provider.GetRequiredService<IDownloadProcessingJobService>();

        private async Task RetryAsync(string id)
        {
            Assert.IsType<OkObjectResult>(await MockUtils.CreateDownloadsController(_provider).RetryBlockedImport(id));
        }

        private async Task RetryAndFinishTheImportAsync(string id)
        {
            await RetryAsync(id);
            var queued = await _downloadProcessingJobRepository.GetActiveByDownloadIdAsync(id);
            await _downloadProcessingJobRepository.UpdateAsync(queued!.MarkAsProcessing().MarkAsCompleted());
            await _downloadRepository.UpdateAsync((await _downloadRepository.GetByIdAsync(id))!.Imported());
        }

        private async Task AssertTheImportIsUntouchedAsync(string id)
        {
            Assert.Equal(DownloadStatus.Moved, (await _downloadRepository.GetByIdAsync(id))!.Status);
            var job = (await _downloadProcessingJobRepository.GetByDownloadIdAsync(id)).Single();
            Assert.Equal(ProcessingJobStatus.Completed, job.Status);
            Assert.Null(await _downloadProcessingJobRepository.GetActiveByDownloadIdAsync(id));
        }

        private async Task AddBlockedDownloadWithFailedJobAsync(string id)
        {
            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithId(id)
                .WithStartDate(DateTime.UtcNow.AddMinutes(-1))
                .WithBlockedStatus("RepeatedFailure")
                .WithImportAttempts(3)
                .WithBlockMessage("still failing")
                .Build());
            await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithId($"job-{id}")
                .WithDownload(download)
                .WithFailed(at: DateTime.UtcNow)
                .Build());
        }

        // Runs an action once, just before the next transaction begins, on whichever context
        // begins it. The action's own writes begin transactions too, so it disarms first.
        private sealed class TransactionStartHook : DbTransactionInterceptor
        {
            private Func<Task>? _armed;

            public int Fired { get; private set; }

            public void Arm(Func<Task> action) => _armed = action;

            public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
                DbConnection connection,
                TransactionStartingEventData eventData,
                InterceptionResult<DbTransaction> result,
                CancellationToken cancellationToken = default)
            {
                var action = Interlocked.Exchange(ref _armed, null);
                if (action != null)
                {
                    Fired++;
                    await action();
                }

                return result;
            }
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
