namespace Listenarr.Infrastructure.DownloadClients.Common
{
    public class AdapterUtils
    {
        /// <summary>
        /// Used for old adapter implementation.
        /// Returns a download updated with the given values.
        /// </summary>
        public static Download MapDownloadProgress(Download download, double progress, long amountLeft, string clientState)
        {
            var normalizedState = (clientState ?? string.Empty).ToLowerInvariant();

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

            long downloadedSize = download.TotalSize > 0 ? (long)(download.TotalSize * progress / 100) : 0;

            download.Progress = (decimal)progress;
            download.DownloadedSize = downloadedSize;
            download.Metadata ??= new Dictionary<string, object>();
            download.Metadata!["ClientState"] = clientState ?? "Unknown";
            download.Metadata!["AmountLeft"] = amountLeft;

            if (download.Status == DownloadStatus.Failed && mappedStatus != DownloadStatus.Failed)
            {
                var incomingProgress = (decimal)progress;

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
                download.SetStatus(mappedStatus);
            }

            return download;
        }
    }
}
