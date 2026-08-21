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
    /// Cleans up after a download the client never accepted. The provisional Download row is
    /// removed because no client item backs it, so a history event is the only place the
    /// attempt can be recorded. Without one, a rejected grab leaves no trace at all and the
    /// next automatic search repeats it with nothing to show the user why.
    /// </summary>
    internal static class DownloadSubmissionFailureHandler
    {
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
                await downloadHistoryService.RecordDownloadFailedAsync(
                    downloadId,
                    downloadClientId,
                    title,
                    failure.Message);
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
