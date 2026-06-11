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

using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Security;
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Search.Providers;

/// <summary>
/// Search provider for Torznab and Newznab compatible indexers.
/// Supports both torrent and usenet indexers using the standard Torznab/Newznab XML API.
/// </summary>
public class TorznabNewznabSearchProvider : IIndexerSearchProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<TorznabNewznabSearchProvider> _logger;

    public string IndexerType => "Torznab"; // Handles both Torznab and Newznab

    public TorznabNewznabSearchProvider(
        HttpClient httpClient,
        ILogger<TorznabNewznabSearchProvider> logger)
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
            // Build Torznab/Newznab API URL (redact api keys before logging)
            var url = BuildTorznabUrl(indexer, query, category);
            var redactedUrl = LogRedaction.RedactText(url, LogRedaction.GetSensitiveValuesFromEnvironment().Concat(new[] { indexer.ApiKey ?? string.Empty }));
            _logger.LogDebug("Indexer API URL: {Url}", redactedUrl);

            // Make HTTP request with User-Agent header
            using var requestMessage = new HttpRequestMessage(HttpMethod.Get, url);
            var version = typeof(TorznabNewznabSearchProvider).Assembly.GetName().Version?.ToString() ?? "0.0.0";
            var userAgent = $"Listenarr/{version} (+https://github.com/Listenarrs/listenarr)";
            requestMessage.Headers.UserAgent.ParseAdd(userAgent);

            var response = await _httpClient.SendAsync(requestMessage);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Indexer {Name} returned status {Status}", indexer.Name, response.StatusCode);
                return new List<IndexerSearchResult>();
            }

            var xmlContent = await response.Content.ReadAsStringAsync();

            // Parse Torznab/Newznab XML response
            var results = await ParseTorznabResponseAsync(xmlContent, indexer);

            _logger.LogInformation("Indexer {Name} returned {Count} results", indexer.Name, results.Count);
            return results;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogError(ex, "Error searching Torznab/Newznab indexer {Name}", indexer.Name);
            return new List<IndexerSearchResult>();
        }
    }

    private string BuildTorznabUrl(Indexer indexer, string query, string? category)
    {
        var url = indexer.Url.TrimEnd('/');

        // Don't append /api if URL already ends with it (e.g., Prowlarr proxy URLs)
        var apiPath = url.EndsWith("/api", StringComparison.OrdinalIgnoreCase)
            ? ""
            : indexer.Implementation.ToLower() switch
            {
                "torznab" => "/api",
                "newznab" => "/api",
                _ => "/api"
            };

        var queryParams = new List<string>
        {
            $"t=search",
            $"q={Uri.EscapeDataString(query)}"
        };

        // Add API key if provided
        if (!string.IsNullOrEmpty(indexer.ApiKey))
        {
            queryParams.Add($"apikey={Uri.EscapeDataString(indexer.ApiKey)}");
        }

        // Add categories if specified
        if (!string.IsNullOrEmpty(category))
        {
            queryParams.Add($"cat={Uri.EscapeDataString(category)}");
        }
        else if (!string.IsNullOrEmpty(indexer.Categories))
        {
            queryParams.Add($"cat={Uri.EscapeDataString(indexer.Categories)}");
        }

        // Add limit
        queryParams.Add("limit=100");

        // Request extended info for Newznab/Torznab indexers to include grabs/snatches and other attributes when available
        if (!string.IsNullOrEmpty(indexer.Implementation) && (indexer.Implementation.Equals("newznab", StringComparison.OrdinalIgnoreCase) || indexer.Implementation.Equals("torznab", StringComparison.OrdinalIgnoreCase)))
        {
            queryParams.Add("extended=1");
        }

        return $"{url}{apiPath}?{string.Join("&", queryParams)}";
    }

    private async Task<List<IndexerSearchResult>> ParseTorznabResponseAsync(string xmlContent, Indexer indexer)
    {
        var results = new List<IndexerSearchResult>();

        try
        {
            // Log first 500 chars of XML for debugging
            var preview = xmlContent.Length > 500 ? xmlContent.Substring(0, 500) + "..." : xmlContent;
            _logger.LogDebug("Parsing XML from {IndexerName}: {Preview}", indexer.Name, preview);

            // Parse XML with settings that are more lenient
            var settings = new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Ignore,
                XmlResolver = null,
                IgnoreWhitespace = true,
                IgnoreComments = true
            };

            System.Xml.Linq.XDocument doc;
            using (var reader = System.Xml.XmlReader.Create(new System.IO.StringReader(xmlContent), settings))
            {
                doc = System.Xml.Linq.XDocument.Load(reader);
            }

            var channel = doc.Root?.Element("channel");
            if (channel == null)
            {
                _logger.LogWarning("Invalid Torznab response: no channel element");
                return results;
            }

            var items = channel.Elements("item");
            var isUsenet = indexer.Type.Equals("Usenet", StringComparison.OrdinalIgnoreCase);

            foreach (var item in items)
            {
                try
                {
                    var result = new IndexerSearchResult
                    {
                        Id = item.Element("guid")?.Value ?? Guid.NewGuid().ToString(),
                        Title = item.Element("title")?.Value ?? "Unknown",
                        Source = indexer.Name,
                        Category = item.Element("category")?.Value ?? "Audiobook"
                    };
                    result.IndexerId = indexer.Id;
                    result.IndexerImplementation = indexer.Implementation;

                    // Track peers value for potential leechers calculation
                    int? peersValue = null;

                    // Parse published date
                    var pubDateStr = item.Element("pubDate")?.Value;
                    result.PublishedDate = DateTime.TryParse(pubDateStr, out var pubDate)
                        ? pubDate.ToString("o")
                        : string.Empty;

                    // Parse Torznab/Newznab attributes (support both torznab and newznab namespaces)
                    var torznabNs = System.Xml.Linq.XNamespace.Get("http://torznab.com/schemas/2015/feed");
                    var newznabNs = System.Xml.Linq.XNamespace.Get("http://www.newznab.com/DTD/2010/feeds/attributes/");
                    var attributes = item.Elements(torznabNs + "attr").Concat(item.Elements(newznabNs + "attr")).ToList();

                    foreach (var attr in attributes)
                    {
                        var name = attr.Attribute("name")?.Value;
                        var value = attr.Attribute("value")?.Value;

                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(value))
                            continue;

                        // Debug logging to see what Prowlarr sends
                        _logger.LogDebug("Torznab attr from {Indexer}: {Name}={Value} for title {Title}", indexer.Name, name, value, result.Title);

                        switch (name.ToLower())
                        {
                            case "size":
                                var parsedSize = ParseSizeString(value);
                                if (parsedSize > 0)
                                {
                                    result.Size = parsedSize;
                                    _logger.LogDebug("Parsed size for {Title}: {Size} bytes from indexer {Indexer}", result.Title, parsedSize, indexer.Name);
                                }
                                else
                                {
                                    _logger.LogWarning("Failed to parse size value '{Value}' for result '{Title}' from indexer {Indexer}", value, result.Title, indexer.Name);
                                }
                                break;
                            case "seeders":
                                if (int.TryParse(value, out var seeders))
                                    result.Seeders = seeders;
                                break;
                            case "peers":
                                // Peers = seeders + leechers (total peers)
                                // Store for later calculation if no explicit leechers
                                if (int.TryParse(value, out var peers))
                                    peersValue = peers;
                                break;
                            case "leechers":
                                // Explicit leechers attribute takes priority
                                if (int.TryParse(value, out var leechers))
                                    result.Leechers = leechers;
                                break;
                            case "magneturl":
                                result.MagnetLink = value;
                                break;
                            case "filetype":
                            case "format":
                                // Prefer explicit filetype/format attributes
                                var normalizedFmt = value.ToLowerInvariant();
                                if (normalizedFmt.Contains("m4b")) result.Format = "M4B";
                                else if (normalizedFmt.Contains("flac")) result.Format = "FLAC";
                                else if (normalizedFmt.Contains("opus")) result.Format = "OPUS";
                                else if (normalizedFmt.Contains("aac")) result.Format = "AAC";
                                else if (normalizedFmt.Contains("mp3")) result.Format = "MP3";

                                // Also set Quality from format where possible
                                if (string.IsNullOrEmpty(result.Quality))
                                {
                                    if (normalizedFmt.Contains("320")) result.Quality = "MP3 320kbps";
                                    else if (normalizedFmt.Contains("256")) result.Quality = "MP3 256kbps";
                                    else if (normalizedFmt.Contains("192")) result.Quality = "MP3 192kbps";
                                    else if (normalizedFmt.Contains("128")) result.Quality = "MP3 128kbps";
                                    else if (normalizedFmt.Contains("m4b")) result.Quality = "M4B";
                                }
                                break;
                            case "lang_code":
                            case "language_code":
                            case "lang":
                                // Standardized language codes (e.g., ENG, FR)
                                try
                                {
                                    var parsedLang = ParseLanguageFromText(value);
                                    if (!string.IsNullOrEmpty(parsedLang)) result.Language = parsedLang;
                                }
                                catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException)
                                {
                                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                }
                                break;
                            case "language":
                                // Some indexers use numeric language IDs (e.g., 1 -> ENG)
                                if (int.TryParse(value, out var langNum))
                                {
                                    if (langNum == 1) result.Language = "English";
                                    // Add other mappings if required in the future
                                }
                                else
                                {
                                    try
                                    {
                                        var pl = ParseLanguageFromText(value);
                                        if (!string.IsNullOrEmpty(pl)) result.Language = pl;
                                    }
                                    catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException)
                                    {
                                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                    }
                                }
                                break;
                            case "grabs":
                                if (int.TryParse(value, out var grabs))
                                    result.Grabs = grabs;
                                break;
                            case "files":
                                if (int.TryParse(value, out var files))
                                    result.Files = files;
                                break;
                            case "usenetdate":
                                // Some indexers expose a usenet-specific date attribute; prefer it if parseable
                                if (long.TryParse(value, out var unixSec))
                                {
                                    try
                                    {
                                        var dt = DateTimeOffset.FromUnixTimeSeconds(unixSec).UtcDateTime;
                                        result.PublishedDate = dt.ToString("o");
                                    }
                                    catch (Exception caughtEx_3) when (caughtEx_3 is not OperationCanceledException && caughtEx_3 is not OutOfMemoryException && caughtEx_3 is not StackOverflowException)
                                    {
                                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                    }
                                }
                                else if (DateTime.TryParse(value, out var udt))
                                {
                                    result.PublishedDate = udt.ToString("o");
                                }
                                break;
                        }
                    }

                    // Fallback: some indexers don't expose "grabs" as a standard torznab/newznab attr.
                    // Attempt a few common alternate attribute names and elements (snatches, comments, etc.)
                    if (result.Grabs == 0)
                    {
                        var altNames = new[] { "snatches", "snatched", "numgrabs", "num_grabs", "grab_count" };
                        foreach (var alt in altNames)
                        {
                            var altAttr = attributes.FirstOrDefault(a => string.Equals(a.Attribute("name")?.Value, alt, System.StringComparison.OrdinalIgnoreCase));
                            if (altAttr != null)
                            {
                                var av = altAttr.Attribute("value")?.Value ?? altAttr.Value;
                                if (!string.IsNullOrEmpty(av) && int.TryParse(av, out var g2))
                                {
                                    result.Grabs = g2;
                                    _logger.LogDebug("Set grabs from alternate attr '{Alt}' for {Title}: {Grabs}", alt, result.Title, g2);
                                    break;
                                }
                            }
                        }

                        // If still zero, and a comments element points to a details URL (althub-style), attempt to scrape comment count
                        if (result.Grabs == 0)
                        {
                            var commentsVal = item.Element("comments")?.Value;
                            if (!string.IsNullOrEmpty(commentsVal))
                            {
                                // If comments is a URL, try scraping the page for a numeric comments count (only for known indexers to avoid many extra requests)
                                if (Uri.TryCreate(commentsVal, UriKind.Absolute, out var commentsUri) && indexer.Url != null && indexer.Url.Contains("althub", StringComparison.OrdinalIgnoreCase))
                                {
                                    try
                                    {
                                        var commentsPageUrl = new Uri(commentsUri.GetLeftPart(UriPartial.Path));
                                        _logger.LogDebug("Fetching comments page to extract grabs for {Title}: {Url}", result.Title, commentsPageUrl);
                                        using var resp = await _httpClient.GetAsync(commentsPageUrl);
                                        if (resp.IsSuccessStatusCode)
                                        {
                                            var html = await resp.Content.ReadAsStringAsync();
                                            var htmlDoc = new HtmlDocument();
                                            htmlDoc.LoadHtml(html);

                                            // Look for common comment count patterns in page text
                                            var text = htmlDoc.DocumentNode.InnerText;
                                            var m = Regex.Match(text, "(\\d{1,6})\\s+comments?", RegexOptions.IgnoreCase);
                                            if (!m.Success)
                                            {
                                                m = Regex.Match(text, "Comments\\s*[:\\(]?\\s*(\\d{1,6})", RegexOptions.IgnoreCase);
                                            }

                                            if (m.Success && int.TryParse(m.Groups[1].Value, out var scrapedComments))
                                            {
                                                result.Grabs = scrapedComments;
                                                _logger.LogDebug("Scraped comments count for {Title}: {Grabs}", result.Title, scrapedComments);
                                            }
                                        }
                                    }
                                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                    {
                                        _logger.LogDebug(ex, "Failed to scrape comments page for {Title}", result.Title);
                                    }
                                }
                                else
                                {
                                    // Some feeds put a numeric comments value directly; parse that
                                    if (int.TryParse(commentsVal, out var commVal))
                                    {
                                        result.Grabs = commVal;
                                        _logger.LogDebug("Set grabs from <comments> element for {Title}: {Grabs}", result.Title, commVal);
                                    }
                                }
                            }
                        }
                    }

                    // Get enclosure/link for download URL
                    var enclosure = item.Element("enclosure");
                    if (enclosure != null)
                    {
                        var enclosureUrl = enclosure.Attribute("url")?.Value;
                        if (!string.IsNullOrEmpty(enclosureUrl))
                        {
                            if (isUsenet)
                            {
                                result.NzbUrl = enclosureUrl;
                            }
                            else
                            {
                                result.TorrentUrl = enclosureUrl;
                            }
                        }

                        // If the indexer provides an enclosure length, use it as a size fallback
                        var lengthStr = enclosure.Attribute("length")?.Value;
                        if (!string.IsNullOrEmpty(lengthStr) && result.Size == 0)
                        {
                            var parsedLen = ParseSizeString(lengthStr);
                            if (parsedLen > 0)
                            {
                                result.Size = parsedLen;
                                _logger.LogDebug("Set size from enclosure length for {Title}: {Size} bytes", result.Title, parsedLen);
                            }
                        }
                    }

                    // If no magnet link found in attributes, check link element
                    var linkElem = item.Element("link")?.Value;
                    if (!string.IsNullOrEmpty(linkElem))
                    {
                        if (linkElem.StartsWith("magnet:") && string.IsNullOrEmpty(result.MagnetLink) && !isUsenet)
                        {
                            result.MagnetLink = linkElem;
                        }
                        else
                        {
                            // Use the link element as the canonical indexer page when possible
                            if (Uri.IsWellFormedUriString(linkElem, UriKind.Absolute))
                            {
                                result.ResultUrl = linkElem;
                            }

                            // If torrentUrl is empty, prefer the link
                            if (string.IsNullOrEmpty(result.TorrentUrl) && !linkElem.StartsWith("magnet:") && !isUsenet)
                            {
                                result.TorrentUrl = linkElem;
                            }
                            else if (string.IsNullOrEmpty(result.NzbUrl) && isUsenet && !linkElem.StartsWith("magnet:"))
                            {
                                result.NzbUrl = linkElem;
                            }
                        }
                    }

                    // Parse description for additional metadata
                    var description = item.Element("description")?.Value;
                    if (!string.IsNullOrEmpty(description))
                    {
                        result.Description = description;

                        // Try to extract quality/format from description or title
                        var titleAndDesc = $"{result.Title} {description}".ToLower();

                        if (titleAndDesc.Contains("flac"))
                            result.Quality = "FLAC";
                        else if (titleAndDesc.Contains("320") || titleAndDesc.Contains("320kbps"))
                            result.Quality = "MP3 320kbps";
                        else if (titleAndDesc.Contains("256") || titleAndDesc.Contains("256kbps"))
                            result.Quality = "MP3 256kbps";
                        else if (titleAndDesc.Contains("192") || titleAndDesc.Contains("192kbps"))
                            result.Quality = "MP3 192kbps";
                        else if (titleAndDesc.Contains("128") || titleAndDesc.Contains("128kbps"))
                            result.Quality = "MP3 128kbps";
                        else if (titleAndDesc.Contains("64") || titleAndDesc.Contains("64kbps"))
                            result.Quality = "MP3 64kbps";
                        else if (titleAndDesc.Contains("m4b"))
                            result.Quality = "M4B";
                        else
                            result.Quality = "Unknown";

                        // Detect format
                        if (titleAndDesc.Contains("m4b"))
                            result.Format = "M4B";
                        else if (titleAndDesc.Contains("flac"))
                            result.Format = "FLAC";
                        else if (titleAndDesc.Contains("mp3"))
                            result.Format = "MP3";
                        else if (titleAndDesc.Contains("opus"))
                            result.Format = "OPUS";
                        else if (titleAndDesc.Contains("aac"))
                            result.Format = "AAC";

                        // Detect language codes present in title or description (e.g. [ENG / M4B])
                        try
                        {
                            var lang = ParseLanguageFromText(result.Title + " " + description);
                            if (!string.IsNullOrEmpty(lang)) result.Language = lang;
                        }
                        catch (Exception caughtEx_4) when (caughtEx_4 is not OperationCanceledException && caughtEx_4 is not OutOfMemoryException && caughtEx_4 is not StackOverflowException)
                        { /* Non-critical */
                            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                        }
                    }

                    // Extract author from title if possible (common format: "Author - Title")
                    var titleParts = result.Title.Split(new[] { " - ", " \u2013 " }, StringSplitOptions.RemoveEmptyEntries);
                    if (titleParts.Length >= 2)
                    {
                        result.Artist = titleParts[0].Trim();
                        result.Album = string.Join(" - ", titleParts.Skip(1)).Trim();
                    }
                    else
                    {
                        result.Artist = "Unknown Author";
                        result.Album = result.Title;
                    }

                    // Calculate leechers from peers if no explicit leechers was provided
                    if (!result.Leechers.HasValue && peersValue.HasValue && result.Seeders.HasValue)
                    {
                        result.Leechers = Math.Max(0, peersValue.Value - result.Seeders.Value);
                        _logger.LogDebug("Calculated leechers for {Title}: peers={Peers} - seeders={Seeders} = {Leechers}",
                            result.Title, peersValue.Value, result.Seeders.Value, result.Leechers.Value);
                    }

                    // Only add results that have a valid download link
                    if (!string.IsNullOrEmpty(result.MagnetLink) ||
                        !string.IsNullOrEmpty(result.TorrentUrl) ||
                        !string.IsNullOrEmpty(result.NzbUrl))
                    {
                        // Set download type based on what's available
                        if (!string.IsNullOrEmpty(result.NzbUrl))
                        {
                            result.DownloadType = "Usenet";
                        }
                        else if (!string.IsNullOrEmpty(result.MagnetLink) || !string.IsNullOrEmpty(result.TorrentUrl))
                        {
                            result.DownloadType = "Torrent";
                        }

                        results.Add(result);
                    }
                    else
                    {
                        _logger.LogWarning("Skipping result '{Title}' - no download link found", result.Title);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "Error parsing indexer result item");
                }
            }
        }
        catch (System.Xml.XmlException xmlEx)
        {
            _logger.LogError(xmlEx, "XML parsing error from {IndexerName} at Line {Line}, Position {Position}: {Message}",
                indexer.Name, xmlEx.LineNumber, xmlEx.LinePosition, xmlEx.Message);

            // Log the problematic XML content around the error
            if (!string.IsNullOrEmpty(xmlContent))
            {
                var lines = xmlContent.Split('\n');
                if (xmlEx.LineNumber > 0 && xmlEx.LineNumber <= lines.Length)
                {
                    var startLine = Math.Max(0, xmlEx.LineNumber - 3);
                    var endLine = Math.Min(lines.Length - 1, xmlEx.LineNumber + 2);
                    var context = string.Join("\n", lines[startLine..(endLine + 1)]);
                    _logger.LogError("XML context around error:\n{Context}", context);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogError(ex, "Error parsing Torznab XML response from {IndexerName}", indexer.Name);
        }

        return results;
    }

    private long ParseSizeString(string sizeStr)
    {
        if (string.IsNullOrWhiteSpace(sizeStr))
            return 0;

        // Try parsing as a plain number first (bytes)
        if (long.TryParse(sizeStr, out var bytes))
            return bytes;

        // Parse human-readable sizes like "1.5 GB", "3.7 GiB", "500 MB", etc.
        // Support both binary (GiB, MiB, TiB, KiB) and decimal (GB, MB, TB, KB) units
        var match = Regex.Match(sizeStr, @"([\d\.]+)\s*([KMGT]i?B)", RegexOptions.IgnoreCase);
        if (!match.Success)
            return 0;

        if (!double.TryParse(match.Groups[1].Value, out var size))
            return 0;

        var unit = match.Groups[2].Value.ToUpper();
        return unit switch
        {
            "TIB" => (long)(size * 1024 * 1024 * 1024 * 1024),
            "TB" => (long)(size * 1024 * 1024 * 1024 * 1024),
            "GIB" => (long)(size * 1024 * 1024 * 1024),
            "GB" => (long)(size * 1024 * 1024 * 1024),
            "MIB" => (long)(size * 1024 * 1024),
            "MB" => (long)(size * 1024 * 1024),
            "KIB" => (long)(size * 1024),
            "KB" => (long)(size * 1024),
            "B" => (long)size,
            _ => 0
        };
    }

    private string? ParseLanguageFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Normalize whitespace
        var normalized = Regex.Replace(text, "\\s+", " ", RegexOptions.Compiled | RegexOptions.IgnoreCase).Trim();

        var codes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "ENG", "English" }, { "EN", "English" },
            { "DUT", "Dutch" },    { "NL", "Dutch" },
            { "GER", "German" },   { "DE", "German" },
            { "FRE", "French" },   { "FR", "French" }
        };

        // Build a joined alternation like ENG|EN|DUT|NL|...
        var alternation = string.Join("|", codes.Keys.Select(Regex.Escape));

        // Bracketed or parenthesis forms: [ ENG / ... ] or (EN)
        var bracketedPattern = $@"[\[\(]\s*(?:{alternation})\b";

        // Standalone word boundary pattern: \b(ENG|EN|DUT|NL|...)\b
        var standalonePattern = $@"\b(?:{alternation})\b";

        // Try bracketed first (higher confidence)
        var m = Regex.Match(normalized, bracketedPattern, RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var captured = Regex.Match(m.Value, $@"(?:{alternation})", RegexOptions.IgnoreCase);
            if (captured.Success && codes.TryGetValue(captured.Value, out var lang))
                return lang;
        }

        // Try standalone word boundary
        m = Regex.Match(normalized, standalonePattern, RegexOptions.IgnoreCase);
        if (m.Success && codes.TryGetValue(m.Value, out var lang2))
            return lang2;

        return null;
    }
}

