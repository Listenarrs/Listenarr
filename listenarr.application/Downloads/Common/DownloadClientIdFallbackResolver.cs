/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads.Common
{
    internal sealed class DownloadClientIdFallbackResolver
    {
        private readonly DownloadTypeResolver _downloadTypeResolver;
        private readonly ILogger _logger;

        public DownloadClientIdFallbackResolver(DownloadTypeResolver downloadTypeResolver, ILogger logger)
        {
            _downloadTypeResolver = downloadTypeResolver;
            _logger = logger;
        }

        public string? TryResolve(DownloadClientConfiguration client, SearchResult searchResult)
        {
            if (client == null || searchResult == null || !_downloadTypeResolver.IsTorrentResult(searchResult))
            {
                return null;
            }

            var magnetHash = TryExtractMagnetHash(searchResult.MagnetLink);
            if (!string.IsNullOrWhiteSpace(magnetHash))
            {
                _logger.LogInformation(
                    "Using magnet hash fallback for download '{Title}' on client {ClientName}",
                    LogRedaction.SanitizeText(searchResult.Title),
                    LogRedaction.SanitizeText(client.Name ?? client.Id));
                return magnetHash;
            }

            return null;
        }

        private static string? TryExtractMagnetHash(string? magnetLink)
        {
            if (string.IsNullOrWhiteSpace(magnetLink))
            {
                return null;
            }

            var match = Regex.Match(magnetLink, @"xt=urn:btih:([^&]+)", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return null;
            }

            var rawHash = Uri.UnescapeDataString(match.Groups[1].Value).Trim();
            return string.IsNullOrWhiteSpace(rawHash) ? null : rawHash;
        }
    }
}
