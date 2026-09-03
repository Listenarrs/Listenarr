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

namespace Listenarr.Application.Downloads.Submission
{
    internal static class DownloadRecordFactory
    {
        public static Download CreateQueuedDownload(
            string downloadId,
            TrustedDownloadCandidate candidate,
            PreparedDownloadSubmission submission,
            DownloadClientConfiguration downloadClient,
            string downloadClientId,
            int? audiobookId)
        {
            var metadata = new Dictionary<string, object>
            {
                ["Source"] = candidate.Source,
                ["Seeders"] = candidate.Seeders ?? 0,
                ["Quality"] = candidate.Quality ?? string.Empty,
                ["Language"] = candidate.Language ?? string.Empty,
                ["DownloadType"] = submission.Protocol.ToString()
            };

            // Stamp the blocklist key the search result was grabbed under, so the failure path can
            // read it back instead of deriving one of its own from fields that have moved since.
            // TotalSize below is overwritten from the download client's queue snapshot, which is
            // how the write side and the read side ended up hashing different numbers.
            if (!string.IsNullOrWhiteSpace(candidate.ReleaseIdentifier))
            {
                metadata[ReleaseIdentity.MetadataKey] = candidate.ReleaseIdentifier;
            }

            return new Download
            {
                Id = downloadId,
                AudiobookId = audiobookId,
                Title = candidate.Title,
                Artist = candidate.Artist,
                Album = candidate.Album,
                Language = candidate.Language,
                OriginalUrl = submission.OriginalLocator,
                Progress = 0,
                TotalSize = candidate.Size,
                // The size as advertised, kept apart from TotalSize because TotalSize does not
                // survive the first queue poll. ReleaseIdentity.ForGrabbed falls back to this for
                // downloads created before the stamp above existed.
                ExpectedFileSize = candidate.Size,
                DownloadedSize = 0,
                DownloadPath = downloadClient.DownloadPath ?? string.Empty,
                FinalPath = string.Empty,
                StartedAt = DateTime.UtcNow,
                DownloadClientId = downloadClientId,
                Metadata = metadata
            };
        }
    }
}
