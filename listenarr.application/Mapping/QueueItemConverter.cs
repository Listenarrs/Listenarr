
namespace Listenarr.Application.Mapping
{
    public class QueueItemConverter
    {
        public static Download UpdateFromQueueItem(Download download, QueueItem item)
        {
            // Import-owned states are deliberately shielded from live client telemetry.
            // The monitor still polls them so cleanup can learn whether the client item
            // may be removed, but import/finalization workers own their status and path
            // lifecycle. Replaying a stale client path/state here can make an import pick
            // up the wrong source after import has already taken ownership.
            if (download.Status is DownloadStatus.Moved or DownloadStatus.Processing or DownloadStatus.ImportPending)
            {
                UpdateImportOwnedMetadata(download, item);
                return download;
            }

            if (!string.IsNullOrEmpty(item.LocalPath))
            {
                download.DownloadPath = item.LocalPath;
            }

            if (!string.IsNullOrEmpty(item.ContentPath))
            {
                download.SetMetadata("ClientContentPath", item.ContentPath);
            }

            download.SetMetadata("CanBeRemoved", item.CanRemove);

            // Contract gate: a live queue item can be valid even when some
            // telemetry is incomplete. Treat size/downloaded bytes as trusted
            // only when the client reports a positive total size and a non-negative
            // downloaded value. Unknown byte telemetry must never look like
            // "nothing left to download."
            var hasReliableSize = item.Size > 0 && item.Downloaded >= 0;
            var amountLeft = hasReliableSize
                ? Math.Max(0, item.Size - item.Downloaded)
                : null as long?;

            // Contract gate: progress is display telemetry unless it is finite and
            // in the expected client range. Invalid progress should not overwrite
            // a previously known value or drive completion decisions.
            var hasReliableProgress = double.IsFinite(item.Progress) &&
                item.Progress >= 0 &&
                item.Progress <= 100;

            download = MapDownloadProgress(
                download,
                hasReliableProgress ? item.Progress : null,
                amountLeft,
                item.Status,
                hasReliableSize ? item.Size : null,
                hasReliableSize ? Math.Min(item.Downloaded, item.Size) : null,
                hasReliableSize);

            var normalizedState = (item.Status ?? string.Empty).ToLowerInvariant();
            if (normalizedState is "error" or "failed" or "failure" or "missingfiles" or "missing_files")
            {
                download.Failed(item.ErrorMessage ?? $"Download client state: {item.Status}");
                if (!string.IsNullOrWhiteSpace(item.ClientFailureReason))
                {
                    download.SetMetadata("ClientFailureReason", item.ClientFailureReason);
                }

                return download;
            }

            if (!string.IsNullOrWhiteSpace(item.ClientFailureReason))
            {
                download.SetMetadata("ClientFailureReason", item.ClientFailureReason);
            }

            // Contract gate: completion requires an explicit terminal client state
            // or reliable byte telemetry showing that no data remains. Progress by
            // itself is not strong enough because clients can report partial or stale
            // progress while size/downloaded bytes are unknown.
            var isExplicitCompletedState = normalizedState is "completed" or "success";
            if (isExplicitCompletedState || (hasReliableSize && amountLeft <= 0))
            {
                download.Completed();
            }

            return download;
        }

