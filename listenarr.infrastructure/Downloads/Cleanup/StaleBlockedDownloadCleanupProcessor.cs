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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Downloads.Cleanup
{
    /// <summary>
    /// Removes download records that are parked in <see cref="DownloadStatus.ImportBlocked"/>
    /// but can no longer resolve to anything actionable, so they stop lingering on the Activity
    /// page after the book has been dealt with. A blocked download is reaped when:
    /// <list type="bullet">
    ///   <item>it has no associated audiobook, or</item>
    ///   <item>its audiobook no longer exists (was deleted), or</item>
    ///   <item>its audiobook already has at least one file (the book was received another way,
    ///   so the blocked import is redundant).</item>
    /// </list>
    /// A blocked download whose audiobook still exists with no files is left untouched — that one
    /// is a genuine unresolved failure the user may still want to retry.
    /// </summary>
    public class StaleBlockedDownloadCleanupProcessor(
        IServiceScopeFactory scopeFactory,
        ILogger<StaleBlockedDownloadCleanupProcessor> logger)
        : IStaleBlockedDownloadCleanupProcessor
    {
        public async Task RunCycleAsync(CancellationToken cancellationToken)
        {
            using var scope = scopeFactory.CreateScope();
            var downloadRepository = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
            var audiobookRepository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            var audiobookFileRepository = scope.ServiceProvider.GetRequiredService<IAudiobookFileRepository>();

            // GetActiveAsync deliberately excludes ImportBlocked, so read the full set and filter.
            var blocked = (await downloadRepository.GetAllAsync())
                .Where(download => download.Status == DownloadStatus.ImportBlocked)
                .ToList();

            if (blocked.Count == 0)
            {
                return;
            }

            foreach (var download in blocked)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var reason = await ResolveReapReasonAsync(
                    download,
                    audiobookRepository,
                    audiobookFileRepository,
                    cancellationToken);
                if (reason == null)
                {
                    continue;
                }

                try
                {
                    await downloadRepository.RemoveAsync(download.Id);
                    logger.LogInformation(
                        "Reaped stale ImportBlocked download {DownloadId} for audiobook {AudiobookId}: {Reason}",
                        download.Id,
                        download.AudiobookId,
                        reason);
                }
                catch (Exception ex) when (ex is not (OperationCanceledException
                    or OutOfMemoryException
                    or StackOverflowException))
                {
                    logger.LogWarning(
                        ex,
                        "Failed to reap stale ImportBlocked download {DownloadId}",
                        download.Id);
                }
            }
        }

        private static async Task<string?> ResolveReapReasonAsync(
            Download download,
            IAudiobookRepository audiobookRepository,
            IAudiobookFileRepository audiobookFileRepository,
            CancellationToken cancellationToken)
        {
            if (download.AudiobookId is not int audiobookId)
            {
                return "no associated audiobook";
            }

            var audiobook = await audiobookRepository.GetByIdAsync(audiobookId);
            if (audiobook == null)
            {
                return $"audiobook {audiobookId} no longer exists";
            }

            var files = await audiobookFileRepository.GetByAudiobookIdAsync(audiobookId, cancellationToken);
            if (files.Count > 0)
            {
                return $"audiobook {audiobookId} already has {files.Count} file(s)";
            }

            return null;
        }
    }
}
