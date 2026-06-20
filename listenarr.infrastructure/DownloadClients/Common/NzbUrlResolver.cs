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
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Common
{
    public class NzbUrlResolver : INzbUrlResolver
    {
        private readonly IIndexerRepository _indexers;
        private readonly ILogger<NzbUrlResolver> _logger;

        public NzbUrlResolver(IIndexerRepository indexers, ILogger<NzbUrlResolver> logger)
        {
            _indexers = indexers ?? throw new ArgumentNullException(nameof(indexers));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<(string Url, string? IndexerApiKey)> ResolveAsync(SearchResult result, CancellationToken ct = default)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            var nzbUrl = result.NzbUrl ?? string.Empty;
            if (string.IsNullOrWhiteSpace(nzbUrl))
            {
                return (nzbUrl, null);
            }

            try
            {
                var hasApiKey = false;
                if (Uri.TryCreate(nzbUrl, UriKind.Absolute, out var parsed))
                {
                    var query = QueryHelpers.ParseQuery(parsed.Query);
                    hasApiKey = query.Keys.Any(k => string.Equals(k, "apikey", StringComparison.OrdinalIgnoreCase));
                }
                else if (nzbUrl.Contains("apikey=", StringComparison.OrdinalIgnoreCase))
                {
                    hasApiKey = true;
                }

                if (hasApiKey)
                {
                    return (nzbUrl, null);
                }

                Indexer? indexer = null;
                if (result.IndexerId.HasValue)
                {
                    indexer = await _indexers.GetByIdAsync(result.IndexerId!.Value, ct);
                }
                else if (!string.IsNullOrWhiteSpace(result.Source))
                {
                    indexer = await _indexers.GetByNameAsync(result.Source, ct);
                }

                if (indexer != null && !string.IsNullOrWhiteSpace(indexer.ApiKey))
                {
                    var updatedUrl = QueryHelpers.AddQueryString(nzbUrl, "apikey", indexer.ApiKey);
                    return (updatedUrl, indexer.ApiKey);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Failed to append indexer API key to NZB URL for {Title}", result.Title);
            }

            return (nzbUrl, null);
        }
    }
}
