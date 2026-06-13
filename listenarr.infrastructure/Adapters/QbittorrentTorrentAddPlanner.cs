/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using BencodeNET.Parsing;
using BencodeNET.Torrents;
using Listenarr.Application.Security;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Torrents;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Adapters
{
    internal sealed class QbittorrentTorrentAddPlanner
    {
        private readonly ITorrentFileDownloader _torrentFileDownloader;
        private readonly ILogger _logger;

        public QbittorrentTorrentAddPlanner(ITorrentFileDownloader torrentFileDownloader, ILogger logger)
        {
            _torrentFileDownloader = torrentFileDownloader;
            _logger = logger;
        }

        public async Task<QbittorrentTorrentAddPlan?> CreateAsync(
            DownloadClientConfiguration client,
            SearchResult result,
            CancellationToken ct)
        {
            var magnetLink = DownloadClientUriBuilder.NormalizeMagnetLink(result.MagnetLink);
            var httpTorrentUrl = NormalizeTorrentUrl(result.TorrentUrl);
            var torrentFileData = result.TorrentFileContent;

            if (torrentFileData == null && !string.IsNullOrEmpty(httpTorrentUrl))
            {
                var downloadResult = await _torrentFileDownloader.DownloadAsync(httpTorrentUrl, ct);
                if (downloadResult.TorrentBytes != null)
                {
                    torrentFileData = downloadResult.TorrentBytes;
                    _logger.LogInformation($"Pre-downloaded torrent file ({torrentFileData!.Length} bytes) for '{LogRedaction.SanitizeText(result.Title)}'");
                }
                else if (downloadResult.HasMagnet && string.IsNullOrEmpty(magnetLink))
                {
                    magnetLink = DownloadClientUriBuilder.NormalizeMagnetLink(downloadResult.MagnetUri);
                    _logger.LogInformation($"Indexer redirected to magnet link for '{LogRedaction.SanitizeText(result.Title)}'");
                }
            }

            if (torrentFileData == null && string.IsNullOrEmpty(httpTorrentUrl) && string.IsNullOrEmpty(magnetLink))
            {
                _logger.LogError($"No torrent URL, no magnet link and no torrent file given, nothing can be added for search result {result.Title}");
                return null;
            }

            var hash = GetTorrentHash(result, torrentFileData, magnetLink);
            if (string.IsNullOrEmpty(hash))
            {
                _logger.LogError($"Unable to compute hash for the given torrent: {result.Title} with torrent URL: {result.TorrentUrl} and magnet link: {result.MagnetLink}");
                return null;
            }

            var category = client.Settings?.TryGetValue("category", out var categoryObj) is true
                ? categoryObj?.ToString()
                : null;
            var tags = client.Settings?.TryGetValue("tags", out var tagsObj) is true
                ? tagsObj?.ToString()
                : null;

            return new QbittorrentTorrentAddPlan(
                hash,
                client.DownloadPath ?? string.Empty,
                category,
                tags,
                torrentFileData,
                magnetLink,
                httpTorrentUrl);
        }

        private static string? GetTorrentHash(SearchResult result, byte[]? torrentFileData, string? magnetLink)
        {
            if (torrentFileData != null)
            {
                using var stream = new MemoryStream(torrentFileData);
                var parser = new BencodeParser();
                var torrent = parser.Parse<Torrent>(stream);
                return torrent.GetInfoHash();
            }

            return !string.IsNullOrEmpty(magnetLink)
                ? TryExtractMagnetHash(magnetLink)
                : null;
        }

        private static string? TryExtractMagnetHash(string? torrentUrl)
        {
            if (string.IsNullOrEmpty(torrentUrl) ||
                !torrentUrl.Contains("xt=urn:btih:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var start = torrentUrl.IndexOf("xt=urn:btih:", StringComparison.OrdinalIgnoreCase) + "xt=urn:btih:".Length;
            var end = torrentUrl.IndexOf('&', start);
            if (end == -1) end = torrentUrl.Length;
            return torrentUrl[start..end].ToLowerInvariant();
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
    }

    internal sealed record QbittorrentTorrentAddPlan(
        string Hash,
        string SavePath,
        string? Category,
        string? Tags,
        byte[]? TorrentFileData,
        string? MagnetLink,
        string? HttpTorrentUrl);
}
