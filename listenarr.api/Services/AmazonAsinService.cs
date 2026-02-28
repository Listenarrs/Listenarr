using System.Net;
using System.Text.RegularExpressions;

namespace Listenarr.Api.Services
{
    public class AmazonAsinService : IAmazonAsinService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<AmazonAsinService> _logger;
        private readonly IHttpClientFactory? _httpClientFactory;
        private readonly IConfigurationService _configurationService;
        private static readonly Regex AsinRegex = new("/dp/([A-Z0-9]{10})", RegexOptions.Compiled);
        private static readonly Regex DataAsinRegex = new("data-asin=\"([A-Z0-9]{10})\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ProductAsinRegex = new("\\\"asin\\\"\\s*:\\s*\\\"([A-Z0-9]{10})\\\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex DetailBulletsRegex = new("<li[^>]*>\\s*<span[^>]*>\\s*ASIN\\s*</span>\\s*<span[^>]*>\\s*([A-Z0-9]{10})\\s*</span>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public AmazonAsinService(HttpClient httpClient, ILogger<AmazonAsinService> logger, IConfigurationService configurationService, IHttpClientFactory? httpClientFactory = null)
        {
            _httpClient = httpClient;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _configurationService = configurationService;
        }

        public async Task<(bool Success, string? Asin, string? Error)> GetAsinFromIsbnAsync(string isbn, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(isbn)) return (false, null, "ISBN required");
            var cleaned = isbn.Replace("-", "").Trim();
            if (cleaned.Length is not (10 or 13)) return (false, null, "Invalid ISBN length");

            try
            {
                // Ordered set of search URL attempts.
                // IMPORTANT: If the input is an ISBN (10 or 13) do NOT query the Audible index
                // because Audible listings typically do not contain ISBN metadata.
                List<string> searchVariants;
                if (cleaned.Length == 13)
                {
                    // Prefer the books index exact-ISBN filter for ISBN-13 inputs
                    searchVariants = new List<string>
                    {
                        $"https://www.amazon.com/s?i=stripbooks&rh=p_66%3A{WebUtility.UrlEncode(cleaned)}",
                        // Fallbacks: general stripbooks search, then generic search — no audible variants
                        $"https://www.amazon.com/s?k={WebUtility.UrlEncode(cleaned)}&i=stripbooks",
                        $"https://www.amazon.com/s?k={WebUtility.UrlEncode(cleaned)}"
                    };
                }
                else
                {
                    // ISBN-10: avoid the Audible index; try stripbooks and generic searches
                    searchVariants = new List<string>
                    {
                        $"https://www.amazon.com/s?k={WebUtility.UrlEncode(cleaned)}&i=stripbooks",
                        $"https://www.amazon.com/s?k={WebUtility.UrlEncode(cleaned)}"
                    };
                }

                foreach (var url in searchVariants)
                {
                    var html = await GetHtmlAsync(url, ct);
                    if (html == null) continue;
                    if (IsBotBlocked(html))
                    {
                        _logger.LogWarning("Amazon bot-detection encountered for ISBN {Isbn} at {Url}", isbn, url);
                        // Continue trying other variants; maybe a different path passes.
                        continue;
                    }

                    var asin = ExtractFirstAsin(html);
                    if (!string.IsNullOrEmpty(asin))
                    {
                        var verify = await TryVerifyAsinAsync(asin, cleaned, ct);
                        // Require that the product page actually contains the ISBN before accepting the ASIN.
                        if (verify.Success && verify.MatchesIsbn)
                        {
                            return (true, asin, null);
                        }

                        if (verify.Success && !verify.MatchesIsbn)
                        {
                            _logger.LogDebug("ASIN {Asin} was found but product page did not contain ISBN {Isbn}; continuing search", asin, isbn);
                        }
                    }
                }

                return (false, null, "ASIN not found for ISBN");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Failed to resolve ASIN from ISBN {Isbn}", isbn);
                return (false, null, "Lookup failed");
            }
        }

        internal async Task<string?> GetHtmlAsync(string url, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // Amazon expects realistic headers
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
            req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
            req.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
            var resp = await _httpClient.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var html = await resp.Content.ReadAsStringAsync(ct);

            // If the response looks like a geo-redirect or we are not on .com, try forcing amazon.com and retry
            var lower = html?.ToLowerInvariant() ?? string.Empty;
            if (IsNonUsHost(url) || lower.Contains("we have redirected") || lower.Contains("have redirected you") || lower.Contains("redirected to"))
            {
                try
                {
                    var usUrl = ForceToUSDomain(url);
                    if (!string.Equals(usUrl, url, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogDebug("Retrying Amazon URL using US domain: {UsUrl}", usUrl);
                        using var retryReq = new HttpRequestMessage(HttpMethod.Get, usUrl);
                        foreach (var h in req.Headers)
                            retryReq.Headers.TryAddWithoutValidation(h.Key, h.Value);
                            HttpClient? usClient = null;
                            try
                            {
                                // Always prefer a named "us" client if available, otherwise use the default client.
                                usClient = _httpClientFactory != null ? _httpClientFactory.CreateClient("us") : _httpClient;
                                var retryResp = await usClient.SendAsync(retryReq, ct);
                                if (retryResp.IsSuccessStatusCode)
                                    return await retryResp.Content.ReadAsStringAsync(ct);
                            }
                            finally
                            {
                                // Dispose only if we created a dedicated client here (unlikely when using factory)
                                if (usClient != null && usClient != _httpClient && (_httpClientFactory == null || usClient != _httpClientFactory.CreateClient("us")))
                                {
                                    try { usClient.Dispose(); } catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException) { 
                                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                    }
                                }
                            }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    _logger.LogDebug(ex, "Failed retrying Amazon URL as US domain");
                }
            }

            return html;
        }

        internal static bool IsNonUsHost(string url)
        {
            try
            {
                var u = new Uri(url);
                var host = u.Host ?? string.Empty;
                if (host.Contains("amazon", StringComparison.OrdinalIgnoreCase) && !host.EndsWith(".com", StringComparison.OrdinalIgnoreCase))
                    return true;
                return false;
            }
            catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException) {
                return false;
            }
        }

