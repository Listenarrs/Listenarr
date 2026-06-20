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

namespace Listenarr.Application.Downloads.Submission
{
    public class DownloadTypeResolver(
        IIndexerRepository indexerRepository,
        ILogger<DownloadTypeResolver> logger)
    {
        public async Task<EffectiveDownloadType> ResolveAsync(SearchResult result)
        {
            ArgumentNullException.ThrowIfNull(result);

            if (!string.IsNullOrWhiteSpace(result.NzbUrl))
            {
                logger.LogDebug("Result identified as Usenet from NzbUrl: {Title}", result.Title);
                return EffectiveDownloadType.Usenet;
            }

            if (!string.IsNullOrWhiteSpace(result.MagnetLink))
            {
                logger.LogDebug("Result identified as Torrent from MagnetLink: {Title}", result.Title);
                return EffectiveDownloadType.Torrent;
            }

            if (result.TorrentFileContent != null && result.TorrentFileContent.Length > 0)
            {
                logger.LogDebug("Result identified as Torrent from cached torrent bytes: {Title}", result.Title);
                return EffectiveDownloadType.Torrent;
            }

            if (await IsTrustedDirectDownloadAsync(result))
            {
                logger.LogDebug("Result identified as trusted DDL from configured Internet Archive indexer: {Title}", result.Title);
                return EffectiveDownloadType.DirectDownload;
            }

            if (DownloadClientUriBuilder.TryParseHttpOrHttpsAbsoluteUri(result.TorrentUrl, out _))
            {
                logger.LogDebug("Result identified as Torrent from TorrentUrl: {Title}", result.Title);
                return EffectiveDownloadType.Torrent;
            }

            logger.LogWarning(
                "Unable to derive effective download type for '{Title}'. Incoming DownloadType '{DownloadType}' was ignored because no trusted download target was present.",
                result.Title,
                result.DownloadType ?? "(null)");

            return EffectiveDownloadType.Unknown;
        }

        public bool IsTorrentResult(SearchResult result)
        {
            if (!string.IsNullOrEmpty(result.NzbUrl))
            {
                logger.LogDebug("Result identified as NZB (has NzbUrl): {Title}", result.Title);
                return false;
            }

            if (result.TorrentFileContent != null && result.TorrentFileContent.Length > 0)
            {
                logger.LogDebug("Result identified as Torrent (has cached torrent bytes): {Title}", result.Title);
                return true;
            }

            if (!string.IsNullOrEmpty(result.MagnetLink) ||
                DownloadClientUriBuilder.TryParseHttpOrHttpsAbsoluteUri(result.TorrentUrl, out _))
            {
                logger.LogDebug("Result identified as Torrent (has MagnetLink or TorrentUrl): {Title}", result.Title);
                return true;
            }

            logger.LogWarning("Unable to determine result type for '{Title}' from source '{Source}'. No MagnetLink, TorrentUrl, or NzbUrl found. Defaulting to NZB.",
                result.Title, result.Source);
            return false;
        }

        public static string GetLabel(EffectiveDownloadType effectiveDownloadType)
        {
            return effectiveDownloadType switch
            {
                EffectiveDownloadType.Torrent => "Torrent",
                EffectiveDownloadType.Usenet => "Usenet",
                EffectiveDownloadType.DirectDownload => "DDL",
                _ => string.Empty
            };
        }

        private async Task<bool> IsTrustedDirectDownloadAsync(SearchResult result)
        {
            if (result?.IndexerId is not int indexerId || indexerId <= 0)
            {
                return false;
            }

            if (!DownloadClientUriBuilder.TryParseHttpOrHttpsAbsoluteUri(result.TorrentUrl, out var downloadUri) ||
                downloadUri == null)
            {
                return false;
            }

            if (!IsTrustedArchiveOrgHost(downloadUri) ||
                !downloadUri.AbsolutePath.StartsWith("/download/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                var indexer = await indexerRepository.GetByIdAsync(indexerId);

                if (indexer == null || !indexer.IsEnabled)
                {
                    logger.LogDebug(
                        "Direct-download validation rejected '{Title}': indexer {IndexerId} was missing or disabled",
                        result.Title,
                        indexerId);
                    return false;
                }

                if (!string.Equals(indexer.Implementation, "InternetArchive", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogDebug(
                        "Direct-download validation rejected '{Title}': indexer {IndexerId} implementation was {Implementation}",
                        result.Title,
                        indexerId,
                        indexer.Implementation);
                    return false;
                }

                if (!Uri.TryCreate(indexer.Url, UriKind.Absolute, out var indexerUri) ||
                    !IsTrustedArchiveOrgHost(indexerUri))
                {
                    logger.LogDebug(
                        "Direct-download validation rejected '{Title}': configured indexer URL '{IndexerUrl}' is not a trusted archive.org host",
                        result.Title,
                        indexer.Url);
                    return false;
                }

                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(
                    ex,
                    "Failed to validate direct-download route for '{Title}' against configured indexer {IndexerId}",
                    result.Title,
                    indexerId);
                return false;
            }
        }

        private static bool IsTrustedArchiveOrgHost(Uri uri)
        {
            var host = uri.Host.Trim();
            return host.Equals("archive.org", StringComparison.OrdinalIgnoreCase) ||
                   host.EndsWith(".archive.org", StringComparison.OrdinalIgnoreCase);
        }
    }
}
