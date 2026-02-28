using System.Text.Json;
using System.Text.Json.Serialization;
using System;
namespace Listenarr.Api.Services
{
    public class AudimetaService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<AudimetaService> _logger;
        private const string BASE_URL = "https://audimeta.de";

        public AudimetaService(HttpClient httpClient, ILogger<AudimetaService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Listenarr/1.0.0.0");
        }

        /// <summary>
        /// Fetches books for a given author ASIN using the /author/books/[ASIN] endpoint.
        /// </summary>
        /// <param name="authorAsin">The ASIN of the author.</param>
        /// <param name="page">Page number (default 1).</param>
        /// <param name="limit">Number of results per page (default 50).</param>
        /// <param name="region">Region (default "us").</param>
        /// <param name="language">Optional language filter.</param>
        /// <returns>AudimetaSearchResponse containing books by the author.</returns>
        public virtual async Task<AudimetaSearchResponse?> GetBooksByAuthorAsinAsync(string authorAsin, int page = 1, int limit = 50, string region = "us", string? language = null)
        {
            try
            {
                var url = $"{BASE_URL}/author/books/{Uri.EscapeDataString(authorAsin)}?limit={limit}&page={page}&cache=true&region={region}";
                if (!string.IsNullOrWhiteSpace(language)) url += $"&language={Uri.EscapeDataString(language)}";
                _logger.LogInformation("Fetching books for author ASIN {AuthorAsin}: {Url}", authorAsin, url);
                return await ExecuteSearchAsync(url, $"authorAsin:{authorAsin} page {page}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error fetching books for author ASIN {AuthorAsin}", authorAsin);
                return null;
            }
        }

