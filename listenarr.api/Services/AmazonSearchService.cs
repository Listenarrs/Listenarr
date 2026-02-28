
namespace Listenarr.Api.Services
{
    public class AmazonSearchResult
    {
        public string? Asin { get; set; }
        public string? Title { get; set; }
        public string? Author { get; set; }
        public string? ImageUrl { get; set; }
        public string? Price { get; set; }
        public bool IsAudiobook { get; set; }
        // Additional fields parsed from product detail page (audible)
        public string? Series { get; set; }
        public string? SeriesNumber { get; set; }
        public int? RuntimeMinutes { get; set; }
        public string? Narrator { get; set; }
        public string? PublishYear { get; set; }
        public string? Publisher { get; set; }
        public string? Version { get; set; }
        public string? Language { get; set; }
    }

    public interface IAmazonSearchService
    {
        Task<List<AmazonSearchResult>> SearchAudiobooksAsync(string title, string? author = null, CancellationToken ct = default);
        // Scrape the product detail page for an ASIN (product title/author/image) as a fallback
        Task<AmazonSearchResult?> ScrapeProductPageAsync(string asin, CancellationToken ct = default);
    }

    public class AmazonSearchService : IAmazonSearchService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<AmazonSearchService> _logger;
        private readonly IPlaywrightPageFetcher _playwrightFetcher;
        private readonly IAmazonAsinService _amazonAsinService;
        private static readonly Regex AsinRegex = new(@"/dp/([A-Z0-9]{10})", RegexOptions.Compiled);
        // New: placeholder / non-product title patterns
        private static readonly Regex PlaceholderTitleRegex = new(
            @"^(?:\d+[-–]\d+ of \d+ results for|results for|RESULTS FOR|Amazon\.com:|audible session|Audible membership|Best Sellers|Discover more|Unlock\b|Unlock\s+\d+%|Save\b|Savings\b|Visit the\b|Store\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private const int EarlyAsinCap = int.MaxValue; // Temporarily disabled cap for debugging (was 50)

        private bool IsPlaceholderTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return true;
            var t = title.Trim();
            if (t.Length < 3) return true; // too short
            if (PlaceholderTitleRegex.IsMatch(t)) return true;

            // Promotional patterns: percent discounts (e.g., "15%"), "Unlock ... savings", "Visit the X Store"
            if (t.Contains("%") && (t.IndexOf("save", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("savings", StringComparison.OrdinalIgnoreCase) >= 0 || Regex.IsMatch(t, "\\d+%")))
                return true;

            if (Regex.IsMatch(t, "unlock\\s+\\d+%", RegexOptions.IgnoreCase)) return true;
            if (Regex.IsMatch(t, "visit the .*store", RegexOptions.IgnoreCase)) return true;

            // Filter summary-like titles containing 'results for'
            if (t.Contains("results for", StringComparison.OrdinalIgnoreCase) && t.IndexOf('"') == -1)
                return true;
            return false;
        }

        private AmazonSearchResult? ExtractFromLink(HtmlNode linkNode)
        {
            try
            {
                var href = linkNode.GetAttributeValue("href", "");
                var asinMatch = AsinRegex.Match(href);
                if (!asinMatch.Success) return null;

                var asin = asinMatch.Groups[1].Value;
                var title = linkNode.InnerText?.Trim() ?? "Unknown Title";

                // Try to find parent container for more info
                var container = linkNode.ParentNode;
                for (int i = 0; i < 5 && container != null; i++)
                {
                    var titleNode = container.SelectSingleNode(".//span[contains(@class,'a-text-normal')] | .//h2//span | .//h3//span");
                    if (titleNode != null && !string.IsNullOrEmpty(titleNode.InnerText))
                    {
                        title = titleNode.InnerText.Trim();
                        break;
                    }
                    container = container.ParentNode;
                }

                if (IsPlaceholderTitle(title))
                {
                    // Attempt product page fetch for a better title (limited via caller)
                    title = null;
                }

                return new AmazonSearchResult
                {
                    Asin = asin,
                    Title = title,
                    IsAudiobook = true // Assume true since we're searching in Audible category
                };
            }
            catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException) {
                return null;
            }
        }

        public AmazonSearchService(HttpClient httpClient, IPlaywrightPageFetcher playwrightFetcher, ILogger<AmazonSearchService> logger, IAmazonAsinService amazonAsinService)
        {
            _httpClient = httpClient;
            _playwrightFetcher = playwrightFetcher;
            _logger = logger;
            _amazonAsinService = amazonAsinService;
        }

        public async Task<List<AmazonSearchResult>> SearchAudiobooksAsync(string title, string? author = null, CancellationToken ct = default)
        {
            var results = new List<AmazonSearchResult>();

            try
            {
                // Build search query
                var searchQuery = title;
                if (!string.IsNullOrEmpty(author))
                {
                    searchQuery += $" {author}";
                }

                // Try multiple search variations. If the incoming title looks like an ISBN
                // prefer the ISBN/Books (stripbooks) path and DO NOT try Audible index
                // variations which frequently return unrelated Audible results for ISBNs.
                var digitsOnlyCheck = Regex.Replace(searchQuery ?? string.Empty, "\\D", "");
                List<string> searchVariations;
                bool isIsbnSearch = false;
                if (!string.IsNullOrEmpty(digitsOnlyCheck) && (digitsOnlyCheck.Length == 10 || digitsOnlyCheck.Length == 13))
                {
                    // For ISBN searches, only use the cleaned digits (no extra suffixes)
                    _logger.LogInformation("ISBN-only search detected for {Query}; will search ONLY Amazon Books stripbooks (not Audible)", searchQuery);
                    searchVariations = new List<string> { digitsOnlyCheck };
                    isIsbnSearch = true;
                }
                else
                {
                    // General title searches: try audiobook/audible variants
                    searchVariations = new List<string>
                    {
                        $"{(searchQuery ?? string.Empty)} audiobook",
                        $"{(searchQuery ?? string.Empty)} audible",
                        (searchQuery ?? string.Empty)
                    };
                }

                foreach (var query in searchVariations)
                {
                    var searchResults = await SearchAmazonAsync(query, ct, isIsbnSearch);
                    results.AddRange(searchResults);

                    // Stop if we found a larger set of results (increase from 10 to 50)
                    if (results.Count >= 50) break;
                }

                // Remove duplicates based on ASIN & discard placeholder titles if any slipped
                results = results
                    .Where(r => !string.IsNullOrEmpty(r.Asin))
                    .GroupBy(r => r.Asin)
                    .Select(g => g.First())
                    .Where(r => !IsPlaceholderTitle(r.Title))
                    .Take(EarlyAsinCap) // enforce cap again at aggregation
                    .ToList();

                return results;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error searching Amazon for audiobooks: {Title} by {Author}", title, author);
                return results;
            }
        }

