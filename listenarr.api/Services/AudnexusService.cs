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
using System.Text.Json.Serialization;

namespace Listenarr.Api.Services
{
    /// <summary>
    /// Service for fetching audiobook metadata from Audnexus API
    /// API Documentation: https://audnex.us/
    /// </summary>
    public class AudnexusService : IAudnexusService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<AudnexusService> _logger;
        private const string BASE_URL = "https://api.audnex.us";

        public AudnexusService(HttpClient httpClient, ILogger<AudnexusService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;

            // Set default headers
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Listenarr/1.0.0.0");
        }

        /// <summary>
        /// Fetches audiobook metadata from Audnexus by ASIN
        /// </summary>
        /// <param name="asin">The Amazon/Audible ASIN</param>
        /// <param name="region">The region code (default: us)</param>
        /// <param name="seedAuthors">Whether to seed authors of book (default: true)</param>
        /// <param name="update">Have server check for updated data upstream (default: false)</param>
        /// <returns>Audiobook metadata from Audnexus</returns>
        public async Task<AudnexusBookResponse?> GetBookMetadataAsync(string asin, string region = "us", bool seedAuthors = true, bool update = false)
        {
            try
            {
                var seedAuthorsParam = seedAuthors ? "1" : "0";
                var updateParam = update ? "1" : "0";
                var url = $"{BASE_URL}/books/{asin}?region={region}&seedAuthors={seedAuthorsParam}&update={updateParam}";

                _logger.LogInformation("Fetching audiobook metadata from Audnexus: {Url}", url);

                var response = await _httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Audnexus API returned status code {StatusCode} for ASIN {Asin}",
                        response.StatusCode, LogRedaction.SanitizeText(asin));
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();

                _logger.LogDebug("Audnexus raw JSON response for ASIN {Asin}: {Json}", LogRedaction.SanitizeText(asin), json.Length > 500 ? json.Substring(0, 500) + "..." : json);

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };

                AudnexusBookResponse? result = null;
                try
                {
                    result = JsonSerializer.Deserialize<AudnexusBookResponse>(json, options);
                }
                catch (JsonException jsonEx)
                {
                    _logger.LogError(jsonEx, "JSON deserialization failed for Audnexus ASIN {Asin}. JSON: {Json}",
                        LogRedaction.SanitizeText(asin), json.Length > 200 ? json.Substring(0, 200) + "..." : json);
                    return null;
                }