        internal static string ForceToUSDomain(string url)
        {
            try
            {
                var u = new Uri(url);
                var host = u.Host;
                if (host.Contains("amazon", StringComparison.OrdinalIgnoreCase))
                {
                    var builder = new UriBuilder(u)
                    {
                        Host = "www.amazon.com"
                    };
                    return builder.Uri.ToString();
                }
                return url;
            }
            catch (Exception caughtEx_3) when (caughtEx_3 is not OperationCanceledException && caughtEx_3 is not OutOfMemoryException && caughtEx_3 is not StackOverflowException) {
                return url;
            }
        }

        private string? ExtractFirstAsin(string html)
        {
            // Priority: explicit product links, then data attributes, then JSON, then detail bullets
            var m = AsinRegex.Match(html);
            if (m.Success) return m.Groups[1].Value.ToUpperInvariant();

            m = DataAsinRegex.Match(html);
            if (m.Success && !string.IsNullOrWhiteSpace(m.Groups[1].Value))
                return m.Groups[1].Value.ToUpperInvariant();

            m = ProductAsinRegex.Match(html);
            if (m.Success) return m.Groups[1].Value.ToUpperInvariant();

            m = DetailBulletsRegex.Match(html);
            if (m.Success) return m.Groups[1].Value.ToUpperInvariant();

            return null;
        }

        private bool IsBotBlocked(string html)
        {
            // Simple heuristics for Amazon bot / captcha pages
            if (html.Contains("Robot Check", StringComparison.OrdinalIgnoreCase)) return true;
            if (html.Contains("captcha", StringComparison.OrdinalIgnoreCase) && html.Contains("amazon", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private async Task<(bool Success, bool MatchesIsbn)> TryVerifyAsinAsync(string asin, string isbn, CancellationToken ct)
        {
            try
            {
                var productUrl = $"https://www.amazon.com/dp/{asin}";
                var productHtml = await GetHtmlAsync(productUrl, ct);
                if (productHtml == null) return (false, false);

                // Check if ISBN appears on page (some Audible listings include print ISBN)
                if (productHtml.Contains(isbn, StringComparison.OrdinalIgnoreCase))
                    return (true, true);

                // Robust check: strip non-digits from page and look for the ISBN digits sequence
                try
                {
                    var digitsOnlyPage = Regex.Replace(productHtml ?? string.Empty, "\\D", "");
                    var cleanedIsbn = Regex.Replace(isbn ?? string.Empty, "\\D", "");
                    if (!string.IsNullOrEmpty(cleanedIsbn) && digitsOnlyPage.Contains(cleanedIsbn))
                        return (true, true);

                    // If ISBN is 13 and starts with 978, try converting to ISBN-10 and check that too
                    if (cleanedIsbn.Length == 13 && cleanedIsbn.StartsWith("978"))
                    {
                        var isbn10 = ConvertIsbn13ToIsbn10(cleanedIsbn);
                        if (!string.IsNullOrEmpty(isbn10) && digitsOnlyPage.Contains(isbn10))
                            return (true, true);
                    }
                }
                catch (Exception caughtEx_4) when (caughtEx_4 is not OperationCanceledException && caughtEx_4 is not OutOfMemoryException && caughtEx_4 is not StackOverflowException) {
                    // ignore parsing errors and fall through to partial success
                                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                }

                // At least we loaded a product page, consider it a partial success (but not matching ISBN)
                return (true, false);
            }
            catch (Exception caughtEx_5) when (caughtEx_5 is not OperationCanceledException && caughtEx_5 is not OutOfMemoryException && caughtEx_5 is not StackOverflowException) {
                return (false, false);
            }
        }

        // Convert ISBN-13 (starting with 978) to ISBN-10. Returns digits-only ISBN-10 string or null on failure.
        private static string? ConvertIsbn13ToIsbn10(string isbn13)
        {
            if (string.IsNullOrWhiteSpace(isbn13)) return null;
            var digits = Regex.Replace(isbn13, "\\D", "");
            if (digits.Length != 13) return null;
            if (!digits.StartsWith("978")) return null;

            var core = digits.Substring(3, 9); // 9 digits
            int sum = 0;
            for (int i = 0; i < core.Length; i++)
            {
                if (!char.IsDigit(core[i])) return null;
                sum += (10 - i) * (core[i] - '0');
            }
            int mod = 11 - (sum % 11);
            string checkDigit;
            if (mod == 11) checkDigit = "0";
            else if (mod == 10) checkDigit = "X";
            else checkDigit = mod.ToString();

            return core + checkDigit;
        }

        /// <summary>
        /// Public wrapper used by other services to verify whether a product page for an ASIN contains the given ISBN.
        /// </summary>
        public async Task<(bool Success, bool MatchesIsbn, string? Error)> VerifyAsinContainsIsbnAsync(string asin, string isbn, CancellationToken ct = default)
        {
            try
            {
                var verified = await TryVerifyAsinAsync(asin, isbn, ct);
                return (verified.Success, verified.MatchesIsbn, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogDebug(ex, "VerifyAsinContainsIsbnAsync failed for {Asin}", asin);
                return (false, false, ex.Message);
            }
        }
    }
}

