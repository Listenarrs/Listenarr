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
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Ffmpeg.Installation
{
    /// <summary>
    /// Resolves the current macOS ffprobe archive from evermeet's release info endpoint. Its
    /// download endpoints redirect to the latest build without naming a file, which
    /// <see cref="FfprobeArchiveExtractor"/> cannot dispatch on, but the info endpoint reports a
    /// versioned .zip URL.
    /// </summary>
    internal static class FfprobeEvermeetResolver
    {
        internal const string ReleaseInfoUrl = "https://evermeet.cx/ffmpeg/info/ffprobe/release";
        internal static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Resolves only the pinned macOS archive; any other URL (another platform's default or a
        /// provider override) is returned untouched without a request.
        /// </summary>
        public static async Task<string?> ResolveIfPinnedAsync(
            string? downloadUrl,
            HttpClient httpClient,
            ILogger logger,
            CancellationToken cancellationToken = default)
        {
            return downloadUrl == FfprobePlatformDefaults.MacOsFallbackUrl
                ? await ResolveAsync(httpClient, downloadUrl, logger, cancellationToken)
                : downloadUrl;
        }

        /// <summary>
        /// Returns the zip URL the release info names, or <paramref name="fallbackUrl"/> when the
        /// lookup fails, times out, is malformed, or names a URL that is not an https evermeet zip.
        /// </summary>
        public static async Task<string> ResolveAsync(
            HttpClient httpClient,
            string fallbackUrl,
            ILogger logger,
            CancellationToken cancellationToken = default)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(LookupTimeout);

                using var response = await httpClient.GetAsync(ReleaseInfoUrl, timeout.Token);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(timeout.Token);

                using var document = JsonDocument.Parse(json);
                var url = document.RootElement
                    .GetProperty("download")
                    .GetProperty("zip")
                    .GetProperty("url")
                    .GetString();

                if (IsSupportedDownloadUrl(url))
                {
                    return url!;
                }

                logger.LogWarning("Ignoring unsupported ffprobe download URL from evermeet release info; using the pinned archive");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException
                && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                logger.LogWarning(ex, "evermeet ffprobe release lookup failed; using the pinned archive");
            }

            return fallbackUrl;
        }

        private static bool IsSupportedDownloadUrl(string? url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && uri.Host.Equals("evermeet.cx", StringComparison.OrdinalIgnoreCase)
                && FfprobeArchiveExtractor.IsZipArchiveUrl(url!);
        }
    }
}
