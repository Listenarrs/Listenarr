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

using HtmlAgilityPack;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Search.Providers.ZLibrary;

/// <summary>
/// Search provider for Z-Library.
///
/// Config:
///   indexer.Url    = current working Z-Library domain (required).
///                    Examples: https://z-lib.id, https://z-library.sk, or your personal domain.
///                    Z-Library rotates public domains — update this field when one goes down.
///   indexer.ApiKey = session cookies for authenticated access (required for downloads):
///                    "remix_userid=XXXXX; remix_userkey=YYYYY"
///                    To get these: log in to your Z-Library account in a browser and copy the
///                    remix_userid and remix_userkey cookie values from DevTools.
///
/// Download model: DDL. TorrentUrl is set to {baseUrl}/dl/{id}/{hash} (direct download,
/// requires auth cookie). ResultUrl is the {baseUrl}/book/{id}/{hash} detail page.
///
/// ponytail: plain HttpClient with browser UA. Cloudflare may block — switch to a working
///   domain or use a personal Z-Library domain (logged-in users get one at singlelogin.re).
/// </summary>
public class ZLibrarySearchProvider : IIndexerSearchProvider
{
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    private const int MaxResults = 20;

    private readonly HttpClient _httpClient;
    private readonly ILogger<ZLibrarySearchProvider> _logger;

    public string IndexerType => "ZLibrary";

    public ZLibrarySearchProvider(HttpClient httpClient, ILogger<ZLibrarySearchProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<List<IndexerSearchResult>> SearchAsync(
        Indexer indexer,
        string query,
        string? category = null,
        SearchRequest? request = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(indexer.Url))
            {
                _logger.LogWarning("Z-Library indexer {Name} has no URL configured", indexer.Name);
                return [];
            }

            var baseUrl = indexer.Url.TrimEnd('/');
            _logger.LogInformation("Searching Z-Library for: {Query} on {BaseUrl}", query, baseUrl);

            // /s/{query}?e=1 with audiobook-relevant extension filters
            var searchUrl = $"{baseUrl}/s/{Uri.EscapeDataString(query)}?e=1&extensions%5B%5D=mp3&extensions%5B%5D=m4b&extensions%5B%5D=m4a&extensions%5B%5D=aac";

            using var requestMessage = new HttpRequestMessage(HttpMethod.Get, searchUrl);
            requestMessage.Headers.UserAgent.ParseAdd(BrowserUserAgent);
            if (!string.IsNullOrWhiteSpace(indexer.ApiKey))
                requestMessage.Headers.Add("Cookie", indexer.ApiKey);

            var response = await _httpClient.SendAsync(requestMessage);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Z-Library returned status {Status} for indexer {Name}", response.StatusCode, indexer.Name);
                return [];
            }