        private async Task<List<AmazonSearchResult>> SearchAmazonAsync(string query, CancellationToken ct = default, bool isIsbnSearch = false)
        {
            var results = new List<AmazonSearchResult>();
            var seenAsins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int productPageFetches = 0;
            // Track titles / ASINs filtered out due to placeholder detection for diagnostics
            var filteredOut = new List<(string Asin, string? Title, string Reason)>();

            try
            {
                // If the query looks like an ISBN (digits only, length 10 or 13), search the books index
                // using the ISBN filter (p_66) to get exact ISBN matches. Otherwise search Audible index.
                // Additionally: callers may pass a full Amazon "stripbooks" URL. Detect that and
                // extract the ISBN (p_66) so we use the intended Books search rather than
                // re-encoding the whole URL into a k= parameter which yields wrong results.
                // IMPORTANT: For ISBN searches, we ONLY scrape the Books/stripbooks page, NOT Audible.
                var digitsOnly = Regex.Replace(query ?? string.Empty, "\\D", "");
                string searchUrl;

                // Attempt to detect an Amazon stripbooks URL passed as the query and extract p_66 ISBN
                try
                {
                    if (!string.IsNullOrWhiteSpace(query) && Uri.IsWellFormedUriString(query, UriKind.Absolute))
                    {
                        var incomingUri = new Uri(query);
                        if (incomingUri.Host.IndexOf("amazon.", StringComparison.OrdinalIgnoreCase) >= 0 && incomingUri.AbsolutePath.StartsWith("/s", StringComparison.OrdinalIgnoreCase))
                        {
                            var qs = System.Web.HttpUtility.ParseQueryString(incomingUri.Query);
                            var rh = qs.Get("rh");
                            if (!string.IsNullOrEmpty(rh))
                            {
                                // rh may be encoded; try to pull p_66:ISBN pattern
                                var rhDecoded = WebUtility.UrlDecode(rh);
                                var m = Regex.Match(rhDecoded ?? string.Empty, "p_66[:%3A]*(\\d{10,13})");
                                if (m.Success && m.Groups.Count > 1)
                                {
                                    digitsOnly = m.Groups[1].Value;
                                }
                            }

                            // If we successfully extracted an ISBN, prefer the stripbooks path
                            if (!string.IsNullOrEmpty(digitsOnly) && (digitsOnly.Length == 10 || digitsOnly.Length == 13))
                            {
                                _logger.LogDebug("Detected incoming Amazon stripbooks URL; extracted ISBN {Isbn}", digitsOnly);
                                searchUrl = $"https://www.amazon.com/s?i=stripbooks&rh=p_66%3A{Uri.EscapeDataString(digitsOnly)}&s=relevancerank&Adv-Srch-Books-Submit.x=20&Adv-Srch-Books-Submit.y=6&unfiltered=1";
                                goto BuildComplete;
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    _logger.LogDebug(ex, "Failed to parse incoming query as URL for special handling: {Query}", query);
                }

                if (!string.IsNullOrEmpty(digitsOnly) && (digitsOnly.Length == 10 || digitsOnly.Length == 13))
                {
                    // Exact-ISBN search in Books (stripbooks) using p_66 filter with all required parameters
                    searchUrl = $"https://www.amazon.com/s?i=stripbooks&rh=p_66%3A{Uri.EscapeDataString(digitsOnly)}&s=relevancerank&Adv-Srch-Books-Submit.x=20&Adv-Srch-Books-Submit.y=6&unfiltered=1";
                }
                else
                {
                    // Search specifically in Audible store for general queries
                    searchUrl = $"https://www.amazon.com/s?k={Uri.EscapeDataString(query ?? string.Empty)}&i=audible&ref=sr_nr_n_1";
                }
            BuildComplete: ;

                _logger.LogInformation("Searching Amazon: {SearchUrl}", searchUrl);

                var html = await GetHtmlAsync(searchUrl, ct);
                if (string.IsNullOrEmpty(html))
                {
                    return results;
                }

                // Parse search results
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                // Debug: Log HTML length and check for bot detection
                _logger.LogInformation("Amazon HTML response length: {Length} characters", html.Length);

                if (html.Contains("To discuss automated access to Amazon data please contact"))
                {
                    _logger.LogWarning("Amazon bot detection triggered");
                    return results;
                }
                
                // For ISBN searches, we ONLY scrape the stripbooks page results - no second-pass searches
                if (isIsbnSearch)
                {
                    _logger.LogInformation("ISBN search mode: extracting ASINs from Books/stripbooks results only (skipping Audible search)");
                }

                // Find search result items using modern Amazon selectors
                var resultNodes = doc.DocumentNode.SelectNodes(
                    "//div[@data-component-type='s-search-result'] | " +
                    "//div[contains(@class,'s-result-item')] | " +
                    "//div[contains(@class,'s-card-container')] | " +
                    "//div[contains(@class,'sg-col-inner')] | " +
                    "//div[contains(@class,'s-widget-container')] | " +
                    "//span[contains(@class,'rush-component')] | " +
                    "//div[contains(@data-index,'')][contains(@class,'s-result')] | " +
                    "//div[contains(@class,'AdHolder')] | " +
                    "//div[contains(@class,'s-expand-height')] | " +
                    "//article | " +
                    "//div[.//a[contains(@href,'/dp/')]] | " +
                    "//div[.//a[contains(@href,'/gp/product/')]] | " +
                    "//li[contains(@class,'s-result-item')]"
                );

                _logger.LogInformation("Found {Count} result nodes using XPath selector", resultNodes?.Count ?? 0);

                if (resultNodes != null)
                {
                    foreach (var node in resultNodes.Take(200)) // Increase scan to first 200 nodes to capture more candidates
                    {
                        ct.ThrowIfCancellationRequested();
                        if (results.Count >= EarlyAsinCap) break; // early cap
                        var result = await ExtractSearchResult(node, ct);
                        if (result != null && !string.IsNullOrEmpty(result.Asin) && seenAsins.Add(result.Asin))
                        {
                            // For ISBN queries, accept ASINs from stripbooks results without strict verification.
                            // Audiobooks don't list print ISBNs on their pages, so verification would reject valid results.
                            // Let the metadata enrichment and scoring determine relevance.
                            if (!string.IsNullOrEmpty(digitsOnly) && (digitsOnly.Length == 10 || digitsOnly.Length == 13))
                            {
                                // Optionally fetch a nicer title if we only had a placeholder
                                if (IsPlaceholderTitle(result.Title) && productPageFetches < 100)
                                {
                                    try
                                    {
                                        var fetched = await FetchProductTitleAsync(result.Asin, ct);
                                        if (!IsPlaceholderTitle(fetched))
                                        {
                                            result.Title = fetched;
                                            productPageFetches++;
                                        }
                                    }
                                    catch (Exception pex) when (pex is not OperationCanceledException && pex is not OutOfMemoryException && pex is not StackOverflowException) {
                                        _logger.LogDebug(pex, "Product page title fetch failed for {Asin}", result.Asin);
                                    }
                                }
                                results.Add(result);
                                _logger.LogInformation("Amazon candidate (ISBN): {Asin} Title={Title} Author={Author} Image={ImageUrl}", result.Asin, result.Title, result.Author, result.ImageUrl);
                            }
                            else
                            {
                                // Non-ISBN flow unchanged
                                if (IsPlaceholderTitle(result.Title))
                                {
                                    // attempt fetch product page title if budget remains (increased budget)
                                    if (productPageFetches < 100)
                                    {
                                        try
                                        {
                                            var fetched = await FetchProductTitleAsync(result.Asin, ct);
                                            if (!IsPlaceholderTitle(fetched))
                                            {
                                                result.Title = fetched;
                                                productPageFetches++;
                                            }
                                        }
                                        catch (Exception pex) when (pex is not OperationCanceledException && pex is not OutOfMemoryException && pex is not StackOverflowException) {
                                            _logger.LogDebug(pex, "Product page title fetch failed for {Asin}", result.Asin);
                                        }
                                    }
                                }
                                if (!IsPlaceholderTitle(result.Title))
                                {
                                    results.Add(result);
                                    _logger.LogInformation("Amazon candidate: {Asin} Title={Title} Author={Author} Image={ImageUrl}", result.Asin, result.Title, result.Author, result.ImageUrl);
                                    // If author was missing/placeholder, try fetching it from the product page (budget permitting)
                                    if (string.IsNullOrWhiteSpace(result.Author) && productPageFetches < 100)
                                    {
                                        try
                                        {
                                            var fetchedAuthor = await FetchProductAuthorAsync(result.Asin, ct);
                                            if (!string.IsNullOrWhiteSpace(fetchedAuthor))
                                            {
                                                result.Author = fetchedAuthor;
                                                _logger.LogInformation("Fetched product page author for {Asin}: {Author}", result.Asin, result.Author);
                                            }
                                            productPageFetches++;
                                        }
                                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                                            _logger.LogDebug(ex, "Product page author fetch failed for {Asin}", result.Asin);
                                        }
                                    }
                                }
                                else
                                {
                                    // Still a placeholder after any fetch attempts - record for diagnostics
                                    filteredOut.Add((result.Asin ?? "<unknown>", result.Title, "placeholder-after-fetch"));
                                    _logger.LogInformation("Filtered out Amazon candidate {Asin} Title={Title} Reason={Reason}", result.Asin, result.Title, "placeholder-after-fetch");
                                    _logger.LogDebug("Filtering out ASIN {Asin} due to placeholder title (after fetch attempts): {Title}", result.Asin, result.Title);
                                }
                            }
                        }
                    }
                }

                // Fallback: If no results, try finding any links with ASINs
                if (!results.Any())
                {
                    _logger.LogInformation("No results with primary selectors, trying ASIN link fallback");
                    var asinLinks = doc.DocumentNode.SelectNodes("//a[contains(@href,'/dp/')]");
                    if (asinLinks != null)
                    {
                        foreach (var link in asinLinks.Take(200))
                        {
                            ct.ThrowIfCancellationRequested();
                            if (results.Count >= EarlyAsinCap) break;
                            var result = ExtractFromLink(link);
                            if (result != null && !string.IsNullOrEmpty(result.Asin) && seenAsins.Add(result.Asin))
                            {
                                if (IsPlaceholderTitle(result.Title) && productPageFetches < 100)
                                {
                                    try
                                    {
                                        var fetched = await FetchProductTitleAsync(result.Asin, ct);
                                        if (!IsPlaceholderTitle(fetched))
                                        {
                                            result.Title = fetched;
                                            productPageFetches++;
                                        }
                                    }
                                    catch (Exception dpex) when (dpex is not OperationCanceledException && dpex is not OutOfMemoryException && dpex is not StackOverflowException) {
                                        _logger.LogDebug(dpex, "Failed to fetch product page for ASIN {Asin}", result.Asin);
                                    }
                                }
                                if (!IsPlaceholderTitle(result.Title))
                                {
                                    results.Add(result);
                                    _logger.LogInformation("Amazon candidate (link-fallback): {Asin} Title={Title} Author={Author} Image={ImageUrl}", result.Asin, result.Title, result.Author, result.ImageUrl);
                                }
                                else
                                {
                                    filteredOut.Add((result.Asin ?? "<unknown>", result.Title, "link-fallback-placeholder"));
                                    _logger.LogInformation("Filtered out Amazon candidate {Asin} Title={Title} Reason={Reason}", result.Asin, result.Title, "link-fallback-placeholder");
                                    _logger.LogDebug("Filtering out ASIN {Asin} from link fallback due to placeholder title: {Title}", result.Asin, result.Title);
                                }
                            }
                        }
                    }
                }

                // Second-level fallback: regex scan entire HTML for /dp/ASIN patterns if still empty
                if (!results.Any())
                {
                    _logger.LogInformation("No results after DOM link fallback. Running regex ASIN scan.");
                    try
                    {
                        var asinMatches = AsinRegex.Matches(html);
                        foreach (Match m in asinMatches)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (results.Count >= EarlyAsinCap) break;
                            if (!m.Success) continue;
                            var asin = m.Groups[1].Value;
                            if (!seenAsins.Add(asin)) continue;

                            var anchor = doc.DocumentNode.SelectSingleNode($"//a[contains(@href,'/dp/{asin}')]");
                            var titleGuess = anchor?.InnerText?.Trim() ?? "Unknown Title";
                            if (IsPlaceholderTitle(titleGuess) && productPageFetches < 20)
                            {
                                try
                                {
                                    var fetched = await FetchProductTitleAsync(asin, ct);
                                    if (!IsPlaceholderTitle(fetched))
                                    {
                                        titleGuess = fetched!;
                                        productPageFetches++;
                                    }
                                }
                                catch (Exception dpex) when (dpex is not OperationCanceledException && dpex is not OutOfMemoryException && dpex is not StackOverflowException) {
                                    _logger.LogDebug(dpex, "Failed to fetch product page for ASIN {Asin}", asin);
                                }
                            }
                            if (IsPlaceholderTitle(titleGuess))
                            {
                                filteredOut.Add((asin, titleGuess, "regex-placeholder"));
                                _logger.LogInformation("Filtered out Amazon candidate {Asin} Title={Title} Reason={Reason}", asin, titleGuess, "regex-placeholder");
                                _logger.LogDebug("Filtering out ASIN {Asin} from regex fallback due to placeholder title: {Title}", asin, titleGuess);
                                continue;
                            }
                            var newResult = new AmazonSearchResult
                            {
                                Asin = asin,
                                Title = titleGuess,
                                IsAudiobook = true
                            };
                            results.Add(newResult);
                            _logger.LogInformation("Amazon candidate (regex-fallback): {Asin} Title={Title}", newResult.Asin, newResult.Title);
                        }
                        _logger.LogInformation("Regex fallback added {Count} results", results.Count);
                    }
                    catch (Exception rex) when (rex is not OperationCanceledException && rex is not OutOfMemoryException && rex is not StackOverflowException) {
                        _logger.LogWarning(rex, "Regex ASIN fallback failed");
                    }
                }

                _logger.LogInformation("Found {Count} Amazon search results for query: {Query}", results.Count, query);

                // Diagnostic: log how many candidates were filtered out due to placeholder titles
                if (filteredOut.Any())
                {
                    var sample = string.Join(", ", filteredOut.Take(10).Select(f => $"{f.Asin}:{(string.IsNullOrWhiteSpace(f.Title) ? "<empty>" : f.Title)}[{f.Reason}]"));
                    _logger.LogInformation("Filtered out {Count} ASIN candidates due to placeholder titles (showing up to 10): {Sample}", filteredOut.Count, sample);
                }
                return results;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error parsing Amazon search results for query: {Query}", LogRedaction.SanitizeText(query));
                return results;
            }
        }

        private async Task<AmazonSearchResult?> ExtractSearchResult(HtmlNode node, CancellationToken ct = default)
        {
            try
            {
                // Extract ASIN
                var asinLink = node.SelectSingleNode(".//a[contains(@href,'/dp/')]")?.GetAttributeValue("href", "");
                if (string.IsNullOrEmpty(asinLink)) return null;

                var asinMatch = AsinRegex.Match(asinLink);
                if (!asinMatch.Success) return null;

                var asin = asinMatch.Groups[1].Value;

                // Extract title
                var titleNode = node.SelectSingleNode(
                    ".//span[contains(@class,'a-size-base-plus')] | " +
                    ".//span[contains(@class,'a-size-medium')] | " +
                    ".//h2//span | " +
                    ".//a//span[contains(@class,'a-text-normal')]"
                );
                var title = titleNode?.InnerText.Trim();

                // Extract author/narrator info. Author may be split across multiple spans like:
                // <div class="a-row"><span class="a-size-base">by </span><span class="a-size-base">J.K. Rowling</span><span class="a-size-base">, </span><span class="a-size-base">Jim Dale</span>...</div>
                string? author = null;
                var authorContainer = node.SelectSingleNode(".//div[contains(@class,'a-row') and .//span[contains(@class,'a-size-base')]]");
                if (authorContainer != null)
                {
                    var spanNodes = authorContainer.SelectNodes(".//span[contains(@class,'a-size-base')]");
                    if (spanNodes != null && spanNodes.Count > 0)
                    {
                        // Only treat this container as an author list if it contains an explicit 'by' token
                        var hasByToken = spanNodes.Any(s =>
                        {
                            var txt = s.InnerText?.Trim();
                            return !string.IsNullOrWhiteSpace(txt) && (txt.Equals("by", StringComparison.OrdinalIgnoreCase) || txt.StartsWith("by ", StringComparison.OrdinalIgnoreCase));
                        });

                        if (hasByToken)
                        {
                            var parts = new List<string>();
                            foreach (var s in spanNodes)
                            {
                                var text = s.InnerText?.Trim();
                                if (string.IsNullOrWhiteSpace(text)) continue;
                                var lower = text.Trim();
                                // Skip the leading 'by' token
                                if (string.Equals(lower, "by", StringComparison.OrdinalIgnoreCase) || lower.StartsWith("by ", StringComparison.OrdinalIgnoreCase)) continue;
                                // filter out simple punctuation-only spans
                                if (lower.All(c => char.IsPunctuation(c) || char.IsWhiteSpace(c))) continue;
                                // Normalize: remove surrounding commas and trim
                                var cleanedPart = text.Trim().Trim(',').Trim();
                                if (string.IsNullOrWhiteSpace(cleanedPart)) continue;
                                parts.Add(cleanedPart);
                            }
                            if (parts.Any())
                            {
                                author = string.Join(", ", parts);
                            }
                        }
                    }
                }
                // Fallback: try common single-node author selectors
                if (string.IsNullOrWhiteSpace(author))
                {
                    var authorNode = node.SelectSingleNode(
                        ".//a[contains(@href,'/author')]|.//a[contains(@href,'author')]|.//span[contains(text(),'by')]"
                    );
                    author = authorNode?.InnerText.Trim();
                }

                // Clean up author text and guard against placeholder values like "Sort by:" or stray UI text
                if (!string.IsNullOrEmpty(author))
                {
                    var cleaned = author.Replace("by ", "").Replace("By ", "").Trim();
                    // Remove stray UI fragments that sometimes appear in search results
                    if (cleaned.StartsWith("Sort by", StringComparison.OrdinalIgnoreCase) || cleaned.Equals("Sort by:", StringComparison.OrdinalIgnoreCase))
                    {
                        cleaned = null;
                    }
                    author = string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
                }

                // If author is missing or invalid (e.g. came back as 'by' or UI text), leave null here.
                // We will attempt product-page author fetch later from the async caller where budget is available.

                // Extract image (normalize many Amazon image attribute variants)
                var imageNode = node.SelectSingleNode(".//img");
                string? imageUrl = null;
                if (imageNode != null)
                {
                    imageUrl = imageNode.GetAttributeValue("src", null)
                               ?? imageNode.GetAttributeValue("srcset", null)
                               ?? imageNode.GetAttributeValue("data-src", null)
                               ?? imageNode.GetAttributeValue("data-srcset", null)
                               ?? imageNode.GetAttributeValue("data-a-dynamic-image", null)
                               ?? imageNode.GetAttributeValue("data-image", null)
                               ?? imageNode.GetAttributeValue("data-lazy-src", null);

                    // If it's a srcset or comma-separated list, parse entries and prefer CDN hosts
                    List<string> candidates = new();
                    if (!string.IsNullOrWhiteSpace(imageUrl))
                    {
                        // data-a-dynamic-image JSON -> extract keys
                        if (imageUrl.TrimStart().StartsWith("{"))
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(imageUrl);
                                var root = doc.RootElement;
                                if (root.ValueKind == JsonValueKind.Object)
                                {
                                    foreach (var prop in root.EnumerateObject())
                                    {
                                        candidates.Add(prop.Name);
                                    }
                                }
                            }
                            catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException) { 
                                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                            }
                        }
                        else
                        {
                            // srcset or comma-separated values
                            try
                            {
                                var parts = imageUrl.Split(',').Select(p => p.Trim());
                                foreach (var part in parts)
                                {
                                    if (string.IsNullOrWhiteSpace(part)) continue;
                                    var url = part.Split(' ')[0];
                                    if (!string.IsNullOrWhiteSpace(url)) candidates.Add(url);
                                }
                            }
                            catch (Exception caughtEx_3) when (caughtEx_3 is not OperationCanceledException && caughtEx_3 is not OutOfMemoryException && caughtEx_3 is not StackOverflowException) { 
                                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                            }
                        }
                    }

                    // Always include direct src if present and not already in candidates
                    var direct = imageNode.GetAttributeValue("src", null);
                    if (!string.IsNullOrWhiteSpace(direct) && !candidates.Contains(direct)) candidates.Insert(0, direct);

                    // Normalize and pick best candidate: prefer known CDN hosts, avoid batch/placeholder hosts
                    string[] preferredHosts = new[] { "m.media-amazon.com", "images-na.ssl-images-amazon.com", "m.media-amazon.com" };
                    string[] ignorePatterns = new[] { "fls-na.amazon.com/1/batch", "/batch/", "fls-na.amazon.com" };

                    string? chosen = null;
                    foreach (var c in candidates)
                    {
                        if (string.IsNullOrWhiteSpace(c)) continue;
                        var norm = c.Trim();
                        if (norm.StartsWith("//")) norm = "https:" + norm;
                        else if (norm.StartsWith("/")) norm = "https://www.amazon.com" + norm;
                        // skip obvious batch/placeholder urls
                        if (ignorePatterns.Any(p => norm.Contains(p))) continue;
                        // pick first preferred host match
                        if (preferredHosts.Any(h => norm.Contains(h)))
                        {
                            chosen = norm; break;
                        }
                        if (chosen == null) chosen = norm; // fallback
                    }
                    imageUrl = chosen;

                    // Validate candidate image early: discard 1x1 or non-square images to avoid placeholders
                    if (!string.IsNullOrWhiteSpace(imageUrl))
                    {
                        try
                        {
                            var isSquare = await IsImageSquareAsync(imageUrl, ct);
                            if (!isSquare)
                            {
                                _logger.LogInformation("Discarding non-square or placeholder image for candidate ASIN {Asin}: {ImageUrl}", asin, imageUrl);
                                imageUrl = null;
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                            _logger.LogDebug(ex, "Image validation failed for candidate image {Url}", imageUrl);
                        }
                    }
                }

                // Extract price
                var priceNode = node.SelectSingleNode(
                    ".//span[contains(@class,'a-price-whole')] | " +
                    ".//span[contains(@class,'a-price')] | " +
                    ".//span[contains(text(),'$')]"
                );
                var price = priceNode?.InnerText.Trim();

                // Check if it's likely an audiobook
                var nodeText = node.InnerText.ToLower();
                var isAudiobook = nodeText.Contains("audible") ||
                                  nodeText.Contains("audiobook") ||
                                  nodeText.Contains("narrator") ||
                                  nodeText.Contains("narrated by");

                return new AmazonSearchResult
                {
                    Asin = asin,
                    Title = title,
                    Author = author,
                    ImageUrl = imageUrl,
                    Price = price,
                    IsAudiobook = isAudiobook
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogWarning(ex, "Error extracting search result from Amazon node");
                return null;
            }
        }

