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
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Torrents
{
    public class TorrentFileDownloader : ITorrentFileDownloader
    {
        private const int MaxAttempts = 3;
        private const int MaxRedirects = 10;
        private readonly ILogger<TorrentFileDownloader> _logger;
        private readonly Func<HttpMessageHandler> _handlerFactory;
        private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

        public TorrentFileDownloader(ILogger<TorrentFileDownloader> logger)
            : this(
                logger,
                static () => new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.All,
                    AllowAutoRedirect = false
                },
                static (delay, cancellationToken) => Task.Delay(delay, cancellationToken))
        {
        }

        internal TorrentFileDownloader(
            ILogger<TorrentFileDownloader> logger,
            Func<HttpMessageHandler> handlerFactory,
            Func<TimeSpan, CancellationToken, Task> delayAsync)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _handlerFactory = handlerFactory ?? throw new ArgumentNullException(nameof(handlerFactory));
            _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
        }

        public async Task<TorrentDownloadResult> DownloadAsync(string torrentUrl, CancellationToken ct = default)
        {
            try
            {
                using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                downloadCts.CancelAfter(TimeSpan.FromSeconds(60));

                // Redirects remain manual so every hop passes the SSRF guard.
                using var handler = _handlerFactory();
                using var httpClient = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(60)
                };

                var currentUrl = torrentUrl;
                var hop = 0;
                var attempt = 1;
                while (hop < MaxRedirects)
                {
                    // SSRF guard: reject non-HTTP(S) schemes and embedded credentials on every hop; allow
                    // private/LAN hosts because torrent indexers are commonly self-hosted on local networks.
                    if (!OutboundRequestSecurity.TryValidateExternalHttpUrl(currentUrl, out var ssrfReason, allowPrivateTargets: true))
                    {
                        _logger.LogWarning("Blocked SSRF attempt in torrent download (hop {Hop}): {Reason}", hop, ssrfReason);
                        return TorrentDownloadResult.Failed("The torrent URL was rejected by outbound request validation.");
                    }

                    using var request = new HttpRequestMessage(HttpMethod.Get, currentUrl);
                    request.Headers.Accept.ParseAdd("application/x-bittorrent, application/octet-stream, */*");
                    request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

                    using var response = await httpClient.SendAsync(request, downloadCts.Token);

                    if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
                        or HttpStatusCode.SeeOther)
                    {
                        var location = response.Headers.Location;
                        if (location == null)
                        {
                            _logger.LogWarning("Pre-download got {StatusCode} with no Location header from {Url}",
                                response.StatusCode, LogRedaction.SanitizeUrl(currentUrl));
                            return TorrentDownloadResult.Failed("The torrent download redirected without a destination.");
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
                        hop++;
                        attempt = 1;
                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        if (IsTransient(response.StatusCode) && attempt < MaxAttempts)
                        {
                            var delay = GetRetryDelay(response.Headers.RetryAfter, attempt);
                            _logger.LogWarning(
                                "Pre-download attempt {Attempt}/{MaxAttempts} failed ({StatusCode}) from {Url}; retrying in {DelayMs}ms",
                                attempt,
                                MaxAttempts,
                                response.StatusCode,
                                LogRedaction.SanitizeUrl(currentUrl),
                                delay.TotalMilliseconds);
                            await _delayAsync(delay, downloadCts.Token);
                            attempt++;
                            continue;
                        }

                        _logger.LogWarning("Pre-download failed ({StatusCode}) from {Url}",
                            response.StatusCode, LogRedaction.SanitizeUrl(currentUrl));
                        return TorrentDownloadResult.Failed($"Torrent metadata download failed with HTTP {(int)response.StatusCode}.");
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
                            return TorrentDownloadResult.Failed("The torrent URL returned non-torrent content.");
                        }

                        _logger.LogWarning("Pre-download response is not valid bencoded torrent data (first byte=0x{FirstByte:X2})",
                            bytes.Length > 0 ? bytes[0] : 0);
                        return TorrentDownloadResult.Failed("The torrent URL returned invalid torrent data.");
                    }

                    return TorrentDownloadResult.FromBytes(bytes);
                }

                _logger.LogWarning("Pre-download exceeded maximum redirects ({MaxRedirects}) starting from {Url}", MaxRedirects, LogRedaction.SanitizeUrl(torrentUrl));
                return TorrentDownloadResult.Failed("The torrent download exceeded the redirect limit.");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("Torrent metadata download timed out for {Url}", LogRedaction.SanitizeUrl(torrentUrl));
                return TorrentDownloadResult.Failed("Torrent metadata download timed out.");
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                _logger.LogWarning(exception, "Failed to pre-download torrent file from {Url}", LogRedaction.SanitizeUrl(torrentUrl));
                return TorrentDownloadResult.Failed("Torrent metadata download failed.");
            }
        }

        private static bool IsTransient(HttpStatusCode statusCode)
        {
            return statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                || (int)statusCode >= 500;
        }

        private static TimeSpan GetRetryDelay(RetryConditionHeaderValue? retryAfter, int attempt)
        {
            var retryDelay = retryAfter?.Delta;
            if (retryDelay == null && retryAfter?.Date is DateTimeOffset retryDate)
            {
                retryDelay = retryDate - DateTimeOffset.UtcNow;
            }

            if (retryDelay is { } requestedDelay && requestedDelay > TimeSpan.Zero)
            {
                return requestedDelay > TimeSpan.FromSeconds(10)
                    ? TimeSpan.FromSeconds(10)
                    : requestedDelay;
            }

            return TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1));
        }
    }
}