        /// <summary>
        /// Used for old adapter implementation
        /// Returns a download updated with the given values
        /// </summary>
        /// <param name="download"></param>
        /// <param name="progress"></param>
        /// <param name="amountLeft"></param>
        /// <param name="clientState"></param>
        /// <param name="totalSize"></param>
        /// <param name="downloadedSize"></param>
        /// <param name="hasReliableSize"></param>
        /// <returns></returns>
        private static Download MapDownloadProgress(
            Download download,
            double? progress,
            long? amountLeft,
            string clientState,
            long? totalSize,
            long? downloadedSize,
            bool hasReliableSize)
        {
            var normalizedState = (clientState ?? string.Empty).ToLowerInvariant();

            // Map client state to our DownloadStatus
            var mappedStatus = normalizedState switch
            {
                "downloading" => DownloadStatus.Downloading,
                "metadl" => DownloadStatus.Downloading,
                "forceddl" => DownloadStatus.Downloading,
                "stalleddl" => DownloadStatus.Downloading,
                "checkingdl" => DownloadStatus.Downloading,
                "checkingresumedata" => DownloadStatus.Downloading,
                "moving" => DownloadStatus.Downloading,
                "fetching" => DownloadStatus.Downloading,
                "scanning" => DownloadStatus.Downloading,
                "pp_queued" => DownloadStatus.Downloading,
                "pp_processing" => DownloadStatus.Downloading,
                "uploading" => DownloadStatus.Downloading,
                "stalledup" => DownloadStatus.Downloading,
                "checkingup" => DownloadStatus.Downloading,
                "forcedup" => DownloadStatus.Downloading,
                "stoppeddl" => DownloadStatus.Paused,
                "stoppedup" => DownloadStatus.Paused,
                "queueddl" => DownloadStatus.Queued,
                "queuedup" => DownloadStatus.Queued,
                "queued" => DownloadStatus.Queued,
                "paused" => DownloadStatus.Paused,
                "seeding" => DownloadStatus.Downloading,
                "success" => DownloadStatus.Completed,
                "error" => DownloadStatus.Failed,
                "failed" => DownloadStatus.Failed,
                "failure" => DownloadStatus.Failed,
                "missingfiles" => DownloadStatus.Failed,
                "missing_files" => DownloadStatus.Failed,
                _ => DownloadStatus.Queued
            };

            // Update trusted telemetry only. Unknown size/downloaded values are
            // preserved instead of converted into completed byte counts.
            if (progress.HasValue)
            {
                download.Progress = (decimal)progress.Value;
            }

            if (hasReliableSize && totalSize.HasValue && downloadedSize.HasValue && amountLeft.HasValue)
            {
                download.TotalSize = totalSize.Value;
                download.DownloadedSize = downloadedSize.Value;
            }

            download.Metadata ??= new Dictionary<string, object>();
            download.Metadata!["ClientState"] = clientState ?? "Unknown";

            // AmountLeft remains numeric for legacy consumers. Callers must check
            // HasReliableSize before treating AmountLeft == 0 as a completion signal.
            download.Metadata!["AmountLeft"] = amountLeft ?? 0;
            download.Metadata!["HasReliableSize"] = hasReliableSize;

            // Conservative guard: if the DB record is currently Failed, do not overwrite
            // the status to a non-failed value unless we have strong evidence (progress increased)
            // or the client reports Completed. This prevents transient client "error" states
            // from flipping the UI incorrectly.
            if (download.Status == DownloadStatus.Failed && mappedStatus != DownloadStatus.Failed)
            {
                var incomingProgress = progress.HasValue ? (decimal)progress.Value : download.Progress;

                // Allow transition to Completed always (finalization or client reports complete)
                if (mappedStatus == DownloadStatus.Completed)
                {
                    download.Completed();
                }
                else if (incomingProgress > download.Progress)
                {
                    download.Downloading();
                }
            }
            else if (download.Status != DownloadStatus.Completed && download.Status != DownloadStatus.Moved)
            {
                // Don't overwrite Completed/Moved status - Completed is managed by the completion
                // detection logic, and Moved means the file is already imported (we only keep
                // polling Moved downloads to update CanBeRemoved for deferred client removal).
                download.SetStatus(mappedStatus);
            }

            return download;
        }

        private static void UpdateImportOwnedMetadata(Download download, QueueItem item)
        {
            if (!string.IsNullOrEmpty(item.ContentPath))
            {
                download.SetMetadata("ClientContentPath", item.ContentPath);
            }

            download.SetMetadata("CanBeRemoved", item.CanRemove);
        }
    }
}
