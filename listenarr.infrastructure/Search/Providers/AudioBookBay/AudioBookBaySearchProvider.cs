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

using System.Globalization;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Search.Providers.AudioBookBay;

/// <summary>
/// Search provider for AudioBookBay (audiobookbay.lu and mirrors).
/// Scrapes the public search pages, then reads each result's detail page to
/// build a magnet link from the listed info hash + trackers. Ported from the
/// audiobookbay-automated project (Flask) to Listenarr's indexer model.
/// ponytail: plain HttpClient + browser UA is enough today (no Cloudflare solver);
/// if ABB starts returning JS/CF challenges, route this client through FlareSolverr.
/// </summary>
public class AudioBookBaySearchProvider : IIndexerSearchProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AudioBookBaySearchProvider> _logger;

    // Bound the per-result detail fetches (each result costs one extra request).
    private const int MaxResults = 15;
    private const string DefaultHost = "audiobookbay.lu";
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/115.0.0.0 Safari/537.36";

    private static readonly string[] FallbackTrackers =
    {
        "udp://tracker.openbittorrent.com:80",
        "udp://opentor.org:2710",
        "udp://tracker.ccc.de:80",
        "udp://tracker.blackunicorn.xyz:6969",
        "udp://tracker.coppersurfer.tk:6969",
        "udp://tracker.leechers-paradise.org:6969",
    };

    public string IndexerType => "AudioBookBay";

    public AudioBookBaySearchProvider(
        HttpClient httpClient,
        ILogger<AudioBookBaySearchProvider> logger)
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
        var results = new List<IndexerSearchResult>();

        try
        {
            var host = ResolveHost(indexer.Url);
            var slug = Uri.EscapeDataString(query.Trim().ToLowerInvariant()).Replace("%20", "+");
            var searchUrl = $"https://{host}/?s={slug}";

            _logger.LogInformation("Searching AudioBookBay ({Host}) for: {Query}", host, query);

            var searchHtml = await GetHtmlAsync(searchUrl);
            if (searchHtml == null)
            {
                return results;
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(searchHtml);

            var posts = doc.DocumentNode.SelectNodes(
                "//div[contains(concat(' ', normalize-space(@class), ' '), ' post ')]");

            if (posts == null || posts.Count == 0)
            {
                _logger.LogInformation("AudioBookBay returned no posts for: {Query}", query);
                return results;
            }

            foreach (var post in posts)
            {
                if (results.Count >= MaxResults)
                {
                    break;
                }

                var parsed = ParsePost(post, host, indexer);
                if (parsed == null)
                {
                    continue;
                }

                // Resolve the magnet link from the detail page (required to grab).
                var magnet = await ExtractMagnetAsync(parsed.DetailUrl);
                if (string.IsNullOrEmpty(magnet))
                {
                    _logger.LogDebug("Skipping '{Title}' - no magnet/info hash on detail page", parsed.Result.Title);
                    continue;
                }

                parsed.Result.MagnetLink = magnet;
                parsed.Result.DownloadType = "Torrent";
                results.Add(parsed.Result);
            }

            _logger.LogInformation("AudioBookBay returned {Count} grabbable results for: {Query}", results.Count, query);
            return results;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogError(ex, "Error searching AudioBookBay indexer {Name}", indexer.Name);
            return results;
        }
    }

    private sealed class ParsedPost
    {
        public required IndexerSearchResult Result { get; init; }
        public required string DetailUrl { get; init; }
    }

    private ParsedPost? ParsePost(HtmlNode post, string host, Indexer indexer)
    {
        var titleAnchor = post.SelectSingleNode(".//*[contains(@class,'postTitle')]//h2/a")
                          ?? post.SelectSingleNode(".//*[contains(@class,'postTitle')]//a");
        if (titleAnchor == null)
        {
            return null;
        }

        var title = HtmlEntity.DeEntitize(titleAnchor.InnerText).Trim();
        var href = titleAnchor.GetAttributeValue("href", "");
        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(href))
        {
            return null;
        }

        var detailUrl = href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? href
            : $"https://{host}{href}";

        // postContent details paragraph carries Format / Bitrate / File Size / Posted.
        var detailsNode = post.SelectSingleNode(".//*[contains(@class,'postContent')]//p[contains(@style,'text-align')]");
        var detailsHtml = detailsNode?.InnerHtml ?? post.InnerHtml;

        var format = MatchSpan(detailsHtml, "Format");
        var bitrate = MatchSpan(detailsHtml, "Bitrate");
        var fileSize = MatchFileSize(detailsHtml);
        var postedDate = Regex.Match(detailsHtml, @"Posted:\s*([^<]+)").Groups[1].Value.Trim();

        var infoNode = post.SelectSingleNode(".//*[contains(@class,'postInfo')]");
        var language = string.Empty;
        if (infoNode != null)
        {
            var langMatch = Regex.Match(infoNode.InnerText, @"Language:\s*(.*?)(?:\s*Keywords:|$)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (langMatch.Success)
            {
                language = langMatch.Groups[1].Value.Trim();
            }
        }

        var qualityBits = string.Join(" ", new[] { format, bitrate }.Where(s => !string.IsNullOrEmpty(s)));

        var result = new IndexerSearchResult
        {
            Id = Guid.NewGuid().ToString(),
            Title = title,
            Artist = "Unknown",
            Album = title,
            Category = "Audiobook",
            Size = ParseSizeToBytes(fileSize),
            Seeders = 0,   // ABB search page does not expose seeder counts
            Leechers = 0,
            DownloadType = "Torrent",
            Format = string.IsNullOrEmpty(format) ? string.Empty : format,
            Quality = string.IsNullOrEmpty(qualityBits) ? null : qualityBits,
            Source = $"{indexer.Name} (AudioBookBay)",
            ResultUrl = detailUrl,
            PublishedDate = postedDate,
            Language = string.IsNullOrEmpty(language) || language == "N/A" ? null : language,
            IndexerId = indexer.Id,
            IndexerImplementation = indexer.Implementation,
        };

        return new ParsedPost { Result = result, DetailUrl = detailUrl };
    }

    private async Task<string?> ExtractMagnetAsync(string detailUrl)
    {
        try
        {
            var html = await GetHtmlAsync(detailUrl);
            if (html == null)
            {
                return null;
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var cells = doc.DocumentNode.SelectNodes("//td");
            if (cells == null)
            {
                return null;
            }

            string? infoHash = null;
            var trackers = new List<string>();

            for (var i = 0; i < cells.Count; i++)
            {
                var text = HtmlEntity.DeEntitize(cells[i].InnerText).Trim();

                if (infoHash == null && Regex.IsMatch(text, "info\\s*hash", RegexOptions.IgnoreCase))
                {
                    // value is the next <td>
                    if (i + 1 < cells.Count)
                    {
                        infoHash = HtmlEntity.DeEntitize(cells[i + 1].InnerText).Trim();
                    }
                }
                else if (Regex.IsMatch(text, "^(udp|https?)://", RegexOptions.IgnoreCase))
                {
                    trackers.Add(text);
                }
            }

            if (string.IsNullOrEmpty(infoHash))
            {
                return null;
            }

            if (trackers.Count == 0)
            {
                trackers.AddRange(FallbackTrackers);
            }

            var trackerQuery = string.Join("&", trackers.Select(t => "tr=" + Uri.EscapeDataString(t)));
            return $"magnet:?xt=urn:btih:{infoHash}&{trackerQuery}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogWarning(ex, "Failed to extract magnet from {DetailUrl}", detailUrl);
            return null;
        }
    }

    private async Task<string?> GetHtmlAsync(string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        var resp = await _httpClient.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("AudioBookBay GET {Url} returned {Status}", url, resp.StatusCode);
            return null;
        }
        return await resp.Content.ReadAsStringAsync();
    }

    private static string ResolveHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return DefaultHost;
        }

        var trimmed = url.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var abs) && !string.IsNullOrEmpty(abs.Host))
        {
            return abs.Host;
        }

        // Bare host or host/path
        return trimmed.Replace("https://", "").Replace("http://", "").Split('/')[0];
    }

    private static string MatchSpan(string html, string label)
    {
        var m = Regex.Match(html, label + @":\s*<span[^>]*>([^<]+)</span>", RegexOptions.IgnoreCase);
        return m.Success ? HtmlEntity.DeEntitize(m.Groups[1].Value).Trim() : string.Empty;
    }

    private static string MatchFileSize(string html)
    {
        var m = Regex.Match(html, @"File Size:\s*<span[^>]*>([^<]+)</span>\s*([^<]+)", RegexOptions.IgnoreCase);
        return m.Success ? $"{m.Groups[1].Value.Trim()} {m.Groups[2].Value.Trim()}" : string.Empty;
    }

    /// <summary>Parses human sizes like "359.13 MB" / "1.2 GB" into bytes.</summary>
    private static long ParseSizeToBytes(string size)
    {
        if (string.IsNullOrWhiteSpace(size))
        {
            return 0;
        }

        var m = Regex.Match(size, @"([\d.,]+)\s*(KB|MB|GB|TB|B)", RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            return 0;
        }

        if (!double.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
        {
            return 0;
        }

        var multiplier = m.Groups[2].Value.ToUpperInvariant() switch
        {
            "B" => 1L,
            "KB" => 1024L,
            "MB" => 1024L * 1024,
            "GB" => 1024L * 1024 * 1024,
            "TB" => 1024L * 1024 * 1024 * 1024,
            _ => 1L,
        };

        return (long)(value * multiplier);
    }
}
