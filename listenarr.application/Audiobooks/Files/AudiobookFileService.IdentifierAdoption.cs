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
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Files
{
    public partial class AudiobookFileService
    {
        /// <summary>
        /// Best-effort identifier adoption for a just-registered file, run inside the operation
        /// lock. Flags <paramref name="adoptedIdentifierSignal"/> when a new identifier was taken
        /// so the caller can refresh metadata after the lock releases. Never throws into the
        /// registration path.
        /// </summary>
        private async Task TryAdoptFileIdentifiersAsync(
            Audiobook audiobook,
            AudioMetadata? meta,
            string filePath,
            StrongBox<bool> adoptedIdentifierSignal,
            CancellationToken cancellationToken)
        {
            try
            {
                if (await AdoptFileIdentifiersAsync(audiobook, meta, filePath, cancellationToken))
                {
                    adoptedIdentifierSignal.Value = true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogDebug(ex, "Identifier adoption failed for added audiobook file {Path}", LogRedaction.SanitizeFilePath(filePath));
            }
        }

        /// <summary>
        /// Copies identifiers found in a scanned file's embedded tags (ASIN, ISBN) onto the
        /// audiobook when it doesn't already have them. This lets "Rescan Metadata" resolve
        /// upstream metadata for files imported with an embedded ASIN, without the user having to
        /// type the identifier by hand. Existing identifiers are never overwritten.
        ///
        /// Runs inside the per-audiobook operation lock (called from EnsureAudiobookFileCoreAsync),
        /// so the identifier write cannot race file ownership or a concurrent audiobook update.
        /// Returns true when a NEW identifier was adopted, so the caller can trigger the upstream
        /// metadata refresh AFTER the lock is released -- that network lookup must not hold the lock.
        /// </summary>
        private async Task<bool> AdoptFileIdentifiersAsync(
            Audiobook audiobook,
            AudioMetadata? meta,
            string filePath,
            CancellationToken cancellationToken)
        {
            if (meta == null)
            {
                return false;
            }

            var changed = false;

            if (string.IsNullOrWhiteSpace(audiobook.Asin))
            {
                // Only adopt when every file linked to this audiobook that carries an ASIN carries the
                // same one. Disagreement means a file was likely mis-attributed to this book, and a
                // wrongly linked file must not be allowed to donate its identifier (which the metadata
                // auto-refresh would then act on). Returns null on conflict, so nothing is adopted.
                var agreedAsin = await ResolveUnanimousFileAsinAsync(audiobook, meta, cancellationToken);
                if (!string.IsNullOrWhiteSpace(agreedAsin))
                {
                    audiobook.Asin = agreedAsin;
                    changed = true;
                }
            }

            // ISBN, like ASIN, is only adopted when the book has none -- never appended on top of an
            // existing set, so a mis-attributed file cannot accumulate a stray identifier.
            if ((audiobook.Isbn == null || audiobook.Isbn.Count == 0) && !string.IsNullOrWhiteSpace(meta.Isbn))
            {
                audiobook.Isbn = new List<string> { meta.Isbn.Trim() };
                changed = true;
            }

            if (!changed)
            {
                return false;
            }

            try
            {
                await audiobookRepository.UpdateAsync(audiobook);
                logger.LogInformation(
                    "Adopted identifiers from file tags for audiobook {AudiobookId} (ASIN set: {HasAsin})",
                    audiobook.Id,
                    !string.IsNullOrWhiteSpace(audiobook.Asin));

                await historyRepository.AddAsync(new History
                {
                    AudiobookId = audiobook.Id,
                    AudiobookTitle = audiobook.Title ?? "Unknown",
                    EventType = "Identifier Added",
                    Message = "Identifier read from embedded file tags during scan",
                    Source = "Scan",
                    Data = JsonSerializer.Serialize(new { audiobook.Asin, Isbn = audiobook.Isbn, FilePath = filePath }),
                    Timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Failed to persist adopted file identifiers for audiobook {AudiobookId}", audiobook.Id);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Returns the single ASIN shared by every ASIN-carrying file linked to this audiobook
        /// (plus the file currently being processed), or null when there is none or when the files
        /// disagree. A disagreement is treated as a sign the file-to-book attribution is wrong, so
        /// no identifier is adopted rather than picking one arbitrarily.
        ///
        /// Limitation: the agreement set is only the files linked at the moment this runs. On a
        /// first scan the first tagged file to arrive is "unanimous" simply by being the sole
        /// member, so the guard is weakest exactly when a fresh mis-attribution is most likely.
        /// It still refuses once a second, disagreeing file appears; it cannot retroactively
        /// un-adopt an ASIN taken from a lone early file that later proves to be the odd one out.
        /// </summary>
        private async Task<string?> ResolveUnanimousFileAsinAsync(
            Audiobook audiobook,
            AudioMetadata currentMeta,
            CancellationToken cancellationToken)
        {
            var asins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(currentMeta.Asin))
            {
                asins.Add(currentMeta.Asin.Trim());
            }

            List<AudiobookFile> linkedFiles;
            try
            {
                linkedFiles = await audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogDebug(ex, "Could not load linked files to verify ASIN agreement for audiobook {AudiobookId}", audiobook.Id);
                return asins.Count == 1 ? asins.First() : null;
            }

            foreach (var linked in linkedFiles)
            {
                var path = linked.Path;
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                if (!Path.IsPathRooted(path) && !string.IsNullOrWhiteSpace(audiobook.BasePath))
                {
                    path = Path.Combine(audiobook.BasePath, path);
                }

                var meta = await ExtractMetadataAsync(path, path, path);
                if (!string.IsNullOrWhiteSpace(meta?.Asin))
                {
                    asins.Add(meta!.Asin!.Trim());
                }
            }

            if (asins.Count > 1)
            {
                logger.LogInformation(
                    "Not adopting an ASIN for audiobook {AudiobookId}: its linked files carry {Count} distinct ASINs, so attribution is uncertain",
                    audiobook.Id,
                    asins.Count);
                return null;
            }

            return asins.Count == 1 ? asins.First() : null;
        }

        /// <summary>
        /// Fetches upstream metadata for a book whose ASIN was just adopted from a file tag and
        /// fills in only the fields that are still empty. Runs AFTER the operation lock is released
        /// (see AdoptFileIdentifiersAsync) so the network lookup never holds the filesystem lock.
        /// Best-effort: never throws into the scan.
        /// </summary>
        private async Task RefreshMetadataAfterAdoptionAsync(int audiobookId, CancellationToken cancellationToken)
        {
            try
            {
                var refreshed = await audiobookRepository.GetByIdSnapshotAsync(audiobookId, cancellationToken);
                if (refreshed == null || string.IsNullOrWhiteSpace(refreshed.Asin))
                {
                    return;
                }

                await metadataRefreshService.TryPopulateMissingMetadataAsync(
                    refreshed,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Auto metadata refresh after identifier adoption failed for audiobook {AudiobookId}", audiobookId);
            }
        }
    }
}
