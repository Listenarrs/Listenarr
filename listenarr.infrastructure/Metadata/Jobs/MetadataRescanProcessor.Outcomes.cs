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
using Listenarr.Application.Common.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Metadata.Jobs
{
    public partial class MetadataRescanProcessor
    {
        // Runs one selected file and persists what it concluded. Cancellation is decided by the
        // cycle's token, not by the exception type: an OperationCanceledException raised while
        // the token is not cancelled (a timeout, for example) is a failed extraction.
        private async Task RescanAndRecordAsync(
            AudiobookFileRescanCandidate candidate,
            TimeProvider timeProvider,
            CancellationToken cancellationToken)
        {
            var file = candidate.File;
            MetadataRescanOutcome outcome;
            Exception? failure = null;
            try
            {
                using var releaser = await _sem.LockAsync(cancellationToken);
                outcome = await RescanCandidateAsync(file.Id, file.AudiobookId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug("Metadata rescan cancelled for file id={Id}", file.Id);
                return;
            }
            catch (ApplicationConflictException ex)
            {
                logger.LogDebug(
                    ex,
                    "Skipping metadata rescan for file id={Id}; audiobook {AudiobookId} has unresolved move state",
                    file.Id,
                    file.AudiobookId);
                outcome = MetadataRescanOutcome.MoveRecoveryBlocked;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                outcome = MetadataRescanOutcome.ExtractionFailed;
                failure = ex;
            }

            await RecordOutcomeAsync(candidate, outcome, failure, timeProvider);
        }

        // Null when the file is gone by the time its lease opens.
        private PinnedAudiobookFileRegistrationLease? OpenRegistrationLease(int fileId, string canonicalPath)
        {
            try
            {
                return PinnedAudiobookFileRegistrationLease.OpenForMetadataRead(
                    canonicalPath,
                    expectedPhysicalObjectIdentity: null);
            }
            catch (Exception ex) when (FileSystemSafety.IsProvenMissingPathException(ex))
            {
                logger.LogDebug(
                    "Skipping metadata rescan for file id={Id}; the file is no longer present",
                    fileId);
                return null;
            }
        }

        // Each outcome is written in its own scope as its file finishes, with no cancellation,
        // so a cycle stopped part way keeps what it completed. The write is a compare-and-set
        // on the counters the selection read; if another write changed them first, it is dropped.
        private async Task RecordOutcomeAsync(
            AudiobookFileRescanCandidate candidate,
            MetadataRescanOutcome outcome,
            Exception? failure,
            TimeProvider timeProvider)
        {
            var file = candidate.File;
            if (outcome == MetadataRescanOutcome.RowGone)
            {
                return;
            }

            var expectedCounts = MetadataRescanSelection.CountsOf(file);
            var recorded = false;
            try
            {
                using var outcomeScope = scopeFactory.CreateScope();
                var fileRepository = outcomeScope.ServiceProvider.GetRequiredService<IAudiobookFileRepository>();
                var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
                if (outcome == MetadataRescanOutcome.Completed)
                {
                    var record = await fileRepository.RecordMetadataRescanRefreshedAsync(
                        file.Id,
                        expectedCounts,
                        MetadataRescanSchedulePolicy.ToScheduleWrite(MetadataRescanOutcome.Incomplete, expectedCounts, nowUtc)!,
                        CancellationToken.None);
                    recorded = record != AudiobookFileRescanRefreshRecord.NotRecorded;
                    if (record == AudiobookFileRescanRefreshRecord.Incomplete)
                    {
                        outcome = MetadataRescanOutcome.Incomplete;
                    }
                }
                else
                {
                    recorded = await fileRepository.ApplyMetadataRescanScheduleAsync(
                        file.Id,
                        expectedCounts,
                        MetadataRescanSchedulePolicy.ToScheduleWrite(outcome, expectedCounts, nowUtc)!,
                        CancellationToken.None);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(
                    ex,
                    "Could not record metadata rescan outcome {Outcome} for file id={Id}",
                    outcome,
                    file.Id);
            }

            logger.LogDebug(
                "Metadata rescan recorded {Outcome} for file id={Id}: {Recorded}",
                outcome,
                file.Id,
                recorded);
            if (failure != null)
            {
                LogExtractionFailure(file, failure, firstFailure: recorded && expectedCounts.Failure == 0);
            }
        }

        // A file's first failure is a Warning and every later one a Debug, judged by the stored
        // failure count going from 0 to 1, so the rule holds across restarts.
        private void LogExtractionFailure(AudiobookFile file, Exception failure, bool firstFailure)
        {
            if (firstFailure)
            {
                logger.LogWarning(
                    failure,
                    "Failed to rescan metadata for file id={Id} path={Path}; further failures for this file are logged at Debug",
                    file.Id,
                    LogRedaction.SanitizeFilePath(file.Path));
                return;
            }

            logger.LogDebug(
                failure,
                "Failed to rescan metadata for file id={Id} path={Path}",
                file.Id,
                LogRedaction.SanitizeFilePath(file.Path));
        }
    }
}
