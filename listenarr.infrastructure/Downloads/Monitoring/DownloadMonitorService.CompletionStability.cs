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

namespace Listenarr.Infrastructure.Downloads.Monitoring
{
    /// <summary>
    /// The completion stability window: how long the download client must keep reporting a
    /// download as complete before finalization is allowed to start.
    ///
    /// Kept beside the monitor rather than inside it because the monitor file is already at the
    /// size the architecture tests cap production files at.
    /// </summary>
    public partial class DownloadMonitorProcessor
    {
        // When the client first reported each download complete. In memory on purpose: a restart
        // simply restarts the window, which is the safe direction, and persisting it would need a
        // column for a value that is meaningless once the transition has been let through.
        //
        // Entries are removed when the transition is let through and when the client stops
        // reporting the download as complete, but not when a download vanishes from the client
        // mid-window, so the dictionary can hold entries for downloads that no longer exist. The
        // processor is a singleton, so those survive for the life of the process. That is
        // accepted rather than swept: an entry is a string key and a DateTime, it can only be
        // added for a download the client itself reported complete during this run, and the
        // bound is therefore the number of downloads that completed and then disappeared before
        // their window elapsed, which is a handful over an uptime rather than something that
        // grows with the library. Sweeping it would mean either a second timer or walking the
        // dictionary on every cycle, both of which cost more than the leak.
        internal readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _completionFirstSeen = new();

        /// <summary>
        /// Has this download been reported complete by the client for long enough to finalize?
        /// </summary>
        /// <remarks>
        /// Only a first transition into Completed is held, which means the row's previous status
        /// has to be one finalization has not started from. A download the client has always
        /// reported as complete, one already in import, and anything that is not a completion all
        /// pass straight through, so this can never stall a download that is past this point and
        /// never changes what the monitor does to a row that has already finalized.
        /// </remarks>
        private bool HasSettledAsComplete(Download current, Download previous, TimeSpan stabilityWindow)
        {
            if (current.Status != DownloadStatus.Completed || !IsPreCompletion(previous.Status))
            {
                _completionFirstSeen.TryRemove(current.Id, out _);
                return true;
            }

            if (stabilityWindow <= TimeSpan.Zero)
            {
                return true;
            }

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var firstSeen = _completionFirstSeen.GetOrAdd(current.Id, now);
            if (now - firstSeen < stabilityWindow)
            {
                logger.LogDebug(
                    "Download {DownloadId} reported complete {Elapsed:0}s ago; holding finalization until the {Window:0}s stability window passes",
                    LogRedaction.SanitizeText(current.Id),
                    (now - firstSeen).TotalSeconds,
                    stabilityWindow.TotalSeconds);
                return false;
            }

            _completionFirstSeen.TryRemove(current.Id, out _);
            return true;
        }

        /// <summary>
        /// Is this a status a download can still make its first transition into Completed from?
        /// </summary>
        /// <remarks>
        /// The window exists to hold that first transition. Completed, ImportPending,
        /// ImportBlocked and Moved are all past it, and the repository's active query returns
        /// Completed, ImportPending and Moved rows on every cycle, so naming the set matters: the
        /// guard read as "hold a completion" while meaning "hold anything that is not already
        /// Completed", which pulled rows that are in import into the window. Those are left
        /// exactly as the monitor handled them before the window existed.
        /// </remarks>
        internal static bool IsPreCompletion(DownloadStatus status) =>
            status is DownloadStatus.Queued
                or DownloadStatus.Downloading
                or DownloadStatus.Paused
                or DownloadStatus.Processing
                or DownloadStatus.Ready
                or DownloadStatus.Failed;
    }
}
