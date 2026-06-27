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

namespace Listenarr.Infrastructure.Search.Providers.AnnasArchive;

/// <summary>
/// Search provider for Anna's Archive (annas-archive.org).
/// Searches the largest shadow library index for audiobooks.
///
/// Config:
///   indexer.Url = mirror base URL (default: https://annas-archive.org).
///                 Update to a working mirror if the canonical domain is blocked.
///   No auth required for search.
///
/// Download model: DDL. TorrentUrl is set to the /fast-download/{md5} endpoint
/// (IPFS/partner mirror redirect). ResultUrl is the /md5/{md5} detail page.
///
/// ponytail: plain HttpClient with browser UA. If Cloudflare starts blocking,
///   route through FlareSolverr (no existing support in this codebase — see AudioBookBay ponytail note).
/// </summary>
public class AnnasArchiveSearchProvider : IIndexerSearchProvider
{
    private const string DefaultBaseUrl = "https://annas-archive.org";
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    private const int MaxResults = 20;

    private readonly HttpClient _httpClient;
    private readonly ILogger<AnnasArchiveSearchProvider> _logger;

    public string IndexerType => "AnnasArchive";

    public AnnasArchiveSearchProvider(HttpClient httpClient, ILogger<AnnasArchiveSearchProvider> logger)
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
            var baseUrl = (!string.IsNullOrWhiteSpace(indexer.Url) ? indexer.Url : DefaultBaseUrl).TrimEnd('/');
            _logger.LogInformation("Searching Anna's Archive for: {Query} on {BaseUrl}", query, baseUrl);

            var searchUrl = $"{baseUrl}/search?q={Uri.EscapeDataString(query)}&ext=mp3&ext=m4b&ext=m4a&ext=aac&content=audiobook";

            using var requestMessage = new HttpRequestMessage(HttpMethod.Get, searchUrl);
            requestMessage.Headers.UserAgent.ParseAdd(BrowserUserAgent);

            var response = await _httpClient.SendAsync(requestMessage);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Anna's Archive returned status {Status} for {Name} (possibly Cloudflare block)", response.StatusCode, indexer.Name);
                return [];
            }

            var html = await response.Content.ReadAsStringAsync();
            return ParseSearchResults(html, baseUrl, indexer);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogError(ex, "Error searching Anna's Archive indexer {Name}", indexer.Name);
            return [];
        }
    }

    /// <summary>
    /// Parses Anna's Archive search result HTML into IndexerSearchResults.
    /// Internal for unit testing.
    /// </summary>
    internal List<IndexerSearchResult> ParseSearchResults(string html, string baseUrl, Indexer indexer)
    {
        var results = new List<IndexerSearchResult>();
        try
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // Each result links to /md5/{hash}
            var resultLinks = doc.DocumentNode.SelectNodes("//a[starts-with(@href, '/md5/')]");
            if (resultLinks == null)
            {
                _logger.LogDebug("No Anna's Archive result links found in response");
                return results;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var link in resultLinks.Take(MaxResults))
            {
                try
                {
                    var href = link.GetAttributeValue("href", "");
                    // href = "/md5/abc123..." → strip the leading "/md5/"
                    var md5 = href.Length > 5 ? href[5..].Trim('/') : "";
                    if (string.IsNullOrEmpty(md5) || !seen.Add(md5))
                        continue;

                    // Title: first element with font-bold class, or h3, or first strong
                    var titleNode = link.SelectSingleNode(".//*[contains(@class,'font-bold')]")
                                 ?? link.SelectSingleNode(".//h3")
                                 ?? link.SelectSingleNode(".//strong");
                    var title = HtmlEntity.DeEntitize(titleNode?.InnerText.Trim() ?? "");
                    if (string.IsNullOrEmpty(title))
                        continue;

                    // Author: first element with italic or author class
                    var authorNode = link.SelectSingleNode(".//*[contains(@class,'italic')]")
                                  ?? link.SelectSingleNode(".//*[contains(@class,'author')]");
                    var author = HtmlEntity.DeEntitize(authorNode?.InnerText.Trim() ?? "");

                    // Metadata (format/size/language): scan the full inner text of the link.
                    // Anna's Archive metadata lines look like: "English [EN], MP3, 356.2MB, fiction"
                    var (format, size, language) = ParseMetadataFromInnerText(link.InnerText);

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
                        TorrentUrl = $"{baseUrl}/fast-download/{md5}",
                        ResultUrl = $"{baseUrl}/md5/{md5}",
                        DownloadType = "DDL",
                        Source = $"{indexer.Name} (Anna's Archive)",
                        PublishedDate = string.Empty,
                        IndexerId = indexer.Id,
                        IndexerImplementation = indexer.Implementation,
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogDebug(ex, "Error parsing Anna's Archive result item");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogError(ex, "Error parsing Anna's Archive HTML response");
        }

        _logger.LogInformation("Anna's Archive parsed {Count} results", results.Count);
        return results;
    }

    /// <summary>
    /// Scans the raw inner text of a result item for format, size, and language tokens.
    /// Splits on commas and newlines; classifies each token by content.
    /// Internal for unit testing.
    /// </summary>
    internal static (string Format, long Size, string Language) ParseMetadataFromInnerText(string innerText)
    {
        if (string.IsNullOrWhiteSpace(innerText))
            return ("", 0, "");

        var format = "";
        long size = 0;
        var language = "";

        var parts = innerText.Split([',', '\n', '\r', '\t', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            var lower = part.ToLowerInvariant();

            // Size: e.g., "356.2MB", "1.2 GB", "234 KB"
            if (size == 0 && (lower.Contains("mb") || lower.Contains("gb") || lower.Contains("kb")))
            {
                var parsed = ParseSizeBytes(part);
                if (parsed > 0) { size = parsed; continue; }
            }

            // Format: known audio containers
            if (format.Length == 0 && lower.Trim() is "mp3" or "m4b" or "m4a" or "aac" or "flac" or "ogg" or "opus" or "wav")
            {
                format = lower.Trim().ToUpperInvariant();
                continue;
            }

            // Language: "English [EN]" — extract the word before the bracket
            if (language.Length == 0 && lower.Contains('[') && lower.Contains(']'))
            {
                var bracketIdx = part.IndexOf('[');
                var lang = bracketIdx > 0 ? part[..bracketIdx].Trim() : part.Trim();
                if (lang.Length > 0) { language = lang; continue; }
            }
        }

        return (format, size, language);
    }

    private static long ParseSizeBytes(string raw)
    {
        try
        {
            var s = raw.Replace(" ", "").ToUpperInvariant();
            if (s.EndsWith("GB")) { var n = s[..^2]; return double.TryParse(n, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? (long)(v * 1_073_741_824.0) : 0; }
            if (s.EndsWith("MB")) { var n = s[..^2]; return double.TryParse(n, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? (long)(v * 1_048_576.0) : 0; }
            if (s.EndsWith("KB")) { var n = s[..^2]; return double.TryParse(n, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? (long)(v * 1_024.0) : 0; }
        }
        catch { /* ponytail: swallow size parse failures */ }
        return 0;
    }
}
