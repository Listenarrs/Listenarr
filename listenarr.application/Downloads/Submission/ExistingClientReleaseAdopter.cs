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
    /// Handles a client refusing a torrent it already holds when Listenarr's only record of that
    /// torrent belonged to a book that has since been deleted (#1221). The client's copy is
    /// complete and its files are still on disk, so the record is pointed at the book being
    /// grabbed and queued for import again, rather than reporting a refusal nothing can clear.
    ///
    /// Only a finished (Moved) record whose book no longer exists is adopted. A record that
    /// belongs to a book that still exists means the release was grabbed for that book, and the
    /// refusal stands, as it does when there is no record at all.
    /// </summary>
    internal static class ExistingClientReleaseAdopter
    {
        public static async Task<Download?> TryAdoptAsync(
            string downloadClientId,
            string infoHash,
            int audiobookId,
            IDownloadRepository downloadRepository,
            IAudiobookRepository audiobookRepository,
            IDownloadProcessingJobService downloadProcessingJobService,
            IDownloadHistoryService downloadHistoryService,
            ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(infoHash))
            {
                return null;
            }

            var candidates = (await downloadRepository.GetByClientAsync(downloadClientId))
                .Where(d => d.Status == DownloadStatus.Moved
                    && string.Equals(d.GetMetadataString("TorrentHash"), infoHash, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.StartedAt);

            foreach (var held in candidates)
            {
                if (held.AudiobookId.HasValue && await audiobookRepository.GetByIdAsync(held.AudiobookId.Value) != null)
                {
                    continue;
                }

                var previousAudiobookId = held.AudiobookId;
                held.AudiobookId = audiobookId;
                held.Completed();
                held.CompletedAt = DateTime.UtcNow;
                held.FinalPath = string.Empty;
                held.ErrorMessage = null;
                held.ImportAttempts = 0;
                held.ImportBlockReason = null;
                held.ImportBlockMessages = null;
                held.LastImportedAt = null;

                try
                {
                    await downloadRepository.UpdateAsync(held);
                }
                catch (UniqueConstraintViolationException)
                {
                    logger.LogInformation(
                        "Concurrent duplicate download prevented for audiobook {AudiobookId} while adopting download {DownloadId}",
                        audiobookId,
                        held.Id);
                    return null;
                }

                // The monitor only enqueues an import on a status change, and a Completed record
                // that stays Completed never produces one, so queue it here.
                await downloadProcessingJobService.EnqueueAsync(held);

                try
                {
                    await downloadHistoryService.RecordGrabbedAsync(
                        held.Id, downloadClientId, held.Title, DownloadProtocol.Torrent);
                }
                catch (Exception histEx) when (histEx is not OperationCanceledException && histEx is not OutOfMemoryException && histEx is not StackOverflowException)
                {
                    logger.LogWarning(histEx, "Failed to record grabbed event in history for adopted download {DownloadId} (non-critical)", held.Id);
                }

                logger.LogInformation(
                    "Download client already holds this release; adopted download {DownloadId} (previously audiobook {PreviousAudiobookId}) for audiobook {AudiobookId} and queued it for import",
                    held.Id,
                    previousAudiobookId,
                    audiobookId);
                return held;
            }

            return null;
        }
    }
}
