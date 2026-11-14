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
using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Metadata.Jobs
{
    // One cycle's extraction batch, in due order, and how much of each bound the selection spent.
    public sealed record MetadataRescanSelectionResult(
        IReadOnlyList<AudiobookFileRescanCandidate> Batch,
        int RowsRead,
        int PagesRead,
        int Probes,
        int ForeignRowsDeferred);

    // Chooses the files one rescan cycle extracts. Resolve it from the cycle's scope, so the
    // repository, resolver and filesystem it uses belong to that cycle and nothing outlives it.
    // Every row it looks at either joins the batch or gets a persisted later due time, unless a
    // bound stops the cycle first; batch, page and probe bounds are separate.
    public sealed class MetadataRescanSelection(
        IAudiobookFileRepository fileRepository,
        IMoveQueueService moveQueueService,
        IAudiobookFilePathIdentityResolver pathIdentityResolver,
        IFileSystem fileSystem,
        ILogger<MetadataRescanSelection> logger)
    {
        public async Task<MetadataRescanSelectionResult> SelectAsync(
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            var hostSyntax = OperatingSystem.IsWindows()
                ? FileSystemPathSyntax.Windows
                : FileSystemPathSyntax.Unix;
            var foreignRowsDeferred = await DeferForeignSyntaxRowsAsync(
                nowUtc,
                hostSyntax,
                cancellationToken);
            var cycle = new SelectionCycle(nowUtc, hostSyntax);

            while (cycle.PagesRead < MetadataRescanSchedulePolicy.MaxPages && !cycle.BoundReached)
            {
                var page = await fileRepository.GetMetadataRescanDuePageAsync(
                    nowUtc,
                    cycle.ExcludedIds,
                    MetadataRescanSchedulePolicy.PageSize,
                    cancellationToken);
                cycle.PagesRead++;
                cycle.RowsRead += page.Count;
                foreach (var candidate in page)
                {
                    if (cycle.BoundReached)
                    {
                        break;
                    }

                    await SelectRowAsync(candidate, cycle, cancellationToken);
                }

                if (page.Count < MetadataRescanSchedulePolicy.PageSize)
                {
                    break;
                }
            }

            logger.LogDebug(
                "Rescan selection chose {Selected} files after reading {Rows} rows in {Pages} pages with {Probes} resolve-and-exists checks; {Foreign} foreign-syntax rows were deferred",
                cycle.Batch.Count,
                cycle.RowsRead,
                cycle.PagesRead,
                cycle.Probes,
                foreignRowsDeferred);
            return new MetadataRescanSelectionResult(
                cycle.Batch,
                cycle.RowsRead,
                cycle.PagesRead,
                cycle.Probes,
                foreignRowsDeferred);
        }

        // The repository write for an outcome, from the counters the row was read with. Null when
        // the outcome writes no due time: Completed is recorded through RecordMetadataRescanRefreshedAsync,
        // and RowGone and Cancelled write nothing.
        internal static AudiobookFileRescanScheduleWrite? ToScheduleWrite(
            MetadataRescanOutcome outcome,
            AudiobookFileRescanCounts current,
            DateTime nowUtc)
        {
            var change = MetadataRescanSchedulePolicy.Apply(
                outcome,
                new MetadataRescanCounters(current.Failure, current.Unavailable, current.Blocked),
                nowUtc);
            if (change is not { DueUtc: { } dueUtc } scheduled)
            {
                return null;
            }

            return new AudiobookFileRescanScheduleWrite(
                new AudiobookFileRescanCounts(
                    scheduled.Counters.Failure,
                    scheduled.Counters.Unavailable,
                    scheduled.Counters.Blocked),
                dueUtc,
                outcome.ToString());
        }

        internal static AudiobookFileRescanCounts CountsOf(AudiobookFile file) => new(
            file.MetadataRescanFailureCount,
            file.MetadataRescanUnavailableCount,
            file.MetadataRescanBlockedCount);

        // Rows whose stored syntax is foreign never reach a page; without this bounded statement
        // they would stay at the old end of the due index forever.
        private async Task<int> DeferForeignSyntaxRowsAsync(
            DateTime nowUtc,
            FileSystemPathSyntax hostSyntax,
            CancellationToken cancellationToken)
        {
            var foreignRows = await fileRepository.GetDueForeignSyntaxRescanRowsAsync(
                nowUtc,
                hostSyntax,
                MetadataRescanSchedulePolicy.MaxForeignRows,
                cancellationToken);
            return foreignRows.Count == 0
                ? 0
                : await fileRepository.DeferForeignSyntaxRescanRowsAsync(
                    foreignRows,
                    nowUtc,
                    hostSyntax,
                    cancellationToken);
        }

        private async Task SelectRowAsync(
            AudiobookFileRescanCandidate candidate,
            SelectionCycle cycle,
            CancellationToken cancellationToken)
        {
            try
            {
                var rejection = await ClassifyAsync(candidate, cycle, cancellationToken);
                if (rejection == null)
                {
                    cycle.Batch.Add(candidate);
                    cycle.ExcludedIds.Add(candidate.File.Id);
                    return;
                }

                logger.LogDebug(
                    "Rescan selection recorded {Outcome} for file id={Id}",
                    rejection.Value,
                    candidate.File.Id);
                await PersistAsync(candidate, rejection.Value, cycle.NowUtc, cancellationToken);
            }
            catch (Exception exception) when (IsSelectionFailure(exception, cancellationToken))
            {
                logger.LogWarning(
                    exception,
                    "Rescan selection failed for file id={Id}; recording {Outcome}",
                    candidate.File.Id,
                    MetadataRescanOutcome.SelectionFailed);
                await PersistSelectionFailedAsync(candidate, cycle, cancellationToken);
            }
        }

        // Null means the row joins the batch. The order matters: the host filter is pure, the
        // recovery check comes before any filesystem work, and only then is the path resolved and
        // checked, which spends one unit of the probe budget.
        private async Task<MetadataRescanOutcome?> ClassifyAsync(
            AudiobookFileRescanCandidate candidate,
            SelectionCycle cycle,
            CancellationToken cancellationToken)
        {
            var file = candidate.File;
            if (!EfAudiobookFileRepository.CanRescanOnCurrentHost(
                    file,
                    candidate.AudiobookBasePath,
                    cycle.HostSyntax))
            {
                return MetadataRescanOutcome.NotOnThisHost;
            }

            if (await BlocksFilesystemMutationAsync(file.AudiobookId, cycle, cancellationToken))
            {
                return MetadataRescanOutcome.MoveRecoveryBlocked;
            }

            // The extraction task removes non-audio rows, so they need no probe here.
            if (!FileUtils.IsAudioFile(file.Path ?? string.Empty))
            {
                return null;
            }

            cycle.Probes++;
            var identity = await pathIdentityResolver.ResolveAsync(
                new Audiobook { Id = file.AudiobookId, BasePath = candidate.AudiobookBasePath },
                file.Path!,
                cancellationToken);
            if (identity.State != PathIdentityState.Valid)
            {
                return MetadataRescanOutcome.NotOnThisHost;
            }

            return fileSystem.FileExists(identity.CanonicalPath)
                ? null
                : MetadataRescanOutcome.FileMissing;
        }

        private async Task<bool> BlocksFilesystemMutationAsync(
            int audiobookId,
            SelectionCycle cycle,
            CancellationToken cancellationToken)
        {
            if (!cycle.RecoveryBlocks.TryGetValue(audiobookId, out var blocks))
            {
                var recovery = await moveQueueService.GetRecoveryStateForAudiobookAsync(
                    audiobookId,
                    cancellationToken);
                blocks = recovery.BlocksFilesystemMutation;
                cycle.RecoveryBlocks[audiobookId] = blocks;
            }

            return blocks;
        }

        private async Task PersistAsync(
            AudiobookFileRescanCandidate candidate,
            MetadataRescanOutcome outcome,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            var expectedCounts = CountsOf(candidate.File);
            var write = ToScheduleWrite(outcome, expectedCounts, nowUtc)!;
            await fileRepository.ApplyMetadataRescanScheduleAsync(
                candidate.File.Id,
                expectedCounts,
                write,
                cancellationToken);
        }

        // A row whose SelectionFailed cannot be stored keeps its due time, so it is excluded from
        // the rest of this cycle's pages instead of being read again.
        private async Task PersistSelectionFailedAsync(
            AudiobookFileRescanCandidate candidate,
            SelectionCycle cycle,
            CancellationToken cancellationToken)
        {
            try
            {
                await PersistAsync(
                    candidate,
                    MetadataRescanOutcome.SelectionFailed,
                    cycle.NowUtc,
                    cancellationToken);
            }
            catch (Exception exception) when (IsSelectionFailure(exception, cancellationToken))
            {
                logger.LogWarning(
                    exception,
                    "Could not record {Outcome} for file id={Id}; it is skipped for the rest of this cycle",
                    MetadataRescanOutcome.SelectionFailed,
                    candidate.File.Id);
                cycle.ExcludedIds.Add(candidate.File.Id);
            }
        }

        // Cancellation is decided by the cycle's token, not by the exception type: any other
        // OperationCanceledException is a failure of this row.
        private static bool IsSelectionFailure(Exception exception, CancellationToken cancellationToken) =>
            exception is not OutOfMemoryException
            && exception is not StackOverflowException
            && !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested);

        private sealed class SelectionCycle(DateTime nowUtc, FileSystemPathSyntax hostSyntax)
        {
            public DateTime NowUtc { get; } = nowUtc;

            public FileSystemPathSyntax HostSyntax { get; } = hostSyntax;

            public List<AudiobookFileRescanCandidate> Batch { get; } = [];

            public List<int> ExcludedIds { get; } = [];

            public Dictionary<int, bool> RecoveryBlocks { get; } = [];

            public int PagesRead { get; set; }

            public int RowsRead { get; set; }

            public int Probes { get; set; }

            public bool BoundReached =>
                Batch.Count >= MetadataRescanSchedulePolicy.BatchSize
                || Probes >= MetadataRescanSchedulePolicy.MaxProbes;
        }
    }
}
