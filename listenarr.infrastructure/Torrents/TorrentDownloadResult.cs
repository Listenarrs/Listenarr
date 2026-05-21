namespace Listenarr.Infrastructure.Torrents
{
    /// <summary>
    /// Result of a torrent file pre-download attempt. Either contains raw .torrent bytes,
    /// a magnet URI that the indexer redirected to, or nothing (failure).
    /// </summary>
    public sealed class TorrentDownloadResult
    {
        /// <summary>Raw .torrent file bytes (non-null when download succeeded).</summary>
        public byte[]? TorrentBytes { get; init; }

        /// <summary>Magnet URI discovered via redirect (non-null when indexer redirects to magnet).</summary>
        public string? MagnetUri { get; init; }

        public bool HasBytes => TorrentBytes != null && TorrentBytes.Length > 0;
        public bool HasMagnet => !string.IsNullOrEmpty(MagnetUri);
        public bool IsEmpty => !HasBytes && !HasMagnet;

        public static TorrentDownloadResult FromBytes(byte[] bytes) => new() { TorrentBytes = bytes };
        public static TorrentDownloadResult FromMagnet(string magnetUri) => new() { MagnetUri = magnetUri };
        public static TorrentDownloadResult Empty { get; } = new();
    }
}
