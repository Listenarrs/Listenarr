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
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Images.Cache
{
    internal sealed class ImageDownloadValidator
    {
        private const int MaxImageRedirects = 5;

        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;

        public ImageDownloadValidator(HttpClient httpClient, ILogger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task<(HttpResponseMessage Response, Uri FinalUri)> DownloadWithValidatedRedirectsAsync(string imageUrl)
        {
            if (!TryValidateExternalImageUrl(imageUrl, out var validationReason))
            {
                throw new InvalidOperationException($"Blocked image URL: {validationReason}");
            }

            var currentUri = new Uri(imageUrl);
            HttpResponseMessage? response = null;

            for (var redirectCount = 0; redirectCount <= MaxImageRedirects; redirectCount++)
            {
                if (!TryValidateExternalImageUri(currentUri, out var uriValidationReason))
                {
                    response?.Dispose();
                    throw new InvalidOperationException($"Blocked image URL: {uriValidationReason}");
                }

                if (!await TryValidateResolvedExternalImageUriAsync(currentUri))
                {
                    response?.Dispose();
                    throw new InvalidOperationException("Blocked image URL: DNS resolved to private or loopback address");
                }

                response = await _httpClient.GetAsync(currentUri, HttpCompletionOption.ResponseHeadersRead);

                if (IsRedirectStatusCode(response.StatusCode))
                {
                    var location = response.Headers.Location;
                    response.Dispose();
                    if (location == null)
                    {
                        throw new InvalidOperationException("Blocked image redirect without a Location header");
                    }

                    var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                    if (!TryValidateExternalImageUri(nextUri, out var redirectValidationReason))
                    {
                        throw new InvalidOperationException($"Blocked image redirect: {redirectValidationReason}");
                    }

                    currentUri = nextUri;
                    continue;
                }

                var finalUri = response.RequestMessage?.RequestUri ?? currentUri;
                if (!TryValidateExternalImageUri(finalUri, out var finalValidationReason))
                {
                    response.Dispose();
                    throw new InvalidOperationException($"Blocked final image URL: {finalValidationReason}");
                }

                if (!await TryValidateResolvedExternalImageUriAsync(finalUri))
                {
                    response.Dispose();
                    throw new InvalidOperationException("Blocked final image URL: DNS resolved to private or loopback address");
                }

                return (response, finalUri);
            }

            response?.Dispose();
            throw new HttpRequestException($"Too many redirects while downloading image (>{MaxImageRedirects}).");
        }

        public static bool TryValidateExternalImageUrl(string imageUrl, out string reason)
        {
            reason = string.Empty;
            if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
            {
                reason = "Invalid URL format";
                return false;
            }

            return TryValidateExternalImageUri(uri, out reason);
        }

        private static bool TryValidateExternalImageUri(Uri uri, out string reason)
        {
            reason = string.Empty;

            if (!uri.IsAbsoluteUri)
            {
                reason = "URL must be absolute";
                return false;
            }

            if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            {
                reason = $"Unsupported URL scheme '{uri.Scheme}'";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(uri.UserInfo))
            {
                reason = "URLs with embedded credentials are not allowed";
                return false;
            }

            var host = uri.Host ?? string.Empty;
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            {
                reason = "Localhost or local-network hostnames are not allowed";
                return false;
            }

            if (IPAddress.TryParse(host, out var ip) && IsPrivateOrLoopback(ip))
            {
                reason = "Private or loopback IP targets are not allowed";
                return false;
            }

            return true;
        }

        private async Task<bool> TryValidateResolvedExternalImageUriAsync(Uri uri)
        {
            try
            {
                var host = uri.Host;
                if (string.IsNullOrWhiteSpace(host))
                {
                    return false;
                }

                if (IPAddress.TryParse(host, out var ip))
                {
                    return !IsPrivateOrLoopback(ip);
                }

                var addresses = await Dns.GetHostAddressesAsync(host);
                if (addresses == null || addresses.Length == 0)
                {
                    _logger.LogWarning("Blocked image URL because DNS resolution returned no addresses: {Host}", LogRedaction.SanitizeText(host));
                    return false;
                }

                var privateOrLoopback = addresses.FirstOrDefault(IsPrivateOrLoopback);
                if (privateOrLoopback != null)
                {
                    _logger.LogWarning(
                        "Blocked image URL because DNS resolved to private/loopback address. Host={Host}, Address={Address}",
                        LogRedaction.SanitizeText(host),
                        privateOrLoopback);
                    return false;
                }

                return true;
            }
            catch (SocketException ex)
            {
                _logger.LogWarning(ex, "Blocked image URL because DNS resolution failed for host {Host}", LogRedaction.SanitizeText(uri.Host));
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Blocked image URL due to unexpected DNS validation error for host {Host}", LogRedaction.SanitizeText(uri.Host));
                return false;
            }
        }

        private static bool IsRedirectStatusCode(HttpStatusCode statusCode)
        {
            return statusCode == HttpStatusCode.Moved
                || statusCode == HttpStatusCode.Redirect
                || statusCode == HttpStatusCode.RedirectMethod
                || statusCode == HttpStatusCode.TemporaryRedirect
                || (int)statusCode == 308;
        }

        private static bool IsPrivateOrLoopback(IPAddress ip)
        {
            if (ip.IsIPv4MappedToIPv6)
            {
                ip = ip.MapToIPv4();
            }

            if (IPAddress.IsLoopback(ip)) return true;

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                if (b[0] == 10) return true;
                if (b[0] == 127) return true;
                if (b[0] == 169 && b[1] == 254) return true;
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
                if (b[0] == 192 && b[1] == 168) return true;
                return false;
            }

            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
                var b = ip.GetAddressBytes();
                if (b.Length > 0 && (b[0] & 0xFE) == 0xFC) return true;
                return false;
            }

            return false;
        }
    }
}