            var html = await response.Content.ReadAsStringAsync();
            return ParseSearchResults(html, baseUrl, indexer);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogError(ex, "Error searching Z-Library indexer {Name}", indexer.Name);
            return [];
        }
    }

    /// <summary>
    /// Parses Z-Library search result HTML into IndexerSearchResults.
    /// Internal for unit testing.
    /// </summary>
    internal List<IndexerSearchResult> ParseSearchResults(string html, string baseUrl, Indexer indexer)
    {
        var results = new List<IndexerSearchResult>();
        try
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Z-Library book result links contain /book/ in the href
            var bookLinks = doc.DocumentNode.SelectNodes("//a[contains(@href, '/book/')]");
            if (bookLinks == null)
            {
                _logger.LogDebug("No Z-Library book links found in response");
                return results;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var bookLink in bookLinks.Take(MaxResults))
            {
                try
                {
                    var href = bookLink.GetAttributeValue("href", "");
                    if (string.IsNullOrEmpty(href) || !href.Contains("/book/"))
                        continue;

                    var detailUrl = href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href : $"{baseUrl}{href}";
                    if (!seen.Add(detailUrl))
                        continue;

                    var title = HtmlEntity.DeEntitize(bookLink.InnerText.Trim());
                    if (string.IsNullOrEmpty(title))
                        continue;

                    // Walk up to find the containing book card for sibling metadata
                    var card = FindAncestorCard(bookLink);
                    var author = ExtractAuthor(card);
                    var (format, size, language) = ExtractMetadata(card);

                    results.Add(new IndexerSearchResult
                    {
                        Id = Guid.NewGuid().ToString(),
                        Title = title,
                        Artist = author,
                        Album = title,
                        Category = "Audiobook",
                        Format = format,
                        Size = size,
                        Language = language,
                        TorrentUrl = BuildDownloadUrl(baseUrl, href),
                        ResultUrl = detailUrl,
                        DownloadType = "DDL",
                        Source = $"{indexer.Name} (Z-Library)",
                        PublishedDate = string.Empty,
                        IndexerId = indexer.Id,
                        IndexerImplementation = indexer.Implementation,
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogDebug(ex, "Error parsing Z-Library result item");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogError(ex, "Error parsing Z-Library HTML response");
        }

        _logger.LogInformation("Z-Library parsed {Count} results", results.Count);
        return results;
    }

    /// <summary>
    /// Converts a Z-Library book path to its download path.
    /// /book/{id}/{hash} → {baseUrl}/dl/{id}/{hash}
    /// Internal for unit testing.
    /// </summary>
    internal static string BuildDownloadUrl(string baseUrl, string bookHref)
    {
        if (string.IsNullOrEmpty(bookHref))
            return "";
        var bookIdx = bookHref.IndexOf("/book/", StringComparison.OrdinalIgnoreCase);
        if (bookIdx < 0) return "";
        var path = bookHref[(bookIdx + "/book/".Length)..].TrimEnd('/');
        return $"{baseUrl}/dl/{path}";
    }

    private static HtmlNode? FindAncestorCard(HtmlNode node)
    {
        var current = node.ParentNode;
        while (current != null && current.Name is not "body" and not "html")
        {
            var cls = current.GetAttributeValue("class", "");
            if (cls.Contains("resItemBox") || cls.Contains("bookCard") || cls.Contains("book-item") || current.Name == "article")
                return current;
            current = current.ParentNode;
        }
        // Fallback: immediate parent (may still yield useful sibling metadata)
        return node.ParentNode;
    }

    private static string ExtractAuthor(HtmlNode? card)
    {
        if (card == null) return "";
        var node = card.SelectSingleNode(".//*[contains(@class,'authors')]//a")
                ?? card.SelectSingleNode(".//*[contains(@class,'author')]")
                ?? card.SelectSingleNode(".//a[contains(@href,'/author/')]");
        return HtmlEntity.DeEntitize(node?.InnerText.Trim() ?? "");
    }

    private static (string Format, long Size, string Language) ExtractMetadata(HtmlNode? card)
    {
        if (card == null) return ("", 0, "");

        var format = "";
        long size = 0;
        var language = "";

        var propertyNodes = card.SelectNodes(".//*[contains(@class,'property_value')]")
                         ?? card.SelectNodes(".//*[contains(@class,'property')]");
        if (propertyNodes == null) return (format, size, language);

        foreach (var node in propertyNodes)
        {
            var text = HtmlEntity.DeEntitize(node.InnerText.Trim());
            if (string.IsNullOrEmpty(text)) continue;
            var lower = text.ToLowerInvariant();

            if (format.Length == 0 && lower.Trim() is "mp3" or "m4b" or "m4a" or "aac" or "flac" or "ogg" or "opus" or "wav")
            {
                format = lower.Trim().ToUpperInvariant();
                continue;
            }

            if (size == 0 && (lower.Contains("mb") || lower.Contains("gb") || lower.Contains("kb")))
            {
                var parsed = ParseSizeBytes(lower);
                if (parsed > 0) { size = parsed; continue; }
            }

            if (language.Length == 0 && IsKnownLanguage(lower))
            {
                language = char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant();
            }
        }

        return (format, size, language);
    }

    private static bool IsKnownLanguage(string lower) =>
        lower is "english" or "spanish" or "french" or "german" or "italian"
              or "portuguese" or "russian" or "japanese" or "chinese" or "korean"
              or "arabic" or "dutch" or "polish" or "swedish" or "norwegian" or "danish";

    private static long ParseSizeBytes(string raw)
    {
        try
        {
            var s = raw.Replace(" ", "").Replace(",", ".").ToUpperInvariant();
            if (s.Contains("GB")) { var n = s.Replace("GB", ""); return double.TryParse(n, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? (long)(v * 1_073_741_824.0) : 0; }
            if (s.Contains("MB")) { var n = s.Replace("MB", ""); return double.TryParse(n, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? (long)(v * 1_048_576.0) : 0; }
            if (s.Contains("KB")) { var n = s.Replace("KB", ""); return double.TryParse(n, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? (long)(v * 1_024.0) : 0; }
        }
        catch { /* ponytail: swallow size parse failures */ }
        return 0;
    }
}
