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
        internal readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _completionFirstSeen = new();

        /// <summary>
        /// Has this download been reported complete by the client for long enough to finalize?
        /// </summary>
        /// <remarks>
        /// Only a transition into Completed is held. A download the client has always reported as
        /// complete, and anything that is not a completion, passes straight through, so this can
        /// never stall a download that is already past this point.
        /// </remarks>
        private bool HasSettledAsComplete(Download current, Download previous, TimeSpan stabilityWindow)
        {
            if (current.Status != DownloadStatus.Completed || previous.Status == DownloadStatus.Completed)
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
    }
}