        private async Task<bool> IsImageSquareAsync(string imageUrl, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(imageUrl)) return false;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, imageUrl);
                using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode) return false;

                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[64 * 1024];
                var read = 0;
                while (read < buffer.Length)
                {
                    var r = await stream.ReadAsync(buffer, read, buffer.Length - read, ct);
                    if (r == 0) break;
                    read += r;
                }
                var content = new byte[read];
                Array.Copy(buffer, content, read);
                var dims = TryGetImageDimensions(content);
                if (dims == null) return false;
                var (w, h) = dims.Value;
                if (w <= 1 || h <= 1) return false;
                return w == h;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogDebug(ex, "IsImageSquareAsync failed for URL {Url}", imageUrl);
                return false;
            }
        }

        private static (int width, int height)? TryGetImageDimensions(byte[] data)
        {
            if (data == null || data.Length < 10) return null;

            // PNG
            if (data.Length >= 24 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            {
                try
                {
                    var width = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16, 4));
                    var height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20, 4));
                    return (width, height);
                }
                catch (Exception caughtEx_4) when (caughtEx_4 is not OperationCanceledException && caughtEx_4 is not OutOfMemoryException && caughtEx_4 is not StackOverflowException) { return null; }
            }

            // GIF
            if (data.Length >= 10 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F')
            {
                try
                {
                    var width = BitConverter.ToUInt16(data, 6);
                    var height = BitConverter.ToUInt16(data, 8);
                    return ((int)width, (int)height);
                }
                catch (Exception caughtEx_5) when (caughtEx_5 is not OperationCanceledException && caughtEx_5 is not OutOfMemoryException && caughtEx_5 is not StackOverflowException) { return null; }
            }

            // JPEG
            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xD8)
            {
                int offset = 2;
                while (offset + 9 < data.Length)
                {
                    if (data[offset] != 0xFF) break;
                    var marker = data[offset + 1];
                    if (marker == 0xC0 || marker == 0xC2)
                    {
                        try
                        {
                            var length = (data[offset + 2] << 8) + data[offset + 3];
                            if (offset + 5 + 4 >= data.Length) return null;
                            var height = (data[offset + 5] << 8) + data[offset + 6];
                            var width = (data[offset + 7] << 8) + data[offset + 8];
                            return (width, height);
                        }
                        catch (Exception caughtEx_6) when (caughtEx_6 is not OperationCanceledException && caughtEx_6 is not OutOfMemoryException && caughtEx_6 is not StackOverflowException) { return null; }
                    }
                    else
                    {
                        if (offset + 4 >= data.Length) break;
                        var segLen = (data[offset + 2] << 8) + data[offset + 3];
                        if (segLen < 2) break;
                        offset += 2 + segLen;
                    }
                }
            }

            return null;
        }

        private async Task<string?> GetHtmlAsync(string url, CancellationToken ct = default)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);

                // Add realistic headers
                request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
                request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
                request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
                request.Headers.Add("Accept-Encoding", "gzip, deflate, br");
                request.Headers.Add("DNT", "1");
                request.Headers.Add("Connection", "keep-alive");
                request.Headers.Add("Upgrade-Insecure-Requests", "1");

                var response = await _httpClient.SendAsync(request, ct);
                var body = await response.Content.ReadAsStringAsync(ct);

                // If the response is not successful or looks like a bot/challenge page, try Playwright fallback
                if (!response.IsSuccessStatusCode ||
                    response.StatusCode == HttpStatusCode.Forbidden ||
                    response.StatusCode == (HttpStatusCode)429 ||
                    response.StatusCode == HttpStatusCode.ServiceUnavailable ||
                    (body != null && (body.Contains("captcha", StringComparison.OrdinalIgnoreCase) || body.Contains("To discuss automated access to Amazon data please contact", StringComparison.OrdinalIgnoreCase))))
                {
                    _logger.LogInformation("HttpClient fetch returned status {Status} or challenge; falling back to Playwright for {Url}", (int)response.StatusCode, LogRedaction.SanitizeUrl(url));
                    try
                    {
                        var pw = await _playwrightFetcher.FetchAsync(url);
                        if (!string.IsNullOrWhiteSpace(pw)) return pw;
                    }
                    catch (Exception pex) when (pex is not OperationCanceledException && pex is not OutOfMemoryException && pex is not StackOverflowException) {
                        _logger.LogDebug(pex, "Playwright fallback failed for {Url}", LogRedaction.SanitizeUrl(url));
                    }
                }

                if (!response.IsSuccessStatusCode)
                {
                    response.EnsureSuccessStatusCode();
                }

                return body;
            }
            catch (Polly.CircuitBreaker.BrokenCircuitException<System.Net.Http.HttpResponseMessage> ex)
            {
                // Circuit breaker is open - try Playwright fallback
                _logger.LogWarning(ex, "Amazon search circuit open; attempting Playwright fallback for {Url}", LogRedaction.SanitizeUrl(url));
                try
                {
                    var pw = await _playwrightFetcher.FetchAsync(url);
                    return pw;
                }
                catch (Exception pex) when (pex is not OperationCanceledException && pex is not OutOfMemoryException && pex is not StackOverflowException) {
                    _logger.LogWarning(pex, "Playwright fallback also failed for {Url}", LogRedaction.SanitizeUrl(url));
                    return null;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error fetching HTML from URL: {Url}", LogRedaction.SanitizeUrl(url));
                // Try Playwright as a last resort
                try
                {
                    var pw = await _playwrightFetcher.FetchAsync(url);
                    return pw;
                }
                catch (Exception pex) when (pex is not OperationCanceledException && pex is not OutOfMemoryException && pex is not StackOverflowException) {
                    _logger.LogDebug(pex, "Playwright fallback failed for {Url}", LogRedaction.SanitizeUrl(url));
                    return null;
                }
            }
        }

        private async Task<string?> FetchProductTitleAsync(string asin, CancellationToken ct = default)
        {
            try
            {
                var url = $"https://www.amazon.com/dp/{asin}";
            var html = await GetHtmlAsync(url, ct);
                if (string.IsNullOrEmpty(html)) return null;
                var doc = new HtmlDocument();
                doc.LoadHtml(html);
                var titleNode = doc.DocumentNode.SelectSingleNode("//span[@id='productTitle'] | //h1//span | //h1");
                var title = titleNode?.InnerText?.Trim();
                if (string.IsNullOrWhiteSpace(title))
                {
                    // Try meta og:title
                    var ogNode = doc.DocumentNode.SelectSingleNode("//meta[@property='og:title']");
                    title = ogNode?.GetAttributeValue("content", null)?.Trim() ?? title;
                }
                if (string.IsNullOrWhiteSpace(title))
                {
                    // Try document <title>
                    var pageTitle = doc.DocumentNode.SelectSingleNode("//title")?.InnerText;
                    if (!string.IsNullOrWhiteSpace(pageTitle))
                    {
                        // Amazon title format: "Amazon.com: Product Title: Optional Subtitle | Category"
                        // Prefer the second segment (product title) over "Amazon.com" prefix
                        var segments = pageTitle.Split('|', '\u2013' /* en dash */, '\u2014' /* em dash */);
                        if (segments.Length > 1)
                        {
                            // Take second-to-last or second segment to get product name
                            var candidate = segments.Length > 2 ? segments[segments.Length - 2].Trim() : segments[0].Trim();
                            if (!string.IsNullOrWhiteSpace(candidate) && !candidate.Equals("Amazon.com", StringComparison.OrdinalIgnoreCase))
                            {
                                title = candidate.Replace("Amazon.com:", "").Trim();
                            }
                        }
                        else
                        {
                            // Fallback: clean the single segment
                            var cleaned = segments[0].Trim();
                            if (!string.IsNullOrWhiteSpace(cleaned) && !cleaned.Equals("Amazon.com", StringComparison.OrdinalIgnoreCase))
                            {
                                title = cleaned.Replace("Amazon.com:", "").Trim();
                            }
                        }
                    }
                }
                // Filter placeholders / generic phrases
                if (!string.IsNullOrWhiteSpace(title) && (title.StartsWith("Amazon.com:") || title.Contains("Audible Audiobook")))
                {
                    // Strip leading Amazon.com: and descriptor
                    title = title.Replace("Amazon.com:", "").Trim();
                }
                if (!string.IsNullOrWhiteSpace(title))
                {
                    _logger.LogInformation("Fetched product page title for {Asin}: {Title}", asin, title);
                }
                return title;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogDebug(ex, "Error fetching product title for {Asin}", asin);
                return null;
            }
        }

        private async Task<string?> FetchProductAuthorAsync(string asin, CancellationToken ct = default)
        {
            try
            {
                var url = $"https://www.amazon.com/dp/{asin}";
            var html = await GetHtmlAsync(url, ct);
                if (string.IsNullOrEmpty(html)) return null;
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                // Try common product-page author selectors
                var authorNode = doc.DocumentNode.SelectSingleNode("//a[contains(@href,'/author') or contains(@href,'/people/') or contains(@class,'contributorNameID') or @id='bylineInfo']")
                                 ?? doc.DocumentNode.SelectSingleNode("//span[contains(@class,'author')]")
                                 ?? doc.DocumentNode.SelectSingleNode("//div[@id='bylineInfo']//a")
                                 ?? doc.DocumentNode.SelectSingleNode("//a[contains(@class,'a-link-normal') and contains(@href,'/author')]");

                var author = authorNode?.InnerText?.Trim();

                // Try meta author as fallback
                if (string.IsNullOrWhiteSpace(author))
                {
                    author = doc.DocumentNode.SelectSingleNode("//meta[@name='author']")?.GetAttributeValue("content", null)?.Trim();
                }

                if (!string.IsNullOrWhiteSpace(author))
                {
                    // Remove common prefixes and trailing UI tokens like "(Author)" which sometimes appear
                    // Example: "J. K. Rowling (Author)," -> "J. K. Rowling"
                    author = author.Replace("by ", "").Replace("By ", "").Trim();
                    // Strip trailing "(Author)" or similar with optional comma/whitespace
                    try
                    {
                        author = System.Text.RegularExpressions.Regex.Replace(author, "\\(Author\\)[,\\s]*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
                        // Also handle cases where parentheses are missing: trailing 'Author,' etc.
                        author = System.Text.RegularExpressions.Regex.Replace(author, @",?\s*Author[,\s]*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
                    }
                    catch (Exception caughtEx_7) when (caughtEx_7 is not OperationCanceledException && caughtEx_7 is not OutOfMemoryException && caughtEx_7 is not StackOverflowException) { 
                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                    }

                    if (!string.IsNullOrWhiteSpace(author))
                    {
                        _logger.LogInformation("Fetched product page author for {Asin}: {Author}", asin, author);
                        return author;
                    }
                }
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogDebug(ex, "Error fetching product author for {Asin}", asin);
                return null;
            }
        }

        // Public wrapper to allow callers to scrape product detail pages (uses Playwright fallback when needed)
        public async Task<AmazonSearchResult?> ScrapeProductPageAsync(string asin, CancellationToken ct = default)
        {
            try
            {
            var title = await FetchProductTitleAsync(asin, ct);
            var author = await FetchProductAuthorAsync(asin, ct);

                // Single HTML fetch and structured parsing for image + product details
                string? image = null;
                string? series = null;
                string? seriesNumber = null;
                int? runtimeMinutes = null;
                string? narrator = null;
                string? publishYear = null;
                string? publisher = null;
                string? version = null;
                string? language = null;

                var url = $"https://www.amazon.com/dp/{asin}";
                var html = await GetHtmlAsync(url, ct);
                if (!string.IsNullOrWhiteSpace(html))
                {
                    var doc = new HtmlDocument();
                    doc.LoadHtml(html);

                    // Image extraction (handle src, srcset, data-a-dynamic-image JSON)
                    try
                    {
                        var imgNode = doc.DocumentNode.SelectSingleNode("//img[@id='imgBlkFront'] | //img[@id='landingImage'] | //img[contains(@class,'amazon') or contains(@id,'landingImage')]");
                        if (imgNode != null)
                        {
                            var src = imgNode.GetAttributeValue("src", null)
                                      ?? imgNode.GetAttributeValue("data-src", null)
                                      ?? imgNode.GetAttributeValue("data-a-dynamic-image", null)
                                      ?? imgNode.GetAttributeValue("srcset", null);

                            var candidates = new List<string>();
                            if (!string.IsNullOrWhiteSpace(src))
                            {
                                if (src.TrimStart().StartsWith("{"))
                                {
                                    try
                                    {
                                        using var docj = JsonDocument.Parse(src);
                                        var root = docj.RootElement;
                                        if (root.ValueKind == JsonValueKind.Object)
                                        {
                                            foreach (var prop in root.EnumerateObject()) candidates.Add(prop.Name);
                                        }
                                    }
                                    catch (Exception caughtEx_8) when (caughtEx_8 is not OperationCanceledException && caughtEx_8 is not OutOfMemoryException && caughtEx_8 is not StackOverflowException) { 
                                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                    }
                                }
                                else if (src.Contains(","))
                                {
                                    try
                                    {
                                        foreach (var part in src.Split(','))
                                        {
                                            var u = part.Trim().Split(' ')[0]; if (!string.IsNullOrWhiteSpace(u)) candidates.Add(u);
                                        }
                                    }
                                    catch (Exception caughtEx_9) when (caughtEx_9 is not OperationCanceledException && caughtEx_9 is not OutOfMemoryException && caughtEx_9 is not StackOverflowException) { 
                                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                    }
                                }
                                else
                                {
                                    candidates.Add(src);
                                }
                            }

                            // Normalize and pick best candidate
                            string[] preferredHosts = new[] { "m.media-amazon.com", "images-na.ssl-images-amazon.com" };
                            string[] ignorePatterns = new[] { "fls-na.amazon.com/1/batch", "/batch/", "fls-na.amazon.com" };
                            string? chosen = null;
                            foreach (var c in candidates)
                            {
                                if (string.IsNullOrWhiteSpace(c)) continue;
                                var norm = c.Trim();
                                if (norm.StartsWith("//")) norm = "https:" + norm;
                                else if (norm.StartsWith("/")) norm = "https://www.amazon.com" + norm;
                                if (ignorePatterns.Any(p => norm.Contains(p))) continue;
                                if (preferredHosts.Any(h => norm.Contains(h))) { chosen = norm; break; }
                                if (chosen == null) chosen = norm;
                            }
                            image = chosen;
                        }
                    }
                    catch (Exception caughtEx_10) when (caughtEx_10 is not OperationCanceledException && caughtEx_10 is not OutOfMemoryException && caughtEx_10 is not StackOverflowException) { 
                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                    }

                    // Parse details table / audible product details
                    try
                    {
                        var detailsNode = doc.DocumentNode.SelectSingleNode("//div[@id='audibleProductDetails']")
                                          ?? doc.DocumentNode.SelectSingleNode("//table[contains(@class,'a-keyvalue')]")
                                          ?? doc.DocumentNode.SelectSingleNode("//div[@id='productDetailsTable']");

                        if (detailsNode != null)
                        {
                            var rows = detailsNode.SelectNodes(".//tr") ?? detailsNode.SelectNodes(".//tbody/tr");
                            if (rows != null)
                            {
                                foreach (var tr in rows)
                                {
                                    var id = tr.GetAttributeValue("id", "").ToLowerInvariant();
                                    try
                                    {
                                        var thText = tr.SelectSingleNode(".//th")?.InnerText?.Trim() ?? string.Empty;
                                        var tdText = tr.SelectSingleNode(".//td")?.InnerText?.Trim() ?? string.Empty;

                                        if (id.Contains("detailsbookseries") || thText.IndexOf("Book", StringComparison.OrdinalIgnoreCase) >= 0 && thText.IndexOf("of", StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            // th may contain 'Book 1 of 7', td contains series link
                                            if (!string.IsNullOrWhiteSpace(tdText)) series = tdText;
                                            var m = System.Text.RegularExpressions.Regex.Match(thText, "(\\d+)\\s*(?:of\\s*\\d+)?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                            if (m.Success) seriesNumber = m.Groups[1].Value;
                                        }
                                        else if (id.Contains("detailslisteninglength") || thText.IndexOf("Listening Length", StringComparison.OrdinalIgnoreCase) >= 0 || thText.IndexOf("Length", StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            if (!string.IsNullOrWhiteSpace(tdText))
                                            {
                                                var hm = System.Text.RegularExpressions.Regex.Match(tdText, "(?:(\\d+)\\s*hours?)?\\s*(?:and\\s*)?(?:(\\d+)\\s*minutes?)?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                                if (hm.Success)
                                                {
                                                    int hrs = 0, mins = 0;
                                                    if (!string.IsNullOrWhiteSpace(hm.Groups[1].Value)) int.TryParse(hm.Groups[1].Value, out hrs);
                                                    if (!string.IsNullOrWhiteSpace(hm.Groups[2].Value)) int.TryParse(hm.Groups[2].Value, out mins);
                                                    runtimeMinutes = hrs * 60 + mins;
                                                }
                                            }
                                        }
                                        else if (id.Contains("detailsauthor") || thText.IndexOf("Author", StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            if (!string.IsNullOrWhiteSpace(tdText)) author = tr.SelectSingleNode(".//td//a")?.InnerText?.Trim() ?? tdText;
                                        }
                                        else if (id.Contains("detailsnarrator") || thText.IndexOf("Narrator", StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            if (!string.IsNullOrWhiteSpace(tdText)) narrator = tr.SelectSingleNode(".//td//a")?.InnerText?.Trim() ?? tdText;
                                        }
                                        else if (id.Contains("detailsreleasedate") || thText.IndexOf("Release Date", StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            if (!string.IsNullOrWhiteSpace(tdText))
                                            {
                                                var m = System.Text.RegularExpressions.Regex.Match(tdText, "(\\d{4})");
                                                if (m.Success) publishYear = m.Groups[1].Value;
                                            }
                                        }
                                        else if (id.Contains("detailspublisher") || thText.IndexOf("Publisher", StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            if (!string.IsNullOrWhiteSpace(tdText)) publisher = tr.SelectSingleNode(".//td//a")?.InnerText?.Trim() ?? tdText;
                                        }
                                        else if (id.Contains("detailsversion") || thText.IndexOf("Version", StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            if (!string.IsNullOrWhiteSpace(tdText)) version = tdText;
                                        }
                                        else if (id.Contains("detailslanguage") || thText.IndexOf("Language", StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            if (!string.IsNullOrWhiteSpace(tdText)) language = tdText;
                                        }
                                    }
                                    catch (Exception caughtEx_11) when (caughtEx_11 is not OperationCanceledException && caughtEx_11 is not OutOfMemoryException && caughtEx_11 is not StackOverflowException) { 
                                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception parseEx) when (parseEx is not OperationCanceledException && parseEx is not OutOfMemoryException && parseEx is not StackOverflowException) {
                        _logger.LogDebug(parseEx, "Failed to parse product details for {Asin}", asin);
                    }
                }

                if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(author) && string.IsNullOrWhiteSpace(image) && string.IsNullOrWhiteSpace(series) && string.IsNullOrWhiteSpace(narrator))
                    return null;

                return new AmazonSearchResult
                {
                    Asin = asin,
                    Title = title,
                    Author = author,
                    ImageUrl = image,
                    IsAudiobook = true,
                    Series = series,
                    SeriesNumber = seriesNumber,
                    RuntimeMinutes = runtimeMinutes,
                    Narrator = narrator,
                    PublishYear = publishYear,
                    Publisher = publisher,
                    Version = version,
                    Language = language
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogDebug(ex, "Error scraping product page for {Asin}", asin);
                return null;
            }
        }
    }
}
