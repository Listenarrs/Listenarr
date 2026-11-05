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
 */
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Metadata.Providers.Audiosilo
{
    /// <summary>
    /// Read-only client for audiosilo-meta's public JSON API (no key; data is CC0).
    /// API: GET /api/v1/search?q= (works) and GET /api/v1/works/{id} (work with its recordings).
    /// </summary>
    public class AudiosiloMetaService : IAudiosiloMetaService
    {
        internal const string BaseUrl = "https://meta.audiosilo.app/api/v1/";
        internal const string SourceName = "Audiosilo";
        private const int MaxWorksPerSearch = 3;
        private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(12);
        private static readonly IReadOnlyList<AudiobookEditionCandidate> None = Array.Empty<AudiobookEditionCandidate>();

        private readonly HttpClient _httpClient;
        private readonly IMemoryCache _cache;
        private readonly ILogger<AudiosiloMetaService> _logger;

        public AudiosiloMetaService(HttpClient httpClient, IMemoryCache cache, ILogger<AudiosiloMetaService> logger)
        {
            _httpClient = httpClient;
            _cache = cache;
            _logger = logger;
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Listenarr (+https://github.com/Listenarrs/Listenarr)");
        }

        public async Task<IReadOnlyList<AudiobookEditionCandidate>> SearchEditionsAsync(
            string title,
            string? author,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return None;
            }

            // Library Import searches the same titles repeatedly; keep the community service's load low.
            var cacheKey = $"audiosilo:{Normalize(title)}|{Normalize(author)}";
            if (_cache.TryGetValue(cacheKey, out IReadOnlyList<AudiobookEditionCandidate>? cached) && cached != null)
            {
                return cached;
            }

            var editions = await FetchEditionsAsync(title.Trim(), author?.Trim(), cancellationToken);
            _cache.Set(cacheKey, editions, CacheDuration);
            return editions;
        }

        private async Task<IReadOnlyList<AudiobookEditionCandidate>> FetchEditionsAsync(
            string title,
            string? author,
            CancellationToken cancellationToken)
        {
            try
            {
                using var search = await GetJsonAsync($"search?q={Uri.EscapeDataString(title)}", cancellationToken);
                if (search == null)
                {
                    return None;
                }

                var workIds = SelectMatchingWorkIds(search.RootElement, title, author);
                var editions = new List<AudiobookEditionCandidate>();
                foreach (var workId in workIds)
                {
                    using var work = await GetJsonAsync($"works/{Uri.EscapeDataString(workId)}", cancellationToken);
                    if (work != null)
                    {
                        editions.AddRange(ParseWork(work.RootElement));
                    }
                }

                return editions;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "audiosilo-meta edition lookup failed for title '{Title}'", title);
                return None;
            }
        }

        private async Task<JsonDocument?> GetJsonAsync(string relativeUrl, CancellationToken cancellationToken)
        {
            using var response = await _httpClient.GetAsync(BaseUrl + relativeUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("audiosilo-meta returned {Status} for {Url}", (int)response.StatusCode, relativeUrl);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }

        /// <summary>Work ids from a search response whose title (and author, when given) match.</summary>
        internal static IReadOnlyList<string> SelectMatchingWorkIds(JsonElement searchResponse, string title, string? author)
        {
            var ids = new List<string>();
            foreach (var result in Items(searchResponse, "results"))
            {
                var id = GetString(result, "id");
                if (id == null || !string.Equals(GetString(result, "kind"), "work", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!TitlesMatch(GetString(result, "title"), title))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(author) && !Names(result, "authors").Any(name => AuthorsMatch(name, author)))
                {
                    continue;
                }

                ids.Add(id);
                if (ids.Count == MaxWorksPerSearch)
                {
                    break;
                }
            }

            return ids;
        }

        /// <summary>One candidate per recording of a work response.</summary>
        internal static IEnumerable<AudiobookEditionCandidate> ParseWork(JsonElement work)
        {
            var workId = GetString(work, "id") ?? string.Empty;
            var title = GetString(work, "title") ?? string.Empty;
            var authors = Names(work, "authors");
            var language = GetString(work, "language");
            string? series = null;
            string? seriesPosition = null;
            if (work.TryGetProperty("series", out var seriesElement))
            {
                var first = seriesElement.ValueKind == JsonValueKind.Array
                    ? seriesElement.EnumerateArray().FirstOrDefault()
                    : seriesElement;
                if (first.ValueKind == JsonValueKind.Object)
                {
                    series = GetString(first, "name");
                    seriesPosition = GetString(first, "position");
                }
            }

            foreach (var recording in Items(work, "recordings"))
            {
                var asins = Items(recording, "asin")
                    .Select(a => (Region: GetString(a, "region"), Asin: GetString(a, "asin")))
                    .Where(a => !string.IsNullOrWhiteSpace(a.Region) && !string.IsNullOrWhiteSpace(a.Asin))
                    .Select(a => new RegionalAsin(a.Region!.ToLowerInvariant(), a.Asin!.ToUpperInvariant()))
                    .ToList();
                yield return new AudiobookEditionCandidate(
                    SourceName,
                    workId,
                    title,
                    authors,
                    Names(recording, "narrators"),
                    GetInt(recording, "runtime_min"),
                    GetString(recording, "publisher"),
                    GetString(recording, "release_date"),
                    asins,
                    GetString(recording, "cover_url"),
                    series,
                    seriesPosition,
                    language,
                    recording.TryGetProperty("abridged", out var abridged) && abridged.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? abridged.GetBoolean()
                        : null);
            }
        }

        internal static bool TitlesMatch(string? candidate, string wanted)
        {
            var a = Normalize(candidate);
            var b = Normalize(wanted);
            if (a.Length == 0 || b.Length == 0)
            {
                return false;
            }

            return a == b || (Math.Min(a.Length, b.Length) >= 6 && (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal)));
        }

        internal static bool AuthorsMatch(string? candidate, string wanted)
        {
            var a = Normalize(candidate);
            var surname = Normalize(wanted).Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            return surname != null && a.Split(' ').Contains(surname);
        }

        private static string Normalize(string? value) =>
            Regex.Replace((value ?? string.Empty).ToLowerInvariant().Replace('’', '\''), "[^a-z0-9]+", " ").Trim();

        private static IEnumerable<JsonElement> Items(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray()
                : Enumerable.Empty<JsonElement>();

        private static IReadOnlyList<string> Names(JsonElement element, string property) =>
            Items(element, property)
                .Select(item => item.ValueKind == JsonValueKind.Object ? GetString(item, "name") : item.ValueKind == JsonValueKind.String ? item.GetString() : null)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!.Trim())
                .ToList();

        private static string? GetString(JsonElement element, string property)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()!.Trim(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            };
        }

        private static int? GetInt(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
            && number > 0
                ? number
                : null;
    }
}
