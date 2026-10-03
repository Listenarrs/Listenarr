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
