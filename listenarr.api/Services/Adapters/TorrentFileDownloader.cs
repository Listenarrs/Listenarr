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
using System.Net;

namespace Listenarr.Api.Services.Adapters
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

    public class TorrentFileDownloader : ITorrentFileDownloader
    {
        private readonly ILogger<TorrentFileDownloader> _logger;

        public TorrentFileDownloader(ILogger<TorrentFileDownloader> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<TorrentDownloadResult> DownloadAsync(string torrentUrl, CancellationToken ct = default)
        {
            using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            downloadCts.CancelAfter(TimeSpan.FromSeconds(60));

            // Use a dedicated handler with redirects disabled so we can follow them manually
            using var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = false
            };
            using var httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(60)
            };

            var currentUrl = torrentUrl;
            for (var hop = 0; hop < 10; hop++)
            {
                // SSRF guard: reject non-HTTP(S) schemes and embedded credentials on every hop; allow
                // private/LAN hosts because torrent indexers are commonly self-hosted on local networks.
                if (!OutboundRequestSecurity.TryValidateExternalHttpUrl(currentUrl, out var ssrfReason, allowPrivateTargets: true))
                {
                    _logger.LogWarning("Blocked SSRF attempt in torrent download (hop {Hop}): {Reason}", hop, ssrfReason);
                    return TorrentDownloadResult.Empty;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, currentUrl);
                request.Headers.Accept.ParseAdd("application/x-bittorrent, application/octet-stream, */*");
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

                var response = await httpClient.SendAsync(request, downloadCts.Token);

                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                    or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
                    or HttpStatusCode.SeeOther)
                {
                    var location = response.Headers.Location;
                    if (location == null)
                    {
                        _logger.LogWarning("Pre-download got {StatusCode} with no Location header from {Url}",
                            response.StatusCode, LogRedaction.SanitizeUrl(currentUrl));
                        return TorrentDownloadResult.Empty;
                    }

                    // Resolve relative redirects
                    var nextUri = location.IsAbsoluteUri ? location : new Uri(new Uri(currentUrl), location);
                    var nextUrl = nextUri.ToString();

                    // If the redirect target is a magnet link, return it directly — HttpClient can't fetch magnets
                    if (nextUrl.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("Pre-download resolved to magnet link from {Url} (after {Hops} hop(s))",
                            LogRedaction.SanitizeUrl(torrentUrl), hop + 1);
                        return TorrentDownloadResult.FromMagnet(nextUrl);
                    }

                    _logger.LogDebug("Pre-download following {StatusCode} redirect: {From} → {To}",
                        response.StatusCode, LogRedaction.SanitizeUrl(currentUrl), LogRedaction.SanitizeUrl(nextUrl));
                    currentUrl = nextUrl;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Pre-download failed ({StatusCode}) from {Url}",
                        response.StatusCode, LogRedaction.SanitizeUrl(currentUrl));
                    return TorrentDownloadResult.Empty;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(downloadCts.Token);
                _logger.LogDebug("Pre-download fetched {Bytes} bytes from {Url} (hops: {Hops})",
                    bytes.Length, LogRedaction.SanitizeUrl(currentUrl), hop);

                // Validate that the response is actually a .torrent file (bencoded dictionary
                // starts with 'd') rather than HTML, error pages, or other non-torrent content.
                if (bytes.Length < 2 || bytes[0] != (byte)'d')
                {
                    // Check if the response looks like HTML
                    var prefix = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 50)).TrimStart();
                    if (prefix.StartsWith("<", StringComparison.Ordinal) ||
                        prefix.StartsWith("{", StringComparison.Ordinal) ||
                        prefix.StartsWith("error", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("Pre-download returned non-torrent content ({Bytes} bytes, prefix='{Prefix}') from {Url}",
                            bytes.Length, prefix.Substring(0, Math.Min(prefix.Length, 30)), LogRedaction.SanitizeUrl(currentUrl));
                        return TorrentDownloadResult.Empty;
                    }

                    _logger.LogDebug("Pre-download response doesn't look like a .torrent file (first byte=0x{FirstByte:X2}), returning anyway",
                        bytes.Length > 0 ? bytes[0] : 0);
                }

                return TorrentDownloadResult.FromBytes(bytes);
            }

            _logger.LogWarning("Pre-download exceeded maximum redirects (10) starting from {Url}", LogRedaction.SanitizeUrl(torrentUrl));
            return TorrentDownloadResult.Empty;
        }
    }
}