        // Series lookup helpers (proxy audimeta /series endpoints)
        public virtual async Task<object?> SearchSeriesByNameAsync(string name, string region = "us")
        {
            try
            {
                var url = $"{BASE_URL}/series?cache=true&name={Uri.EscapeDataString(name)}&region={region}";
                _logger.LogInformation("Searching audimeta.de for series name {Name}: {Url}", name, url);
                var resp = await GetWithTimeoutAsync(url);
                if (resp == null)
                {
                    _logger.LogWarning("Audimeta series search request timed out for name {Name}", name);
                    return null;
                }
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Audimeta series search returned status code {StatusCode} for name {Name}", resp.StatusCode, name);
                    return null;
                }
                var json = await resp.Content.ReadAsStringAsync();
                var obj = JsonSerializer.Deserialize<object>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return obj;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error searching audimeta.de series for name {Name}", name);
                return null;
            }
        }

        public virtual async Task<object?> GetBooksBySeriesAsinAsync(string seriesAsin, string region = "us")
        {
            try
            {
                var url = $"{BASE_URL}/series/books/{Uri.EscapeDataString(seriesAsin)}?cache=true&region={region}";
                _logger.LogInformation("Fetching audimeta.de series books for ASIN {Asin}: {Url}", seriesAsin, url);
                var resp = await GetWithTimeoutAsync(url);
                if (resp == null)
                {
                    _logger.LogWarning("Audimeta series books request timed out for ASIN {Asin}", seriesAsin);
                    return null;
                }
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Audimeta series books returned status code {StatusCode} for series ASIN {Asin}", resp.StatusCode, seriesAsin);
                    return null;
                }
                var json = await resp.Content.ReadAsStringAsync();
                var obj = JsonSerializer.Deserialize<object>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return obj;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error fetching audimeta.de series books for ASIN {Asin}", seriesAsin);
                return null;
            }
        }

        public virtual async Task<AudimetaBookResponse?> GetBookMetadataAsync(string asin, string region = "us", bool useCache = true, string? language = null)
        {
            try
            {
            var url = $"{BASE_URL}/book/{asin}?cache={useCache.ToString().ToLower()}&region={region}";
            if (!string.IsNullOrWhiteSpace(language)) url += $"&language={Uri.EscapeDataString(language)}";
            _logger.LogInformation("Fetching audiobook metadata from audimeta.de: {Url}", url);
                var response = await GetWithTimeoutAsync(url);
                if (response == null)
                {
                    _logger.LogWarning("Audimeta API request timed out for ASIN {Asin}", asin);
                    return null;
                }
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Audimeta API returned status code {StatusCode} for ASIN {Asin}", response.StatusCode, asin);
                    return null;
                }
                var json = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<AudimetaBookResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                _logger.LogInformation("Successfully fetched metadata for ASIN {Asin} from audimeta.de", asin);
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error fetching metadata from audimeta.de for ASIN {Asin}", asin);
                return null;
            }
        }

        public virtual async Task<AudimetaSearchResponse?> SearchByTitleAsync(string title, int page = 1, int limit = 50, string region = "us", string? language = null)
        {
            // For advanced title-only searches, prefer the precise db/book?title= form per requirements
            string url;
            if (page <= 1)
            {
                url = $"{BASE_URL}/db/book?title={Uri.EscapeDataString(title)}&limit={limit}&page={page}&cache=true&region={region}";
                if (!string.IsNullOrWhiteSpace(language)) url += $"&language={Uri.EscapeDataString(language)}";
                _logger.LogInformation("Searching audimeta.de (advanced title-only db/book) : {Url}", url);
                var response = await ExecuteSearchAsync(url, title);

                // Keep existing server-side filtering behavior to remove podcasts/unwanted types
                if (response?.Results != null && response.Results.Any())
                {
                    var allowed = response.Results.Where(r => IsAllowedContentTypeOrDelivery(r)).ToList();
                    if (allowed.Any()) return new AudimetaSearchResponse { Results = allowed, TotalResults = allowed.Count };
                }

                // Fallback: try query= if title= returns nothing
                url = $"{BASE_URL}/db/book?query={Uri.EscapeDataString(title)}&limit={limit}&page={page}&cache=true&region={region}";
                if (!string.IsNullOrWhiteSpace(language)) url += $"&language={Uri.EscapeDataString(language)}";
                _logger.LogInformation("Falling back to db/book?query= for title: {Url}", url);
                return await ExecuteSearchAsync(url, title);
            }
            else
            {
                url = $"{BASE_URL}/db/book?query={Uri.EscapeDataString(title)}&limit={limit}&page={page}&cache=true&contentType=Book&region={region}";
                if (!string.IsNullOrWhiteSpace(language)) url += $"&language={Uri.EscapeDataString(language)}";
                _logger.LogInformation("Searching audimeta.de (advanced title-only page>1) : {Url}", url);
                return await ExecuteSearchAsync(url, title);
            }
        }

        public virtual async Task<AudimetaSearchResponse?> SearchByTitleAndAuthorAsync(string title, string author, int page = 1, int limit = 50, string region = "us", string? language = null)
        {
            // For advanced title+author searches, prefer the author lookup + /author/books/[ASIN] flow
            return await SearchByTitleAndAuthorPagedAsync(title, author, page, limit, region, language);
        }

        public virtual async Task<AudimetaSearchResponse?> SearchByTitleAndAuthorPagedAsync(string title, string author, int page = 1, int limit = 50, string region = "us", string? language = null)
        {
            // Prefer author-specific endpoint when an author is provided: lookup author ASIN then request their books
            if (string.IsNullOrWhiteSpace(author))
            {
                var query = title;
                var url = $"{BASE_URL}/search?products_sort_by=Title&cache=true&page={page}&limit={limit}&query={Uri.EscapeDataString(query)}&region={region}";
                _logger.LogInformation("Searching audimeta.de (search) by title+author (no author provided): {Url}", url);
                return await ExecuteSearchAsync(url, $"{title} (page {page})");
            }

            try
            {
                // 1) Lookup author ASIN via /author?name=
                var authorLookupUrl = $"{BASE_URL}/author?cache=true&region={region}&name={Uri.EscapeDataString(author)}";
                if (!string.IsNullOrWhiteSpace(language)) authorLookupUrl += $"&language={Uri.EscapeDataString(language)}";
                _logger.LogInformation("Looking up author on audimeta.de: {Url}", authorLookupUrl);
                var lookupResp = await GetWithTimeoutAsync(authorLookupUrl);
                if (lookupResp == null)
                {
                    _logger.LogWarning("Author lookup request timed out for author {Author}", author);
                    var fallbackUrl = $"{BASE_URL}/search?products_sort_by=Title&cache=true&page={page}&limit={limit}&query={Uri.EscapeDataString(title + " " + author)}&region={region}";
                    return await ExecuteSearchAsync(fallbackUrl, $"{title} by {author} (page {page})");
                }
                if (!lookupResp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Author lookup returned status {Status} for author {Author}", lookupResp.StatusCode, author);
                    // fallback to generic search
                    var fallbackUrl = $"{BASE_URL}/search?products_sort_by=Title&cache=true&page={page}&limit={limit}&query={Uri.EscapeDataString(title + " " + author)}&region={region}";
                    return await ExecuteSearchAsync(fallbackUrl, $"{title} by {author} (page {page})");
                }

                var lookupJson = await lookupResp.Content.ReadAsStringAsync();
                string? authorAsin = null;
                try
                {
                    // Response may be an array or envelope; try parse as array and pick first item's asin
                    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    if (!string.IsNullOrWhiteSpace(lookupJson) && lookupJson.TrimStart()[0] == '[')
                    {
                        var list = JsonSerializer.Deserialize<List<AuthorLookupItem>>(lookupJson, opts);
                        authorAsin = list?.FirstOrDefault()?.Asin;
                    }
                    else
                    {
                        var doc = JsonSerializer.Deserialize<AuthorLookupEnvelope>(lookupJson, opts);
                        authorAsin = doc?.Asin ?? doc?.Results?.FirstOrDefault()?.Asin;
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse author lookup JSON for author {Author}", author);
                }

                if (string.IsNullOrWhiteSpace(authorAsin))
                {
                    _logger.LogWarning("No author ASIN found for author '{Author}', falling back to search endpoint", author);
                    var fallbackUrl2 = $"{BASE_URL}/search?products_sort_by=Title&cache=true&page={page}&limit={limit}&query={Uri.EscapeDataString(title + " " + author)}&region={region}";
                    return await ExecuteSearchAsync(fallbackUrl2, $"{title} by {author} (page {page})");
                }

                // 2) Fetch author's books
                var booksUrl = $"{BASE_URL}/author/books/{Uri.EscapeDataString(authorAsin)}?limit={limit}&page={page}&cache=true&region={region}";
                if (!string.IsNullOrWhiteSpace(language)) booksUrl += $"&language={Uri.EscapeDataString(language)}";
                _logger.LogInformation("Fetching books for author ASIN {AuthorAsin}: {Url}", authorAsin, booksUrl);
                var booksResult = await ExecuteSearchAsync(booksUrl, $"author:{author} (authorAsin:{authorAsin}) page {page}");

                if (booksResult == null || booksResult.Results == null) return booksResult;

                // 3) Apply server-side filtering using provided title, isbn, asin, language if present
                var filtered = booksResult.Results.AsEnumerable();

                // If the title parameter encodes an ISBN (e.g. "ISBN:1234567890"), extract it
                string? isbnFromTitle = null;
                if (!string.IsNullOrWhiteSpace(title) && title.Trim().StartsWith("ISBN:", StringComparison.OrdinalIgnoreCase))
                {
                    isbnFromTitle = title.Trim().Substring(5).Trim();
                }

                if (!string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(isbnFromTitle))
                {
                    var t = title.Trim();
                    filtered = filtered.Where(r => !string.IsNullOrWhiteSpace(r.Title) && r.Title.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
                }

                // If title looks like an ASIN, prefer exact ASIN match
                if (!string.IsNullOrWhiteSpace(title) && title.Trim().StartsWith("B0", StringComparison.OrdinalIgnoreCase) && title.Trim().Length >= 10)
                {
                    var possibleAsin = title.Trim();
                    filtered = filtered.Where(r => string.Equals(r.Asin, possibleAsin, StringComparison.OrdinalIgnoreCase));
                }

                // If ISBN was provided via title token, try to resolve by fetching metadata per candidate
                if (!string.IsNullOrWhiteSpace(isbnFromTitle))
                {
                    var candidates = filtered.ToList();
                    var matched = new List<AudimetaSearchResult>();
                    foreach (var c in candidates)
                    {
                        try
                        {
                            if (string.IsNullOrWhiteSpace(c.Asin)) continue;
                            var meta = await GetBookMetadataAsync(c.Asin, region, true, language);
                            if (meta != null && !string.IsNullOrWhiteSpace(meta.Isbn) && string.Equals(meta.Isbn.Trim(), isbnFromTitle.Trim(), StringComparison.OrdinalIgnoreCase))
                            {
                                matched.Add(c);
                            }
                        }
                        catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException) { 
                            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                        }
                    }

                    filtered = matched;
                }

                // Language filter (use explicit language param when provided)
                if (!string.IsNullOrWhiteSpace(language))
                {
                    var lang = language.Trim().ToLowerInvariant();
                    filtered = filtered.Where(r => !string.IsNullOrWhiteSpace(r.Language) && r.Language.Trim().ToLowerInvariant() == lang);
                }

                var finalList = filtered.ToList();
                return new AudimetaSearchResponse { Results = finalList, TotalResults = finalList.Count };
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error executing author-based search for: {Title} / {Author}", title, author);
                return null;
            }
        }

        public virtual async Task<AudimetaSearchResponse?> SearchByAuthorAsync(string author, int page = 1, int limit = 50, string region = "us", string? language = null)
        {
            // 1) Lookup author ASIN via /author?name=
            var authorLookupUrl = $"{BASE_URL}/author?cache=true&region={region}&name={Uri.EscapeDataString(author)}";
            if (!string.IsNullOrWhiteSpace(language)) authorLookupUrl += $"&language={Uri.EscapeDataString(language)}";
            _logger.LogInformation("Looking up author on audimeta.de: {Url}", authorLookupUrl);
            var lookupResp = await GetWithTimeoutAsync(authorLookupUrl);
            if (lookupResp == null)
            {
                _logger.LogWarning("Author lookup request timed out for author {Author}", author);
                return null;
            }
            if (!lookupResp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Author lookup returned status {Status} for author {Author}", lookupResp.StatusCode, author);
                return null;
            }

            var lookupJson = await lookupResp.Content.ReadAsStringAsync();
            string? authorAsin = null;
            try
            {
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                if (!string.IsNullOrWhiteSpace(lookupJson) && lookupJson.TrimStart()[0] == '[')
                {
                    var list = JsonSerializer.Deserialize<List<AuthorLookupItem>>(lookupJson, opts);
                    authorAsin = list?.FirstOrDefault()?.Asin;
                }
                else
                {
                    var doc = JsonSerializer.Deserialize<AuthorLookupEnvelope>(lookupJson, opts);
                    authorAsin = doc?.Asin ?? doc?.Results?.FirstOrDefault()?.Asin;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse author lookup JSON for author {Author}", author);
            }

            if (string.IsNullOrWhiteSpace(authorAsin))
            {
                _logger.LogWarning("No author ASIN found for author '{Author}'", author);
                return null;
            }

            // 2) Fetch author's books
            var booksUrl = $"{BASE_URL}/author/books/{Uri.EscapeDataString(authorAsin)}?limit={limit}&page={page}&cache=true&region={region}";
            if (!string.IsNullOrWhiteSpace(language)) booksUrl += $"&language={Uri.EscapeDataString(language)}";
            _logger.LogInformation("Fetching books for author ASIN {AuthorAsin}: {Url}", authorAsin, booksUrl);
            var booksResult = await ExecuteSearchAsync(booksUrl, $"author:{author} (authorAsin:{authorAsin}) page {page}");
            return booksResult;
        }

        /// <summary>
        /// Lookup a single author by name using the Audimeta /author endpoint and return basic info (ASIN + image if available).
        /// </summary>
        public virtual async Task<AuthorLookupItem?> LookupAuthorAsync(string author, string region = "us")
        {
            if (string.IsNullOrWhiteSpace(author)) return null;

            try
            {
                var authorLookupUrl = $"{BASE_URL}/author?cache=true&region={region}&name={Uri.EscapeDataString(author)}";
                _logger.LogInformation("Looking up author on audimeta.de: {Url}", authorLookupUrl);
                var lookupResp = await GetWithTimeoutAsync(authorLookupUrl);
                if (lookupResp == null)
                {
                    _logger.LogWarning("Author lookup request timed out for author {Author}", author);
                    return null;
                }
                if (!lookupResp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Author lookup returned status {Status} for author {Author}", lookupResp.StatusCode, author);
                    return null;
                }

                var lookupJson = await lookupResp.Content.ReadAsStringAsync();
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                if (!string.IsNullOrWhiteSpace(lookupJson) && lookupJson.TrimStart()[0] == '[')
                {
                    var list = JsonSerializer.Deserialize<List<AuthorLookupItem>>(lookupJson, opts);
                    return list?.FirstOrDefault();
                }
                else
                {
                    var doc = JsonSerializer.Deserialize<AuthorLookupEnvelope>(lookupJson, opts);
                    if (doc == null) return null;
                    if (!string.IsNullOrWhiteSpace(doc.Asin)) return new AuthorLookupItem { Asin = doc.Asin, Name = doc.Results?.FirstOrDefault()?.Name, Image = doc.Results?.FirstOrDefault()?.Image };
                    return doc.Results?.FirstOrDefault();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogWarning(ex, "Failed to lookup author {Author}", author);
                return null;
            }
        }

        public virtual async Task<AudimetaSearchResponse?> SearchByIsbnAsync(string isbn, int page = 1, int limit = 50, string region = "us", string? language = null)
        {
            var url = $"{BASE_URL}/db/book?products_sort_by=BestSellers&cache=true&page={page}&limit={limit}&isbn={Uri.EscapeDataString(isbn)}&region={region}";
            if (!string.IsNullOrWhiteSpace(language)) url += $"&language={Uri.EscapeDataString(language)}";
            _logger.LogInformation("Searching audimeta.de by ISBN: {Url}", url);
            return await ExecuteSearchAsync(url, isbn);
        }

        public virtual async Task<AudimetaSearchResponse?> SearchBooksAsync(string query, int page = 1, int limit = 50, string region = "us", string? language = null)
        {
            // If query looks like an ASIN, perform a direct metadata lookup which returns a single result
            bool IsAsin(string s)
            {
                if (string.IsNullOrEmpty(s)) return false;
                if (s.Length != 10) return false;
                if (!(s.StartsWith("B0", StringComparison.OrdinalIgnoreCase) || char.IsDigit(s[0]))) return false;
                return s.All(char.IsLetterOrDigit);
            }

            if (IsAsin(query?.Trim() ?? string.Empty))
            {
                var asin = query?.Trim() ?? string.Empty;
                _logger.LogInformation("Query appears to be an ASIN; performing direct audimeta book lookup for {Asin}", asin);
                var meta = await GetBookMetadataAsync(asin, region, true, language);
                if (meta == null) return null;

                // Convert AudimetaBookResponse to AudimetaSearchResult for compatibility with callers
                var single = new AudimetaSearchResult
                {
                    Asin = meta.Asin,
                    Title = meta.Title,
                    Subtitle = meta.Subtitle,
                    Authors = meta.Authors,
                    ImageUrl = meta.ImageUrl,
                    LengthMinutes = meta.LengthMinutes,
                    Language = meta.Language,
                    ContentType = meta.ContentType,
                    ContentDeliveryType = meta.ContentDeliveryType,
                    BookFormat = meta.BookFormat,
                    Genres = meta.Genres,
                    Series = meta.Series,
                    Publisher = meta.Publisher,
                    Narrators = meta.Narrators,
                    ReleaseDate = meta.ReleaseDate,
                    Link = $"https://www.amazon.com/dp/{meta.Asin}"
                };

                return new AudimetaSearchResponse { Results = new List<AudimetaSearchResult> { single }, TotalResults = 1 };
            }

            // Default simple search should use Relevance sorting per new requirements
            var safeQuery = query ?? string.Empty;
            var url = $"{BASE_URL}/search?products_sort_by=Relevance&cache=true&page={page}&limit={limit}&query={Uri.EscapeDataString(safeQuery)}&region={region}";
            if (!string.IsNullOrWhiteSpace(language)) url += $"&language={Uri.EscapeDataString(language)}";
            _logger.LogInformation("Searching audimeta.de (simple) : {Url}", url);
            return await ExecuteSearchAsync(url, safeQuery);
        }

        private async Task<AudimetaSearchResponse?> ExecuteSearchAsync(string url, string searchTerm)
        {
            try
            {
                var response = await GetWithTimeoutAsync(url);
                if (response == null)
                {
                    _logger.LogWarning("Audimeta search request timed out for: {SearchTerm}", searchTerm);
                    return null;
                }
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Audimeta search returned status code {StatusCode} for: {SearchTerm}", response.StatusCode, searchTerm);
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();

                // Avoid throwing and logging exceptions for expected formats by inspecting JSON first
                var trimmed = json.TrimStart();

                if (!string.IsNullOrEmpty(trimmed) && trimmed[0] == '[')
                {
                    // JSON array -> deserialize as a list
                    try
                    {
                        var list = JsonSerializer.Deserialize<List<AudimetaSearchResult>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (list != null)
                        {
                            var dropped = list.Where(r => SearchResultIndicatesPodcast(r)).ToList();
                            var filtered = list.Except(dropped).ToList();

                            if (dropped.Any())
                            {
                                try
                                {
                                    var entries = dropped.Select(r => string.Format("{0} :: {1} :: {2}", r.Asin ?? "<no-asin>", r.Title ?? "<no-title>", GetPodcastFilterReason(r) ?? "podcast_detected")).ToList();
                                    _logger.LogInformation("Audimeta search removed {Count} items due to podcast heuristics: {Entries}", dropped.Count, string.Join(" | ", entries));
                                }
                                catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException) { 
                                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                }
                            }

                            if (filtered.Any()) return new AudimetaSearchResponse { Results = filtered, TotalResults = filtered.Count };
                            else _logger.LogWarning("Audimeta search returned {Count} results after podcast filtering (list format) for: {SearchTerm}", filtered.Count, searchTerm);
                        }
                        else
                        {
                            _logger.LogWarning("Audimeta search returned null list for: {SearchTerm}, JSON: {Json}", searchTerm, json.Length > 500 ? json.Substring(0, 500) + "..." : json);
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "Failed to deserialize JSON array as List<AudimetaSearchResult> for: {SearchTerm}, JSON: {Json}", searchTerm, json.Length > 500 ? json.Substring(0, 500) + "..." : json);
                    }
                }
                else
                {
                    // JSON object -> expected envelope format
                    try
                    {
                        var envelope = JsonSerializer.Deserialize<AudimetaSearchResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (envelope != null && envelope.Results != null)
                        {
                            var dropped = envelope.Results.Where(r => SearchResultIndicatesPodcast(r)).ToList();
                            if (dropped.Any())
                            {
                                try
                                {
                                    var entries = dropped.Select(r => string.Format("{0} :: {1} :: {2}", r.Asin ?? "<no-asin>", r.Title ?? "<no-title>", GetPodcastFilterReason(r) ?? "podcast_detected")).ToList();
                                    _logger.LogInformation("Audimeta search removed {Count} items due to podcast heuristics: {Entries}", dropped.Count, string.Join(" | ", entries));
                                }
                                catch (Exception caughtEx_3) when (caughtEx_3 is not OperationCanceledException && caughtEx_3 is not OutOfMemoryException && caughtEx_3 is not StackOverflowException) { 
                                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                }
                            }

                            envelope.Results = envelope.Results.Where(r => !SearchResultIndicatesPodcast(r)).ToList();
                            if (envelope.Results.Any()) return envelope;
                            else _logger.LogWarning("Audimeta search returned {Count} results after podcast filtering for: {SearchTerm}", envelope.Results.Count, searchTerm);
                        }
                        else
                        {
                            _logger.LogWarning("Audimeta search returned null envelope or null results for: {SearchTerm}, JSON: {Json}", searchTerm, json.Length > 500 ? json.Substring(0, 500) + "..." : json);
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "Failed to deserialize as AudimetaSearchResponse for: {SearchTerm}, JSON: {Json}", searchTerm, json.Length > 500 ? json.Substring(0, 500) + "..." : json);

                        // Last resort: attempt to parse as a list (some endpoints sometimes return a top-level array)
                        try
                        {
                            var list = JsonSerializer.Deserialize<List<AudimetaSearchResult>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (list != null)
                            {
                                var filtered = list.Where(r => !SearchResultIndicatesPodcast(r)).ToList();
                                if (filtered.Any()) return new AudimetaSearchResponse { Results = filtered, TotalResults = filtered.Count };
                                else _logger.LogWarning("Audimeta search returned {Count} results after podcast filtering (list format) for: {SearchTerm}", filtered.Count, searchTerm);
                            }
                        }
                        catch (JsonException ex2)
                        {
                            _logger.LogWarning(ex2, "Failed to deserialize as List<AudimetaSearchResult> for: {SearchTerm}, JSON: {Json}", searchTerm, json.Length > 500 ? json.Substring(0, 500) + "..." : json);
                        }
                    }
                }

                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error searching audimeta.de for: {SearchTerm}", searchTerm);
                return null;
            }
        }

        private async Task<HttpResponseMessage?> GetWithTimeoutAsync(string url, int timeoutSeconds = 5)
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                var resp = await _httpClient.GetAsync(url, cts.Token);
                return resp;
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogWarning(ex, "Audimeta request timed out for URL: {Url}", url);
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error performing Audimeta HTTP request for URL: {Url}", url);
                return null;
            }
        }

        private static bool SearchResultIndicatesPodcast(AudimetaSearchResult? r)
        {
            if (r == null) return false;
            // If result explicitly indicates it's a book/product by content type or delivery type,
            // prefer that signal and do not treat it as a podcast even if other fields mention 'podcast'.
            var ct = r.ContentType?.Trim();
            var cdt = r.ContentDeliveryType?.Trim();
            var ctIsBookOrProduct = !string.IsNullOrWhiteSpace(ct) && (string.Equals(ct, "Book", StringComparison.OrdinalIgnoreCase) || string.Equals(ct, "Product", StringComparison.OrdinalIgnoreCase));
            var allowedBookDelivery = new[] { "SinglePartBook", "MultiPartBook", "BookSeries" };
            var cdtIsBook = !string.IsNullOrWhiteSpace(cdt) && allowedBookDelivery.Any(a => string.Equals(a, cdt, StringComparison.OrdinalIgnoreCase));
            if (ctIsBookOrProduct || cdtIsBook) return false;

            if (!string.IsNullOrWhiteSpace(r.ContentType) && r.ContentType.IndexOf("podcast", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (!string.IsNullOrWhiteSpace(r.ContentDeliveryType) && r.ContentDeliveryType.IndexOf("podcast", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (!string.IsNullOrWhiteSpace(r.EpisodeType)) return true;
            if (!string.IsNullOrWhiteSpace(r.Sku) && r.Sku.StartsWith("PC_", StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrWhiteSpace(r.BookFormat) && r.BookFormat.IndexOf("podcast", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (r.Genres?.Any(g => (!string.IsNullOrWhiteSpace(g?.Name) && g.Name.IndexOf("podcast", StringComparison.OrdinalIgnoreCase) >= 0) || (!string.IsNullOrWhiteSpace(g?.Type) && g.Type.IndexOf("podcast", StringComparison.OrdinalIgnoreCase) >= 0)) == true) return true;
            return false;
        }

        private static string? GetPodcastFilterReason(AudimetaSearchResult? r)
        {
            if (r == null) return null;
            if (!string.IsNullOrWhiteSpace(r.ContentType) && r.ContentType.IndexOf("podcast", StringComparison.OrdinalIgnoreCase) >= 0) return "ContentType contains 'podcast'";
            if (!string.IsNullOrWhiteSpace(r.ContentDeliveryType) && r.ContentDeliveryType.IndexOf("podcast", StringComparison.OrdinalIgnoreCase) >= 0) return "ContentDeliveryType contains 'podcast'";
            if (!string.IsNullOrWhiteSpace(r.EpisodeType)) return "EpisodeType present";
            if (!string.IsNullOrWhiteSpace(r.Sku) && r.Sku.StartsWith("PC_", StringComparison.OrdinalIgnoreCase)) return "SKU starts with PC_";
            if (!string.IsNullOrWhiteSpace(r.BookFormat) && r.BookFormat.IndexOf("podcast", StringComparison.OrdinalIgnoreCase) >= 0) return "BookFormat contains 'podcast'";
            if (r.Genres?.Any(g => (!string.IsNullOrWhiteSpace(g?.Name) && g.Name.IndexOf("podcast", StringComparison.OrdinalIgnoreCase) >= 0) || (!string.IsNullOrWhiteSpace(g?.Type) && g.Type.IndexOf("podcast", StringComparison.OrdinalIgnoreCase) >= 0)) == true) return "Genre contains 'podcast'";
            return null;
        }

        private static bool IsAllowedContentTypeOrDelivery(AudimetaSearchResult? r)
        {
            if (r == null) return false;
            // Require BOTH: ContentType must be Book|Product AND ContentDeliveryType must be one of allowed book delivery types.
            var ct = r.ContentType?.Trim();
            var cdt = r.ContentDeliveryType?.Trim();

            var ctOk = !string.IsNullOrWhiteSpace(ct) && (string.Equals(ct, "Book", StringComparison.OrdinalIgnoreCase) || string.Equals(ct, "Product", StringComparison.OrdinalIgnoreCase));

            var allowed = new[] { "SinglePartBook", "MultiPartBook", "BookSeries" };
            var cdtOk = !string.IsNullOrWhiteSpace(cdt) && allowed.Any(a => string.Equals(a, cdt, StringComparison.OrdinalIgnoreCase));

            return ctOk && cdtOk;
        }

        private static string? GetTypeFilterReason(AudimetaSearchResult? r)
        {
            if (r == null) return null;
            var ct = r.ContentType?.Trim();
            var cdt = r.ContentDeliveryType?.Trim();

            var ctOk = !string.IsNullOrWhiteSpace(ct) && (string.Equals(ct, "Book", StringComparison.OrdinalIgnoreCase) || string.Equals(ct, "Product", StringComparison.OrdinalIgnoreCase));
            var allowed = new[] { "SinglePartBook", "MultiPartBook", "BookSeries" };
            var cdtOk = !string.IsNullOrWhiteSpace(cdt) && allowed.Any(a => string.Equals(a, cdt, StringComparison.OrdinalIgnoreCase));

            if (ctOk && cdtOk) return $"ContentType='{ct}' AND ContentDeliveryType='{cdt}'";
            if (!ctOk && !cdtOk) return "ContentType not allowed; ContentDeliveryType not allowed";
            if (!ctOk) return $"ContentType='{ct ?? "<null>"}' not allowed";
            return $"ContentDeliveryType='{cdt ?? "<null>"}' not allowed";
        }
    }

    public class AudimetaBookResponse
    {
        public string? Asin { get; set; }
        public string? Title { get; set; }
        public string? Subtitle { get; set; }
        public List<AudimetaAuthor>? Authors { get; set; }
        public List<AudimetaNarrator>? Narrators { get; set; }
        public string? Publisher { get; set; }
        public string? PublishDate { get; set; }
        public string? Description { get; set; }
        public string? ImageUrl { get; set; }
        public int? LengthMinutes { get; set; }
        public string? Language { get; set; }
        public List<AudimetaGenre>? Genres { get; set; }
        public List<AudimetaSeries>? Series { get; set; }
        public bool? Explicit { get; set; }
        public string? ReleaseDate { get; set; }
        public string? Isbn { get; set; }
        public string? Region { get; set; }
        public string? BookFormat { get; set; }
        public string? ContentType { get; set; }
        public string? ContentDeliveryType { get; set; }
        public string? EpisodeType { get; set; }
        public string? Sku { get; set; }
    }

    public class AudimetaAuthor { public string? Asin { get; set; } public string? Name { get; set; } public string? Region { get; set; } }
    public class AudimetaNarrator { public string? Name { get; set; } }
    public class AudimetaGenre { public string? Asin { get; set; } public string? Name { get; set; } public string? Type { get; set; } }
    public class AudimetaSeries { public string? Asin { get; set; } public string? Name { get; set; } public string? Position { get; set; } }

    public class AudimetaSearchResponse { public List<AudimetaSearchResult>? Results { get; set; } public int? TotalResults { get; set; } }

    

    public class AudimetaSearchResult
    {
        public string? Asin { get; set; }
        public string? Title { get; set; }
        public string? Subtitle { get; set; }
        public List<AudimetaAuthor>? Authors { get; set; }
        public string? ImageUrl { get; set; }
        // Runtime fields: audimeta may return different names (runtimeLengthMin, lengthMinutes, runtimeMinutes)
        public int? RuntimeLengthMin { get; set; }
        public int? LengthMinutes { get; set; }
        public int? RuntimeMinutes { get; set; }
        public string? Language { get; set; }
        public string? ContentType { get; set; }
        public string? ContentDeliveryType { get; set; }
        public string? EpisodeType { get; set; }
        public string? Sku { get; set; }
        public string? BookFormat { get; set; }
        public List<AudimetaGenre>? Genres { get; set; }
        public List<AudimetaSeries>? Series { get; set; }
        public string? Publisher { get; set; }
        public List<AudimetaNarrator>? Narrators { get; set; }
        public string? ReleaseDate { get; set; }
        public string? Link { get; set; }
        public string? Isbn { get; set; }
    }

    // Helper types for simple author lookup parsing
    public class AuthorLookupItem { public string? Asin { get; set; } public string? Name { get; set; } public string? Image { get; set; } public string? Region { get; set; } }
    public class AuthorLookupEnvelope { public string? Asin { get; set; } public List<AuthorLookupItem>? Results { get; set; } }
}

