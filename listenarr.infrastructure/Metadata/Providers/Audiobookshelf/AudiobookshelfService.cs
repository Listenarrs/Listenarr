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

namespace Listenarr.Infrastructure.Metadata.Providers.Audiobookshelf
{
    /// <summary>
    /// Pulls already-known library metadata out of a user-owned Audiobookshelf instance.
    /// Audiobookshelf scans the same media folder Listenarr writes into, and either reads
    /// narrator from the file's own embedded tags or from its own metadata-provider matches,
    /// so it can carry narrator information for files added before Listenarr existed, or for
    /// books Listenarr's own matching wasn't confident about.
    /// </summary>
    public class AudiobookshelfService : IAudiobookshelfService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfigurationService _configurationService;
        private readonly ILogger<AudiobookshelfService> _logger;

        public AudiobookshelfService(
            HttpClient httpClient,
            IConfigurationService configurationService,
            ILogger<AudiobookshelfService> logger)
        {
            _httpClient = httpClient;
            _configurationService = configurationService;
            _logger = logger;
        }

        public async Task<bool> IsConfiguredAsync()
        {
            var (baseUrl, apiKey) = await GetConnectionSettingsAsync();
            return !string.IsNullOrWhiteSpace(baseUrl) && !string.IsNullOrWhiteSpace(apiKey);
        }

        public async Task<List<AudiobookshelfLibraryItem>> GetLibraryItemsAsync(CancellationToken ct = default)
        {
            var items = new List<AudiobookshelfLibraryItem>();

            var (baseUrl, apiKey) = await GetConnectionSettingsAsync();
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogInformation("Audiobookshelf integration is not configured; skipping");
                return items;
            }

            var trimmedBaseUrl = baseUrl.TrimEnd('/');

            try
            {
                var libraryIds = await GetBookLibraryIdsAsync(trimmedBaseUrl, apiKey, ct);
                foreach (var libraryId in libraryIds)
                {
                    items.AddRange(await GetItemsForLibraryAsync(trimmedBaseUrl, apiKey, libraryId, ct));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Error fetching library items from Audiobookshelf at {BaseUrl}", LogRedaction.SanitizeUrl(trimmedBaseUrl));
            }

            return items;
        }

        private async Task<List<string>> GetBookLibraryIdsAsync(string baseUrl, string apiKey, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/libraries");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Audiobookshelf /api/libraries returned status {StatusCode}", response.StatusCode);
                return [];
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var ids = new List<string>();
            if (document.RootElement.TryGetProperty("libraries", out var librariesEl) && librariesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var library in librariesEl.EnumerateArray())
                {
                    var mediaType = GetString(library, "mediaType");
                    if (!string.Equals(mediaType, "book", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var id = GetString(library, "id");
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        ids.Add(id);
                    }
                }
            }

            return ids;
        }

        private async Task<List<AudiobookshelfLibraryItem>> GetItemsForLibraryAsync(
            string baseUrl, string apiKey, string libraryId, CancellationToken ct)
        {
            var items = new List<AudiobookshelfLibraryItem>();
            const int pageSize = 100;
            var page = 0;

            while (true)
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"{baseUrl}/api/libraries/{Uri.EscapeDataString(libraryId)}/items?limit={pageSize}&page={page}");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

                using var response = await _httpClient.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Audiobookshelf /api/libraries/{LibraryId}/items returned status {StatusCode}",
                        libraryId, response.StatusCode);
                    break;
                }

                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var root = document.RootElement;

                if (!root.TryGetProperty("results", out var resultsEl) || resultsEl.ValueKind != JsonValueKind.Array)
                {
                    break;
                }

                var pageCount = 0;
                foreach (var result in resultsEl.EnumerateArray())
                {
                    pageCount++;
                    var item = ParseItem(result);
                    if (item != null)
                    {
                        items.Add(item);
                    }
                }

                var total = root.TryGetProperty("total", out var totalEl) && totalEl.ValueKind == JsonValueKind.Number
                    ? totalEl.GetInt32()
                    : items.Count;

                if (pageCount < pageSize || items.Count >= total)
                {
                    break;
                }

                page++;
            }

            return items;
        }

        private static AudiobookshelfLibraryItem? ParseItem(JsonElement result)
        {
            if (!result.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            if (!result.TryGetProperty("media", out var mediaEl) || mediaEl.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!mediaEl.TryGetProperty("metadata", out var metadataEl) || metadataEl.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new AudiobookshelfLibraryItem(
                idEl.GetString() ?? string.Empty,
                GetString(metadataEl, "title"),
                GetString(metadataEl, "authorName"),
                GetString(metadataEl, "narratorName"),
                GetString(metadataEl, "asin"),
                GetString(metadataEl, "isbn"));
        }

        private static string? GetString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        private async Task<(string? BaseUrl, string? ApiKey)> GetConnectionSettingsAsync()
        {
            var settings = await _configurationService.GetApplicationSettingsAsync();
            return (settings?.AudiobookshelfBaseUrl, settings?.AudiobookshelfApiKey);
        }
    }
}
