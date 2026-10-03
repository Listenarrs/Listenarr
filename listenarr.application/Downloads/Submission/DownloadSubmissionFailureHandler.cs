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

using Listenarr.Application.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads.Submission
{
    /// <summary>
    /// Cleans up after a submission that did not yield a trackable download. Two paths reach
    /// here and both are handled the same way. The client can refuse the release outright, and
    /// it can accept it while returning no usable identifier, which leaves us nothing to poll
    /// the queue with even though the download may well be running. Either way the provisional
    /// Download row is removed because no item we can find backs it, so a history event is the
    /// only place the attempt can be recorded. Without one the grab leaves no trace at all and
    /// the next automatic search repeats it with nothing to show the user why.
    /// </summary>
    internal static class DownloadSubmissionFailureHandler
    {
        /// <summary>
        /// Writes the failed attempt to history before the provisional row is removed. The
        /// message carried here is the exception's own, so the no-identifier case reads as the
        /// client returning no verified identifier rather than as a refusal.
        ///
        /// A <see cref="DownloadClientRejectedReleaseException"/> is the one exception this does
        /// not write. The client is refusing a release it already holds, most often because the
        /// same release also satisfies another wanted book and was grabbed for that one first.
        /// The download itself is still running; recording a DownloadFailed row would show the
        /// user a red failure next to a release that is actively downloading. Sonarr's
        /// DownloadService reaches the same decision for the same exception: trace the rejection
        /// and record nothing (Sonarr DownloadService.cs:167-171 at 76c684e09).
        /// </summary>
        public static async Task RecordRejectedSubmissionAsync(
            string downloadId,
            string downloadClientId,
            string title,
            Exception failure,
            IDownloadHistoryService downloadHistoryService,
            ILogger logger)
        {
            if (string.IsNullOrEmpty(downloadClientId))
            {
                return;
            }

            if (failure is DownloadClientRejectedReleaseException)
            {
                logger.LogDebug(
                    "Download client rejected download {DownloadId} as a possible duplicate; not recording a failed attempt",
                    downloadId);
                return;
            }

            try
            {
                // The message comes from the download client and lands in a durable, user-visible
                // row, so it goes through the same helper the rest of the codebase uses for client
                // and user supplied text. It strips newlines, which is what makes a crafted release
                // title or an HTML error page able to forge log lines, and caps the length so a
                // whole error page is not stored twice. It does not redact an absolute container
                // path, and a client that reports one in its error string will still put it here.
                await downloadHistoryService.RecordDownloadFailedAsync(
                    downloadId,
                    downloadClientId,
                    title,
                    LogRedaction.SanitizeText(failure.Message));
            }
            catch (Exception historyException) when (historyException is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                logger.LogWarning(
                    historyException,
                    "Failed to record rejected submission for download {DownloadId} in history (non-critical)",
                    downloadId);
            }
        }

        public static async Task RemoveProvisionalDownloadAsync(
            string downloadId,
            IDownloadRepository downloadRepository,
            ILogger logger)
        {
            try
            {
                await downloadRepository.RemoveAsync(downloadId);
                logger.LogInformation("Removed provisional download {DownloadId} after client submission failed", downloadId);
            }
            catch (Exception cleanupException) when (cleanupException is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                logger.LogError(cleanupException, "Failed to remove provisional download {DownloadId} after client submission failure", downloadId);
            }
        }
    }
}
