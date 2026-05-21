namespace Listenarr.Infrastructure.Torrents
{
    /// <summary>
    /// Shared helper that pre-downloads .torrent files from HTTP(S) URLs,
    /// manually following redirects. This avoids relying on a download client's
    /// built-in HTTP client, which may not handle redirects from indexers
    /// (e.g. Prowlarr returning 301).
    /// </summary>
    public interface ITorrentFileDownloader
    {
        /// <summary>
        /// Downloads a .torrent file from the given URL, following up to 10 redirects.
        /// If the indexer redirects to a magnet link, the result will contain the magnet URI.
        /// Returns an empty result if the download fails.
        /// </summary>
        Task<TorrentDownloadResult> DownloadAsync(string torrentUrl, CancellationToken ct = default);
    }
}
