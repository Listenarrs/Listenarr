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
    /// <remarks>
    /// When the client first reported a held download complete is persisted on the row (see
    /// <see cref="DownloadCompletionObservation"/>), because the hold writes the row back with its
    /// pre-completion status and nothing else records that the completion happened. Kept only in
    /// memory, a restart during the hold lost it: a client that had meanwhile dropped the item was
    /// never seen to complete again, and orphan cleanup deleted the row while the files stayed on
    /// disk. After a restart the monitor resumes from the persisted observation: the window keeps
    /// counting from the first report, and a download the client no longer lists is finalized.
    /// </remarks>
    public partial class DownloadMonitorProcessor
    {
        // Downloads whose overdue hold has been logged, so the line appears once per hold rather
        // than on every incomplete poll.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _overdueHoldsLogged = new();

        /// <summary>
        /// The configured completion stability window.
        /// </summary>
        /// <remarks>
        /// Settings > Download exposes this as Download Completion Stability. It has had no reader
        /// since #535/#492 removed the old one, so until now finalization began in the same pass
        /// that first saw the client report completion.
        /// </remarks>
        private static TimeSpan GetCompletionStabilityWindow(ApplicationSettings settings) =>
            TimeSpan.FromSeconds(Math.Max(0, settings.DownloadCompletionStabilitySeconds));

        /// <summary>
        /// The downloads this pass writes back: the ones the client listed, plus any held completion
        /// it did not.
        /// </summary>
        /// <remarks>
        /// The gateway returns only the downloads the client listed, because an unlisted one has no
        /// new telemetry to write. A held completion the client stopped listing still has to reach
        /// the release, or it would stay in its pre-completion status for good.
        /// </remarks>
        private static List<Download> WithHeldDownloadsMissingFromPoll(List<Download> listed, List<Download> polled, DownloadPollReport poll) =>
            [.. listed, .. polled.Where(download => !poll.ReportedDownloadIds.Contains(download.Id) && download.GetCompletionObservedAt() != null)];

        /// <summary>
        /// Reverts the download to its previous status when its completion is held, and says so.
        /// </summary>
        /// <remarks>
        /// Hold the transition, not the update. Progress and size still persist, so the row stays
        /// current; only finalization waits. Reverting the status rather than skipping the write
        /// also means the next cycle sees the same transition again and can let it through once the
        /// window has passed. The same write persists when the client first reported the
        /// completion, which is what survives a restart. The write goes through the client-poll
        /// guard like any other, which compares the stored row with the previous snapshot, so the
        /// reverted status does not make it look stale.
        /// </remarks>
        private bool HoldIfUnsettled(Download current, Download previous, DownloadPollReport poll, ApplicationSettings settings)
        {
            if (!HoldsPreviousStatus(current, previous, poll, GetCompletionStabilityWindow(settings)))
            {
                return false;
            }

            current.SetStatus(previous.Status);
            return true;
        }

        /// <summary>
        /// Should this pass write the download back with its previous status?
        /// </summary>
        /// <remarks>
        /// A held download the client did not list has no new telemetry, so it goes to the release
        /// rather than the window: the window would read its unchanged status as the client no
        /// longer reporting it complete and discard the observation, including when the poll that
        /// left it out was incomplete. Writing its previous status back changes nothing unless the
        /// release turns it into a completion.
        /// </remarks>
        private bool HoldsPreviousStatus(Download current, Download previous, DownloadPollReport poll, TimeSpan stabilityWindow) =>
            !poll.ReportedDownloadIds.Contains(current.Id) && current.GetCompletionObservedAt() != null
                ? !ReleaseIfGoneWhileHeld(current, previous, poll.IsComplete, stabilityWindow)
                : !HasSettledAsComplete(current, previous, stabilityWindow);

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
                current.ClearCompletionObserved();
                _overdueHoldsLogged.TryRemove(current.Id, out _);
                return true;
            }

            if (stabilityWindow <= TimeSpan.Zero)
            {
                // The window can be set to zero while a download is held; drop its observation so
                // it cannot outlive the hold.
                current.ClearCompletionObserved();
                _overdueHoldsLogged.TryRemove(current.Id, out _);
                return true;
            }

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var firstSeen = current.GetCompletionObservedAt();

            // An observation from the future means the clock moved back since it was taken;
            // counting from it would hold the download for the size of that jump.
            if (firstSeen is not { } observedAt || observedAt > now)
            {
                observedAt = now;
                current.RecordCompletionObserved(now);
            }

            if (now - observedAt < stabilityWindow)
            {
                logger.LogDebug(
                    "Download {DownloadId} reported complete {Elapsed:0}s ago; holding finalization until the {Window:0}s stability window passes",
                    LogRedaction.SanitizeText(current.Id),
                    (now - observedAt).TotalSeconds,
                    stabilityWindow.TotalSeconds);
                return false;
            }

            current.ClearCompletionObserved();
            _overdueHoldsLogged.TryRemove(current.Id, out _);
            return true;
        }

        /// <summary>
        /// Lets a held download through to finalization when its client stops listing it.
        /// </summary>
        /// <remarks>
        /// A download the client drops before the window passes comes back unchanged and is never
        /// seen complete again, and orphan cleanup would then delete it as missing from the client.
        /// Without the window the import was enqueued on the first pass, so this does exactly that:
        /// it applies only to a download the client itself reported complete, and if the files went
        /// with the item, the import fails the same way it would have without the window.
        ///
        /// Only a complete poll can say the item is gone. An adapter that cannot read an item
        /// leaves it out and marks the poll incomplete, and releasing on that omission would queue
        /// the import while the client still lists the item, before the window has passed. Such a
        /// download keeps its hold and its observation until a poll accounts for every item.
        ///
        /// Only a row still waiting for its first completion is released. An observation that
        /// outlived its hold must not write Completed over a row that has moved into import,
        /// because the Completed edge would enqueue that import a second time.
        ///
        /// A hold that has outlived its window and is kept only because the poll was incomplete is
        /// logged once at Information, so an operator can see why a completed download has not
        /// moved on. Nothing about the log feeds back into the decision.
        /// </remarks>
        private bool ReleaseIfGoneWhileHeld(Download current, Download previous, bool pollIsComplete, TimeSpan stabilityWindow)
        {
            if (!IsPreCompletion(previous.Status))
            {
                current.ClearCompletionObserved();
                _overdueHoldsLogged.TryRemove(current.Id, out _);
                return false;
            }

            if (!pollIsComplete)
            {
                if (current.GetCompletionObservedAt() is { } observedAt
                    && timeProvider.GetUtcNow().UtcDateTime - observedAt >= stabilityWindow
                    && _overdueHoldsLogged.TryAdd(current.Id, 0))
                {
                    logger.LogInformation(
                        "Download {DownloadId} completion hold has passed its window but the client poll was incomplete; keeping the hold until a complete poll",
                        LogRedaction.SanitizeText(current.Id));
                }

                logger.LogDebug(
                    "Download {DownloadId} was reported complete and was left out of an incomplete poll; keeping the hold until a complete poll",
                    LogRedaction.SanitizeText(current.Id));
                return false;
            }

            logger.LogInformation(
                "Download {DownloadId} was reported complete and is no longer listed by its client; finalizing without waiting out the stability window",
                LogRedaction.SanitizeText(current.Id));
            current.ClearCompletionObserved();
            _overdueHoldsLogged.TryRemove(current.Id, out _);
            current.SetStatus(DownloadStatus.Completed);
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
