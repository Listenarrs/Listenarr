/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Application.Security;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Torrents;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Adapters
{
    internal sealed class TransmissionTorrentAddPlanner
    {
        private readonly ITorrentFileDownloader _torrentFileDownloader;
        private readonly ILogger _logger;

        public TransmissionTorrentAddPlanner(ITorrentFileDownloader torrentFileDownloader, ILogger logger)
        {
            _torrentFileDownloader = torrentFileDownloader;
            _logger = logger;
        }

        public async Task<Dictionary<string, object>> BuildArgumentsAsync(
            DownloadClientConfiguration client,
            SearchResult result,
            IReadOnlyCollection<string> labels,
            CancellationToken ct)
        {
            var arguments = new Dictionary<string, object>();
            byte[]? torrentFileData = result.TorrentFileContent;
            var magnetLink = DownloadClientUriBuilder.NormalizeMagnetLink(result.MagnetLink);
            var httpTorrentUrl = NormalizeTorrentUrl(result.TorrentUrl);
            var torrentUrl = magnetLink.Length > 0 ? magnetLink : httpTorrentUrl ?? string.Empty;
            var isMagnetTarget = magnetLink.Length > 0;

            _logger.LogDebug("AddAsync entry for '{Title}': TorrentFileContent={HasContent}, MagnetLink={HasMagnet}, TorrentUrl={Url}",
                LogRedaction.SanitizeText(result.Title),
                result.TorrentFileContent != null && result.TorrentFileContent.Length > 0 ? $"{result.TorrentFileContent.Length} bytes" : "null",
                isMagnetTarget ? "yes" : "no",
                LogRedaction.SanitizeUrl(torrentUrl));

            torrentFileData = await TryPreferTorrentFileForMagnetAsync(result, torrentFileData, isMagnetTarget, httpTorrentUrl, ct);
            (torrentFileData, torrentUrl) = await TryPreDownloadTorrentFileAsync(result, torrentFileData, isMagnetTarget, httpTorrentUrl, torrentUrl, ct);

            if (torrentFileData != null && torrentFileData.Length > 0)
            {
                arguments["metainfo"] = Convert.ToBase64String(torrentFileData);
                _logger.LogDebug("Using cached torrent file data ({Bytes} bytes) for '{Title}'", torrentFileData.Length, LogRedaction.SanitizeText(result.Title));
            }
            else
            {
                if (string.IsNullOrEmpty(torrentUrl))
                {
                    throw new ArgumentException("No magnet link, torrent URL, or cached torrent file provided", nameof(result));
                }

                if (torrentUrl.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                {
                    var normalizedMagnetUrl = NormalizeMagnetUriForTransmission(torrentUrl);
                    if (!string.Equals(normalizedMagnetUrl, torrentUrl, StringComparison.Ordinal))
                    {
                        _logger.LogDebug("Normalized percent-encoded magnet link for Transmission compatibility");
                    }
                    torrentUrl = normalizedMagnetUrl;
                }

                arguments["filename"] = torrentUrl;
                _logger.LogDebug("Using torrent URL for '{Title}': {Url}", LogRedaction.SanitizeText(result.Title), LogRedaction.SanitizeUrl(torrentUrl));
            }

            if (!string.IsNullOrWhiteSpace(client.DownloadPath))
            {
                arguments["download-dir"] = client.DownloadPath;
            }

            arguments["paused"] = false;
            if (labels.Count > 0)
            {
                arguments["labels"] = labels.ToArray();
            }

            return arguments;
        }

        private async Task<byte[]?> TryPreferTorrentFileForMagnetAsync(
            SearchResult result,
            byte[]? torrentFileData,
            bool isMagnetTarget,
            string? httpTorrentUrl,
            CancellationToken ct)
        {
            if ((torrentFileData == null || torrentFileData.Length == 0) &&
                isMagnetTarget &&
                !string.IsNullOrEmpty(httpTorrentUrl))
            {
                _logger.LogDebug("Magnet link available but TorrentUrl also present — attempting .torrent pre-download from {Url} for better Transmission compatibility",
                    LogRedaction.SanitizeUrl(httpTorrentUrl));
                try
                {
                    var altResult = await _torrentFileDownloader.DownloadAsync(httpTorrentUrl, ct);
                    if (altResult.HasBytes)
                    {
                        torrentFileData = altResult.TorrentBytes;
                        _logger.LogInformation("Pre-downloaded .torrent file ({Bytes} bytes) from TorrentUrl for '{Title}' — using instead of magnet link",
                            torrentFileData!.Length, LogRedaction.SanitizeText(result.Title));
                    }
                    else
                    {
                        _logger.LogDebug("TorrentUrl pre-download did not return file data for '{Title}', will use magnet link", LogRedaction.SanitizeText(result.Title));
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogDebug(ex, "TorrentUrl pre-download failed for '{Title}', will use magnet link", LogRedaction.SanitizeText(result.Title));
                }
            }

            return torrentFileData;
        }

        private async Task<(byte[]? TorrentFileData, string? TorrentUrl)> TryPreDownloadTorrentFileAsync(
            SearchResult result,
            byte[]? torrentFileData,
            bool isMagnetTarget,
            string? httpTorrentUrl,
            string torrentUrl,
            CancellationToken ct)
        {
            if ((torrentFileData == null || torrentFileData.Length == 0) &&
                !isMagnetTarget &&
                !string.IsNullOrEmpty(httpTorrentUrl))
            {
                _logger.LogDebug("Attempting pre-download of torrent file from {Url}", LogRedaction.SanitizeUrl(httpTorrentUrl));
                try
                {
                    var downloadResult = await _torrentFileDownloader.DownloadAsync(httpTorrentUrl, ct);
                    if (downloadResult.HasBytes)
                    {
                        torrentFileData = downloadResult.TorrentBytes;
                        _logger.LogInformation("Pre-downloaded torrent file ({Bytes} bytes) for '{Title}'",
                            torrentFileData!.Length, LogRedaction.SanitizeText(result.Title));
                    }
                    else if (downloadResult.HasMagnet)
                    {
                        torrentUrl = DownloadClientUriBuilder.NormalizeMagnetLink(downloadResult.MagnetUri);
                        _logger.LogInformation("Indexer redirected to magnet link for '{Title}'", LogRedaction.SanitizeText(result.Title));
                    }
                    else
                    {
                        _logger.LogWarning("Pre-download returned no data for '{Title}', falling back to URL", LogRedaction.SanitizeText(result.Title));
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to pre-download torrent file for '{Title}', falling back to URL", LogRedaction.SanitizeText(result.Title));
                }
            }
            else if (torrentFileData == null || torrentFileData.Length == 0)
            {
                _logger.LogDebug("Skipping pre-download: torrentFileData={HasData}, torrentUrl={Url}, isMagnet={IsMagnet}",
                    torrentFileData != null && torrentFileData.Length > 0 ? "has data" : "null/empty",
                string.IsNullOrEmpty(torrentUrl) ? "(empty)" : LogRedaction.SanitizeUrl(torrentUrl),
                    torrentUrl?.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) == true ? "yes" : "no");
            }

            return (torrentFileData, torrentUrl);
        }

        private static string? NormalizeTorrentUrl(string? torrentUrl)
        {
            var trimmed = (torrentUrl ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                return null;
            }

            if (!DownloadClientUriBuilder.TryParseHttpOrHttpsAbsoluteUri(trimmed, out var torrentUri))
            {
                throw new ArgumentException("Torrent URL must be an absolute HTTP or HTTPS URL.", nameof(torrentUrl));
            }

            return torrentUri!.ToString();
        }

        private static string NormalizeMagnetUriForTransmission(string magnetUri)
        {
            var queryStart = magnetUri.IndexOf('?');
            if (queryStart < 0 || queryStart >= magnetUri.Length - 1)
            {
                return magnetUri;
            }

            var segments = magnetUri[(queryStart + 1)..].Split('&');
            var changed = false;

            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                if (string.IsNullOrEmpty(segment))
                {
                    continue;
                }

                var equalsIndex = segment.IndexOf('=');
                if (equalsIndex <= 0 || equalsIndex >= segment.Length - 1)
                {
                    continue;
                }

                var value = segment[(equalsIndex + 1)..];
                if (!value.Contains('%'))
                {
                    continue;
                }

                var decodedValue = Uri.UnescapeDataString(value);
                if (decodedValue.Contains('&') || decodedValue.Contains('#'))
                {
                    continue;
                }

                if (!string.Equals(decodedValue, value, StringComparison.Ordinal))
                {
                    segments[i] = $"{segment[..(equalsIndex + 1)]}{decodedValue}";
                    changed = true;
                }
            }

            return changed
                ? $"{magnetUri[..(queryStart + 1)]}{string.Join("&", segments)}"
                : magnetUri;
        }
    }
}