                if (result != null)
                {
                    _logger.LogInformation("Successfully fetched metadata for ASIN {Asin} from Audnexus. Title: {Title}",
                        LogRedaction.SanitizeText(asin), LogRedaction.SanitizeText(result.Title ?? "null"));
                }
                else
                {
                    _logger.LogWarning("Audnexus deserialization returned null for ASIN {Asin}", LogRedaction.SanitizeText(asin));
                }

                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error fetching metadata from Audnexus for ASIN {Asin}", LogRedaction.SanitizeText(asin));
                return null;
            }
        }

        /// <summary>
        /// Search for authors on Audnexus
        /// </summary>
        /// <param name="name">Author name to search</param>
        /// <param name="region">The region code (default: us)</param>
        /// <returns>Search results from Audnexus</returns>
        public async Task<List<AudnexusAuthorSearchResult>?> SearchAuthorsAsync(string name, string region = "us")
        {
            try
            {
                var url = $"{BASE_URL}/authors?name={Uri.EscapeDataString(name)}&region={region}";
                _logger.LogInformation("Searching Audnexus authors: {Url}", LogRedaction.SanitizeUrl(url));

                var response = await _httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Audnexus author search returned status code {StatusCode} for query {Query}",
                        response.StatusCode, LogRedaction.SanitizeText(name));
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };

                var result = JsonSerializer.Deserialize<List<AudnexusAuthorSearchResult>>(json, options);

                _logger.LogInformation("Successfully searched Audnexus for author: {Name}, found {Count} results",
                    LogRedaction.SanitizeText(name), result?.Count ?? 0);
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error searching Audnexus for author {Name}", LogRedaction.SanitizeText(name));
                return null;
            }
        }

        /// <summary>
        /// Get author details from Audnexus by ASIN
        /// </summary>
        /// <param name="asin">The author ASIN</param>
        /// <param name="region">The region code (default: us)</param>
        /// <param name="update">Have server check for updated data upstream (default: false)</param>
        /// <returns>Author details from Audnexus</returns>
        public async Task<AudnexusAuthorResponse?> GetAuthorAsync(string asin, string region = "us", bool update = false)
        {
            try
            {
                var updateParam = update ? "1" : "0";
                var url = $"{BASE_URL}/authors/{asin}?region={region}&update={updateParam}";

                _logger.LogInformation("Fetching author from Audnexus: {Url}", LogRedaction.SanitizeUrl(url));

                var response = await _httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Audnexus API returned status code {StatusCode} for author ASIN {Asin}",
                        response.StatusCode, LogRedaction.SanitizeText(asin));
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };

                var result = JsonSerializer.Deserialize<AudnexusAuthorResponse>(json, options);

                _logger.LogInformation("Successfully fetched author ASIN {Asin} from Audnexus", LogRedaction.SanitizeText(asin));
                return result;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP error fetching author from Audnexus for ASIN {Asin}", LogRedaction.SanitizeText(asin));
                return null;
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError(ex, "Request timed out or was canceled fetching author from Audnexus for ASIN {Asin}", LogRedaction.SanitizeText(asin));
                return null;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "JSON deserialization error fetching author from Audnexus for ASIN {Asin}", LogRedaction.SanitizeText(asin));
                return null;
            }
        }

        /// <summary>
        /// Get chapter information for a book from Audnexus
        /// </summary>
        /// <param name="asin">The book ASIN</param>
        /// <param name="region">The region code (default: us)</param>
        /// <param name="update">Have server check for updated data upstream (default: false)</param>
        /// <returns>Chapter information from Audnexus</returns>
        public async Task<AudnexusChapterResponse?> GetChaptersAsync(string asin, string region = "us", bool update = false)
        {
            try
            {
                var updateParam = update ? "1" : "0";
                var url = $"{BASE_URL}/books/{asin}/chapters?region={region}&update={updateParam}";

                _logger.LogInformation("Fetching chapters from Audnexus: {Url}", LogRedaction.SanitizeUrl(url));

                var response = await _httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Audnexus API returned status code {StatusCode} for chapters of ASIN {Asin}",
                        response.StatusCode, LogRedaction.SanitizeText(asin));
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };

                var result = JsonSerializer.Deserialize<AudnexusChapterResponse>(json, options);

                _logger.LogInformation("Successfully fetched chapters for ASIN {Asin} from Audnexus", LogRedaction.SanitizeText(asin));
                return result;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP error fetching chapters from Audnexus for ASIN {Asin}", LogRedaction.SanitizeText(asin));
                return null;
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError(ex, "Request timed out fetching chapters from Audnexus for ASIN {Asin}", LogRedaction.SanitizeText(asin));
                return null;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "JSON deserialization error fetching chapters from Audnexus for ASIN {Asin}", LogRedaction.SanitizeText(asin));
                return null;
            }
        }
    }

    #region Response Models

    /// <summary>
    /// Response from Audnexus book endpoint
    /// </summary>
    public class AudnexusBookResponse
    {
        public string? Asin { get; set; }
        public string? Title { get; set; }
        public string? Subtitle { get; set; }
        public List<AudnexusAuthor>? Authors { get; set; }
        public List<AudnexusNarrator>? Narrators { get; set; }
        public string? PublisherName { get; set; }
        public string? ReleaseDate { get; set; }
        public string? Description { get; set; }
        public string? Summary { get; set; }
        public string? Image { get; set; }
        public int? RuntimeLengthMin { get; set; }
        public string? Language { get; set; }
        public string? FormatType { get; set; }
        public List<AudnexusGenre>? Genres { get; set; }
        public AudnexusSeries? SeriesPrimary { get; set; }
        public AudnexusSeries? SeriesSecondary { get; set; }
        public string? Rating { get; set; }
        public int? Copyright { get; set; }
        public string? Isbn { get; set; }
        public string? Region { get; set; }
        public bool? IsAdult { get; set; }
        public string? LiteratureType { get; set; }
    }

    public class AudnexusAuthor
    {
        public string? Asin { get; set; }
        public string? Name { get; set; }
    }

    public class AudnexusNarrator
    {
        public string? Name { get; set; }
    }

    public class AudnexusGenre
    {
        public string? Asin { get; set; }
        public string? Name { get; set; }
        public string? Type { get; set; }
    }

    public class AudnexusSeries
    {
        public string? Asin { get; set; }
        public string? Name { get; set; }
        public string? Position { get; set; }
    }

    /// <summary>
    /// Response from Audnexus author search endpoint
    /// </summary>
    public class AudnexusAuthorSearchResult
    {
        public string? Asin { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Image { get; set; }
        public string? Region { get; set; }
        public List<AudnexusGenre>? Genres { get; set; }
        public List<AudnexusSimilarAuthor>? Similar { get; set; }
    }

    public class AudnexusSimilarAuthor
    {
        public string? Asin { get; set; }
        public string? Name { get; set; }
    }

    /// <summary>
    /// Response from Audnexus author endpoint
    /// </summary>
    public class AudnexusAuthorResponse
    {
        public string? Asin { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Image { get; set; }
        public string? Region { get; set; }
        public List<AudnexusGenre>? Genres { get; set; }
        public List<AudnexusSimilarAuthor>? Similar { get; set; }
    }

    /// <summary>
    /// Response from Audnexus chapters endpoint
    /// </summary>
    public class AudnexusChapterResponse
    {
        public string? Asin { get; set; }
        public string? Region { get; set; }
        public int? BrandIntroDurationMs { get; set; }
        public int? BrandOutroDurationMs { get; set; }
        public bool? IsAccurate { get; set; }
        public int? RuntimeLengthMs { get; set; }
        public int? RuntimeLengthSec { get; set; }
        public List<AudnexusChapter>? Chapters { get; set; }
    }

    public class AudnexusChapter
    {
        public int? LengthMs { get; set; }
        public int? StartOffsetMs { get; set; }
        public int? StartOffsetSec { get; set; }
        public string? Title { get; set; }
    }

    #endregion
}

