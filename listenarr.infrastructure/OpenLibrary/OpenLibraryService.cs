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
using Listenarr.Application.Interfaces;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Listenarr.Application.Security;
using Listenarr.Application.Search;

namespace Listenarr.Infrastructure.OpenLibrary
{
    public class OpenLibraryService : IOpenLibraryService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<OpenLibraryService> _logger;
        private const string BaseUrl = "https://openlibrary.org";

        public OpenLibraryService(HttpClient httpClient, ILogger<OpenLibraryService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task<List<string>> GetIsbnsForTitleAsync(string title, string? author = null)
        {
            try
            {
                var searchResponse = await SearchBooksAsync(title, author, 20); // Get more results for better ISBN coverage
                var isbns = new List<string>();

                foreach (var book in searchResponse.Docs.Where(book => book.Isbn != null))
                {
                    foreach (var cleanIsbn in book.Isbn!.Select(isbn => isbn.Replace("-", "").Replace(" ", "")).Where(i => !string.IsNullOrEmpty(i) && (i.Length == 10 || i.Length == 13)))
                    {
                        isbns.Add(cleanIsbn);
                    }
                }

                return isbns.Distinct().ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error getting ISBNs for title: {Title}", title);
                return new List<string>();
            }
        }

        public async Task<OpenLibrarySearchResponse> SearchBooksAsync(string title, string? author = null, int limit = 10)
        {
            try
            {
                // Build a normalized 'q' query for OpenLibrary to improve matching.
                // Normalize title and author into tokens (preserve alphanumerics and hyphens like 'sg-1').
                var qParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(title))
                {
                    var nt = NormalizeForOpenLibrary(title);
                    if (!string.IsNullOrEmpty(nt)) qParts.Add(nt);
                }
                if (!string.IsNullOrWhiteSpace(author))
                {
                    var na = NormalizeForOpenLibrary(author);
                    if (!string.IsNullOrEmpty(na)) qParts.Add(na);
                }

                var searchParams = new List<string>();
                var q = qParts.Any() ? string.Join("+", qParts) : string.Empty;
                if (!string.IsNullOrEmpty(q))
                {
                    searchParams.Add($"q={Uri.EscapeDataString(q)}");
                }
                else
                {
                    // Fallback to title param if normalization yields nothing
                    if (!string.IsNullOrEmpty(title))
                        searchParams.Add($"title={Uri.EscapeDataString(title)}");
                    if (!string.IsNullOrEmpty(author))
                        searchParams.Add($"author={Uri.EscapeDataString(author)}");
                }

                searchParams.Add($"limit={limit}");

                // Request specific fields to optimize response
                var fields = new[]
                {
                    "key", "title", "author_name", "author_key", "first_publish_year",
                    "isbn", "publisher", "cover_i", "edition_count", "language", "subject"
                };
                searchParams.Add($"fields={string.Join(",", fields)}");

                var queryString = string.Join("&", searchParams);
                var url = $"{BaseUrl}/search.json?{queryString}";

                _logger.LogInformation("Searching OpenLibrary: {Url}", LogRedaction.SanitizeUrl(url));

                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var jsonContent = await response.Content.ReadAsStringAsync();
                var searchResponse = JsonSerializer.Deserialize<OpenLibrarySearchResponse>(jsonContent);

                return searchResponse ?? new OpenLibrarySearchResponse();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error searching OpenLibrary for title: {Title}, author: {Author}", LogRedaction.SanitizeText(title), LogRedaction.SanitizeText(author));
                return new OpenLibrarySearchResponse();
            }
        }

        // Normalize a title/author for OpenLibrary 'q' parameter by extracting tokens that
        // are alphanumeric or contain hyphens (e.g. 'sg-1'), then join with '+'
        private static string NormalizeForOpenLibrary(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;
            var lower = input.ToLowerInvariant();
            // Extract tokens: letters, digits, and hyphens
            var matches = Regex.Matches(lower, @"[a-z0-9\-]+");
            var tokens = new List<string>();
            foreach (var v in matches.Select(m => m.Value.Trim('-')).Where(v => !string.IsNullOrEmpty(v)))
            {
                tokens.Add(v);
            }
            return string.Join("+", tokens);
        }
    }
}
