using System.Text.Json;
using System.Text.Json.Serialization;

namespace Listenarr.Api.Services
{
    public class OpenLibraryBook
    {
        [JsonPropertyName("key")]
        public string? Key { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("author_name")]
        public List<string>? AuthorName { get; set; }

        [JsonPropertyName("author_key")]
        public List<string>? AuthorKey { get; set; }

        [JsonPropertyName("first_publish_year")]
        public int? FirstPublishYear { get; set; }

        [JsonPropertyName("isbn")]
        public List<string> Isbn { get; set; } = new();

        [JsonPropertyName("publisher")]
        public List<string>? Publisher { get; set; }

        [JsonPropertyName("cover_i")]
        public int? CoverId { get; set; }

        [JsonPropertyName("edition_count")]
        public int? EditionCount { get; set; }

        [JsonPropertyName("language")]
        public List<string>? Language { get; set; }

        [JsonPropertyName("subject")]
        public List<string>? Subject { get; set; }
    }

    public class OpenLibrarySearchResponse
    {
        [JsonPropertyName("start")]
        public int Start { get; set; }

        [JsonPropertyName("num_found")]
        public int NumFound { get; set; }

        [JsonPropertyName("docs")]
        public List<OpenLibraryBook> Docs { get; set; } = new();
    }

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

                foreach (var book in searchResponse.Docs)
                {
                    if (book.Isbn != null)
                    {
                        foreach (var isbn in book.Isbn)
                        {
                            var cleanIsbn = isbn.Replace("-", "").Replace(" ", "");
                            if (!string.IsNullOrEmpty(cleanIsbn) && (cleanIsbn.Length == 10 || cleanIsbn.Length == 13))
                            {
                                isbns.Add(cleanIsbn);
                            }
                        }
                    }
                }

                return isbns.Distinct().ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
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
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
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
            var matches = System.Text.RegularExpressions.Regex.Matches(lower, @"[a-z0-9\-]+");
            var tokens = new List<string>();
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                var v = m.Value.Trim('-');
                if (!string.IsNullOrEmpty(v)) tokens.Add(v);
            }
            return string.Join("+", tokens);
        }
    }
}
