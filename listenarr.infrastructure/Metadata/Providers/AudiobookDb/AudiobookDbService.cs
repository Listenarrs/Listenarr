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

namespace Listenarr.Infrastructure.Metadata.Providers.AudiobookDb
{
    /// <summary>
    /// Service for fetching audiobook release/narrator metadata from AudiobookDB.
    /// API documentation: https://audiobookdb.org/docs
    ///
    /// Only the anonymous, unauthenticated external-identifier lookup is used
    /// (GET /api/audiobooks/external/audible/{asin}). AudiobookDB's general text-search
    /// endpoint requires a CSRF token tied to a browser session and isn't usable from a
    /// server-to-server integration without a registered API key, so it's intentionally
    /// not implemented here - this service only resolves alternate Releases for a book
    /// whose Audible ASIN is already known (e.g. from an existing Audible match).
    /// </summary>
    public class AudiobookDbService : IAudiobookDbService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<AudiobookDbService> _logger;
        private const string BaseUrl = "https://audiobookdb.org/api";

        public AudiobookDbService(HttpClient httpClient, ILogger<AudiobookDbService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;

            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Listenarr (https://github.com/Listenarrs/Listenarr)");
        }

        public async Task<AudiobookDbBook?> GetBookByAudibleAsinAsync(string asin)
        {
            if (string.IsNullOrWhiteSpace(asin))
            {
                return null;
            }

            try
            {
                var url = $"{BaseUrl}/audiobooks/external/audible/{Uri.EscapeDataString(asin)}";
                _logger.LogInformation("Resolving AudiobookDB releases for Audible ASIN {Asin}", LogRedaction.SanitizeText(asin));

                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogInformation(
                        "AudiobookDB returned status {StatusCode} for ASIN {Asin}; no alternate releases available",
                        response.StatusCode, LogRedaction.SanitizeText(asin));
                    return null;
                }

                using var stream = await response.Content.ReadAsStreamAsync();
                using var document = await JsonDocument.ParseAsync(stream);
                return ParseBook(document.RootElement);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Error resolving AudiobookDB releases for ASIN {Asin}", LogRedaction.SanitizeText(asin));
                return null;
            }
        }

        private static AudiobookDbBook? ParseBook(JsonElement root)
        {
            if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var id = idEl.GetString() ?? string.Empty;
            var title = GetString(root, "title");

            var releases = new List<AudiobookDbRelease>();
            if (root.TryGetProperty("releases", out var releasesEl) && releasesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var releaseEl in releasesEl.EnumerateArray())
                {
                    var release = ParseRelease(releaseEl);
                    if (release != null)
                    {
                        releases.Add(release);
                    }
                }
            }

            return new AudiobookDbBook(id, title, releases);
        }

        private static AudiobookDbRelease? ParseRelease(JsonElement releaseEl)
        {
            if (!releaseEl.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var narrators = new List<string>();
            if (releaseEl.TryGetProperty("people", out var peopleEl) && peopleEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var personEntry in peopleEl.EnumerateArray())
                {
                    var roleName = personEntry.TryGetProperty("role", out var roleEl) ? GetString(roleEl, "name") : null;
                    if (!string.Equals(roleName, "Narrator", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var personName = personEntry.TryGetProperty("person", out var personEl) ? GetString(personEl, "name") : null;
                    if (!string.IsNullOrWhiteSpace(personName))
                    {
                        narrators.Add(personName);
                    }
                }
            }

            string? publisherName = null;
            if (releaseEl.TryGetProperty("publisher", out var publisherEl) && publisherEl.ValueKind == JsonValueKind.Object)
            {
                publisherName = GetString(publisherEl, "name");
            }

            return new AudiobookDbRelease(
                idEl.GetString() ?? string.Empty,
                GetString(releaseEl, "title"),
                GetString(releaseEl, "releaseDate"),
                GetString(releaseEl, "duration"),
                GetString(releaseEl, "disambiguation"),
                publisherName,
                narrators);
        }

        private static string? GetString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
    }
}
