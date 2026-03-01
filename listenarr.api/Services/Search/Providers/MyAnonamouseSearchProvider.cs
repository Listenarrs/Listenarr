using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AsyncKeyedLock;
using Listenarr.Api.Models;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;
using Microsoft.Extensions.Logging;

namespace Listenarr.Api.Services.Search.Providers
{
    /// <summary>
    /// Search provider for MyAnonamouse private tracker.
    /// Handles cookie-based authentication, JSON API responses, and optional per-result enrichment.
    /// </summary>
    public class MyAnonamouseSearchProvider : IIndexerSearchProvider
    {
        private readonly ILogger<MyAnonamouseSearchProvider> _logger;
        private readonly HttpClient _httpClient;
        private readonly ListenArrDbContext _dbContext;

        public string IndexerType => "MyAnonamouse";

        public MyAnonamouseSearchProvider(
            ILogger<MyAnonamouseSearchProvider> logger,
            HttpClient httpClient,
            ListenArrDbContext dbContext)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        public async Task<List<IndexerSearchResult>> SearchAsync(Indexer indexer, string query, string? category, SearchRequest? request = null)
        {
            try
            {
                _logger.LogInformation("Searching MyAnonamouse for: {Query}", query);

                // Parse mam_id from AdditionalSettings (robust: case-insensitive and nested)
                var mamId = MyAnonamouseHelper.TryGetMamId(indexer.AdditionalSettings);

                if (string.IsNullOrEmpty(mamId))
                {
                    _logger.LogWarning("MyAnonamouse indexer {Name} missing mam_id", indexer.Name);
                    return new List<IndexerSearchResult>();
                }

                // Build MyAnonamouse API request (mam_id is sent as a cookie)
                // Use the JSON form endpoint with application/x-www-form-urlencoded payload
                var url = $"{indexer.Url.TrimEnd('/')}/tor/js/loadSearchJSONbasic.php";

                // Try to parse title/author from the query to give MyAnonamouse more targeted fields
                var (parsedTitle, parsedAuthor) = ParseTitleAuthorFromQuery(query);

                // Decide searchType: prefer a targeted search when we only have title or only author
                var searchType = "all";
                if (!string.IsNullOrWhiteSpace(parsedTitle) && string.IsNullOrWhiteSpace(parsedAuthor)) searchType = "title";
                if (string.IsNullOrWhiteSpace(parsedTitle) && !string.IsNullOrWhiteSpace(parsedAuthor)) searchType = "author";

                // Build JSON payload according to new MyAnonamouse structure
                // Build tor object to mirror the browse.php parameter shapes (tor[text], tor[srchIn][field]=true, tor[cat][]=...)
                var srchInDict = new Dictionary<string, bool>
                {
                    ["title"] = true,
                    ["author"] = true,
                    ["narrator"] = true,
                    ["series"] = true,
                    ["description"] = false, // default off (Prowlarr default)
                    ["filenames"] = true,     // search filenames by default (Prowlarr default)
                    ["filetype"] = true
                };

                // Apply request overrides if present
                if (request?.MyAnonamouse != null)
                {
                    var opts = request.MyAnonamouse;
                    if (opts.SearchInDescription.HasValue)
                        srchInDict["description"] = opts.SearchInDescription.Value;
                    if (opts.SearchInSeries.HasValue)
                        srchInDict["series"] = opts.SearchInSeries.Value;
                    if (opts.SearchInFilenames.HasValue)
                        srchInDict["filename"] = opts.SearchInFilenames.Value;
                }

                var torObject = new Dictionary<string, object>
                {
                    ["text"] = query,
                    ["srchIn"] = srchInDict,
                    ["searchType"] = searchType,
                    ["searchIn"] = "torrents",
                    // Keep explicit cat[] list copied from the browse URL
                    ["cat"] = new[] { "39", "49", "50", "83", "51", "97", "40", "41", "106", "42", "52", "98", "54", "55", "43", "99", "84", "44", "56", "45", "57", "85", "87", "119", "88", "58", "59", "46", "47", "53", "89", "100", "108", "48", "111", "0" },
                    // Keep main_cat for explicit audiobook focus (some handlers honor it)
                    ["main_cat"] = new[] { "13" },
                    // Additional browse.php parameters observed in the URL
                    ["browse_lang"] = new[] { "1" },
                    ["browseFlagsHideVsShow"] = "0",
                    ["unit"] = "1",
                    ["startDate"] = string.Empty,
                    ["endDate"] = string.Empty,
                    ["hash"] = string.Empty,
                    ["sortType"] = "default",
                    ["startNumber"] = "0",
                    ["perpage"] = "100"
                };

                var jsonPayload = JsonSerializer.Serialize(new Dictionary<string, object> { ["tor"] = torObject });

                // Build query string as per MyAnonamouse loadSearchJSONbasic: include per-field keys for 'tor' structure
                var queryParams = new List<KeyValuePair<string, string>>();
                queryParams.Add(new KeyValuePair<string, string>("tor[text]", query));
                queryParams.Add(new KeyValuePair<string, string>("tor[searchIn]", "torrents"));

                // Add categories
                foreach (var catVal in new[] { "39", "49", "50", "83", "51", "97", "40", "41", "106", "42", "52", "98", "54", "55", "43", "99", "84", "44", "56", "45", "57", "85", "87", "119", "88", "58", "59", "46", "47", "53", "89", "100", "108", "48", "111", "0" })
                {
                    queryParams.Add(new KeyValuePair<string, string>("tor[cat][]", catVal));
                }
                queryParams.Add(new KeyValuePair<string, string>("tor[main_cat][]", "13"));

                // Add browse_lang, other params
                queryParams.Add(new KeyValuePair<string, string>("tor[browse_lang][]", "1"));
                queryParams.Add(new KeyValuePair<string, string>("tor[browseFlagsHideVsShow]", "0"));
                queryParams.Add(new KeyValuePair<string, string>("tor[sortType]", "default"));
                queryParams.Add(new KeyValuePair<string, string>("tor[startNumber]", "0"));
                queryParams.Add(new KeyValuePair<string, string>("tor[perpage]", "100"));

                // Add srchIn parameters: for each field that is true/false
                var srchInValues = srchInDict;
                if (srchInValues != null && srchInValues.Count > 0)
                {
                    foreach (var kv in srchInValues)
                    {
                        queryParams.Add(new KeyValuePair<string, string>($"tor[srchIn][{kv.Key}]", kv.Value ? "true" : "false"));
                    }
                }
                // Add explicit searchType (title/author/all)
                queryParams.Add(new KeyValuePair<string, string>("tor[searchType]", searchType));

                // Apply filter flags based on request options (e.g., active, freeleech, vip)
                if (request?.MyAnonamouse?.Filter != null)
                {
                    switch (request.MyAnonamouse.Filter)
                    {
                        case MamTorrentFilter.Active:
                            queryParams.Add(new KeyValuePair<string, string>("tor[onlyActive]", "1"));
                            break;
                        case MamTorrentFilter.Freeleech:
                            queryParams.Add(new KeyValuePair<string, string>("tor[onlyFreeleech]", "1"));
                            break;
                        case MamTorrentFilter.FreeleechOrVip:
                            queryParams.Add(new KeyValuePair<string, string>("tor[freeleechOrVip]", "1"));
                            break;
                        case MamTorrentFilter.Vip:
                            queryParams.Add(new KeyValuePair<string, string>("tor[onlyVip]", "1"));
                            break;
                        case MamTorrentFilter.NotVip:
                            queryParams.Add(new KeyValuePair<string, string>("tor[notVip]", "1"));
                            break;
                    }
                }

                // Apply freeleech wedge preference
                var freeleechWedge = request?.MyAnonamouse?.FreeleechWedge;
                if (freeleechWedge != null)
                {
                    queryParams.Add(new KeyValuePair<string, string>("tor[freeleechWedge]", freeleechWedge.Value.ToString().ToLowerInvariant()));
                }

                var qs = string.Join("&", queryParams.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value ?? string.Empty)}"));
                var fullUrl = url + (qs.Length > 0 ? "?" + qs : string.Empty);

                _logger.LogInformation("MyAnonamouse outgoing query (loadSearchJSONbasic): {Query}", qs);

                var mamRequest = new HttpRequestMessage(HttpMethod.Get, fullUrl);
                // Add browser-like headers to avoid "invalid request" errors
                mamRequest.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                mamRequest.Headers.Accept.ParseAdd("application/json, text/javascript, */*; q=0.01");
                mamRequest.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
                mamRequest.Headers.Referrer = new Uri("https://www.myanonamouse.net/");

                // Prefer using the injected HttpClient in tests (so DelegatingHandler stubs can capture requests)
                HttpClient? disposableClient = null;
                HttpClient httpClientToUse = _httpClient;
                List<IndexerSearchResult> results = new List<IndexerSearchResult>();
                try
                {
                    if (_httpClient?.BaseAddress == null)
                    {
                        httpClientToUse = MyAnonamouseHelper.CreateAuthenticatedHttpClient(mamId, indexer.Url);
                        disposableClient = httpClientToUse;
                    }
                    else
                    {
                        // Add cookie header for injected client so the request is authenticated for MAM.
                        // This keeps test handlers in place even when the BaseAddress host differs.
                        if (!string.IsNullOrEmpty(mamId))
                        {
                            mamRequest.Headers.Add("Cookie", $"mam_id={mamId}");
                        }
                    }

                    _logger.LogDebug("MyAnonamouse API URL: {Url}", LogRedaction.RedactText(url, LogRedaction.GetSensitiveValuesFromEnvironment().Concat(new[] { indexer.ApiKey ?? string.Empty })));

                    var response = await httpClientToUse.SendAsync(mamRequest);
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("MyAnonamouse returned status {Status}", response.StatusCode);
                        var errorContent = await response.Content.ReadAsStringAsync();
                        _logger.LogWarning("MyAnonamouse error response: {Content}", LogRedaction.RedactText(errorContent, LogRedaction.GetSensitiveValuesFromEnvironment().Concat(new[] { indexer.ApiKey ?? string.Empty })));
                        return new List<IndexerSearchResult>();
                    }

                    // Capture and persist an updated mam_id cookie if the tracker provided one in Set-Cookie
                    try
                    {
                        var newMam = MyAnonamouseHelper.TryExtractMamIdFromResponse(response);
                        if (!string.IsNullOrEmpty(newMam) && !string.Equals(newMam, mamId, StringComparison.Ordinal))
                        {
                            _logger.LogInformation("MyAnonamouse: received updated mam_id from response for indexer {Name}", indexer.Name);
                            var idx = await _dbContext.Indexers.FindAsync(indexer.Id);
                            if (idx != null)
                            {
                                idx.AdditionalSettings = MyAnonamouseHelper.UpdateMamIdInAdditionalSettings(idx.AdditionalSettings, newMam);
                                _dbContext.Indexers.Update(idx);
                                await _dbContext.SaveChangesAsync();
                                mamId = newMam;
                            }
                        }
                    }
                    catch (Exception exMam) when (exMam is not OperationCanceledException && exMam is not OutOfMemoryException && exMam is not StackOverflowException) {
                        _logger.LogDebug(exMam, "Failed to persist updated mam_id from MyAnonamouse response");
                    }

                    var jsonResponse = await response.Content.ReadAsStringAsync();
                    _logger.LogDebug("MyAnonamouse raw response: {Response}", jsonResponse);
                    results = ParseMyAnonamouseResponse(jsonResponse, indexer);

                    // Optional per-result enrichment: fetch individual item pages to populate missing fields
                    try
                    {
                        // Respect global IncludeEnrichment and per-indexer MyAnonamouse options
                        var shouldEnrich = (request?.IncludeEnrichment ?? false) && (request?.MyAnonamouse?.EnrichResults == true);
                        if (shouldEnrich)
                        {
                            var enrichTop = request?.MyAnonamouse?.EnrichTopResults ?? 3;
                            await EnrichMyAnonamouseResultsAsync(indexer, results, enrichTop, mamId, httpClientToUse);
                        }
                    }
                    catch (Exception exEnrich) when (exEnrich is not OperationCanceledException && exEnrich is not OutOfMemoryException && exEnrich is not StackOverflowException) {
                        _logger.LogWarning(exEnrich, "MyAnonamouse enrichment step failed");
                    }
                }
                finally
                {
                    disposableClient?.Dispose();
                }

                _logger.LogInformation("MyAnonamouse returned {Count} results", results.Count);
                return results;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error searching MyAnonamouse indexer {Name}", indexer.Name);
                return new List<IndexerSearchResult>();
            }
        }

        private List<IndexerSearchResult> ParseMyAnonamouseResponse(string jsonResponse, Indexer indexer)
        {
            var results = new List<IndexerSearchResult>();

            if (indexer == null)
            {
                _logger.LogError("ParseMyAnonamouseResponse called with null indexer");
                return results;
            }

            try
            {
                _logger.LogDebug("Parsing MyAnonamouse response, length: {Length}", jsonResponse.Length);

                JsonDocument? doc = null;
                JsonElement dataArrayElement = default;

                // Try to parse JSON directly. If that fails, try to extract the first JSON array substring.
                try
                {
                    doc = JsonDocument.Parse(jsonResponse);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    // Attempt to extract a JSON array from an HTML-wrapped response or stray text
                    var start = jsonResponse.IndexOf('[');
                    var end = jsonResponse.LastIndexOf(']');
                    if (start >= 0 && end > start)
                    {
                        var sub = jsonResponse.Substring(start, end - start + 1);
                        try
                        {
                            doc = JsonDocument.Parse(sub);
                        }
                        catch (Exception parseEx) when (parseEx is not OperationCanceledException && parseEx is not OutOfMemoryException && parseEx is not StackOverflowException) {
                            _logger.LogWarning(parseEx, "Failed to parse extracted JSON array from MyAnonamouse response");
                            return results;
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Unable to locate JSON array in MyAnonamouse response");
                        return results;
                    }
                }

                var root = doc!.RootElement;

                // Support multiple response shapes:
                // 1) Root is an array of items
                // 2) Root is an object with property "data" containing array
                // 3) Root is an object with property "parsed" or "results" or "items"
                if (root.ValueKind == JsonValueKind.Array)
                {
                    dataArrayElement = root;
                }
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("data", out var tmp) && tmp.ValueKind == JsonValueKind.Array)
                    {
                        dataArrayElement = tmp;
                    }
                    else if (root.TryGetProperty("parsed", out tmp) && tmp.ValueKind == JsonValueKind.Array)
                    {
                        dataArrayElement = tmp;
                    }
                    else if (root.TryGetProperty("results", out tmp) && tmp.ValueKind == JsonValueKind.Array)
                    {
                        dataArrayElement = tmp;
                    }
                    else if (root.TryGetProperty("items", out tmp) && tmp.ValueKind == JsonValueKind.Array)
                    {
                        dataArrayElement = tmp;
                    }
                    else
                    {
                        // As a last resort, try to find the first array value anywhere in the object
                        foreach (var prop in root.EnumerateObject())
                        {
                            if (prop.Value.ValueKind == JsonValueKind.Array)
                            {
                                dataArrayElement = prop.Value;
                                break;
                            }
                        }

                        if (dataArrayElement.ValueKind == JsonValueKind.Undefined)
                        {
                            _logger.LogWarning("MyAnonamouse response did not contain an expected array property");
                            return results;
                        }
                    }
                }
                else
                {
                    _logger.LogWarning("Unexpected MyAnonamouse root JSON kind: {Kind}", root.ValueKind);
                    return results;
                }

                _logger.LogDebug("Found {Count} MyAnonamouse results", dataArrayElement.GetArrayLength());
                
                int _mamDebugIndex = 0;
                foreach (var item in dataArrayElement.EnumerateArray())
                {
                    try
                    {
                        // Log property names for first few items to aid debugging
                        if (_mamDebugIndex < 3)
                        {
                            try
                            {
                                var propertyNames = item.EnumerateObject().Select(p => p.Name).ToList();
                                _logger.LogInformation("MyAnonamouse result #{Index} has properties: {Properties}", _mamDebugIndex, string.Join(", ", propertyNames));
                            }
                            catch (Exception exNames) when (exNames is not OperationCanceledException && exNames is not OutOfMemoryException && exNames is not StackOverflowException) {
                                _logger.LogDebug(exNames, "Failed to enumerate property names for MyAnonamouse result #{Index}", _mamDebugIndex);
                            }
                        }

                        string id;
                        if (item.TryGetProperty("id", out var idElem))
                        {
                            id = idElem.ValueKind == JsonValueKind.String ? idElem.GetString() ?? "" : idElem.ToString();
                        }
                        else
                        {
                            id = Guid.NewGuid().ToString();
                        }

                        // MyAnonamouse uses "title" in responses; fall back to "name" if needed
                        var title = "";
                        if (item.TryGetProperty("title", out var titleElem))
                        {
                            title = titleElem.ValueKind == JsonValueKind.String ? titleElem.GetString() ?? "" : titleElem.ToString();
                        }
                        else if (item.TryGetProperty("name", out titleElem))
                        {
                            title = titleElem.ValueKind == JsonValueKind.String ? titleElem.GetString() ?? "" : titleElem.ToString();
                        }
                        
                        var sizeStr = "";
                        if (item.TryGetProperty("size", out var sizeElem))
                        {
                            if (sizeElem.ValueKind == JsonValueKind.String)
                            {
                                sizeStr = sizeElem.GetString() ?? "0";
                            }
                            else if (sizeElem.ValueKind == JsonValueKind.Number)
                            {
                                sizeStr = sizeElem.GetInt64().ToString();
                            }
                            else
                            {
                                sizeStr = "0";
                            }
                        }
                        
                        var seeders = item.TryGetProperty("seeders", out var seedElem) ? seedElem.GetInt32() : 0;
                        var leechers = item.TryGetProperty("leechers", out var leechElem) ? leechElem.GetInt32() : 0;
                        
                        string dlHash = string.Empty;
                        if (item.TryGetProperty("dl", out var dlElem))
                        {
                            dlHash = dlElem.ValueKind == JsonValueKind.String ? dlElem.GetString() ?? string.Empty : dlElem.ToString();
                        }
                        
                        // Get torrent ID for download URL fallback (note: 'id' already parsed above as variable 'id')
                        string torrentId = id;
                        
                        // Debug logging for first result
                        if (_mamDebugIndex == 0)
                        {
                            _logger.LogInformation("MyAnonamouse first result - Title: '{Title}', Size: '{Size}', Seeders: {Seeders}, DlHash: '{DlHash}', TorrentId: '{TorrentId}'", 
                                title, sizeStr, seeders, dlHash, torrentId);
                        }

                        // Explicit downloadUrl / infoUrl / fileName fields
                        string? downloadUrlField = null;
                        string? infoUrlField = null;
                        string? fileNameField = null;
                        foreach (var prop in item.EnumerateObject())
                        {
                            var name = prop.Name;
                            if (downloadUrlField == null && string.Equals(name, "downloadUrl", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String)
                                downloadUrlField = prop.Value.GetString();
                            if (infoUrlField == null && string.Equals(name, "infoUrl", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String)
                                infoUrlField = prop.Value.GetString();
                            if (fileNameField == null && string.Equals(name, "fileName", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String)
                                fileNameField = prop.Value.GetString();
                        }

                        string category = string.Empty;
                        if (item.TryGetProperty("catname", out var catElem))
                        {
                            category = catElem.ValueKind == JsonValueKind.String ? catElem.GetString() ?? string.Empty : catElem.ToString();
                        }

                        string tags = string.Empty;
                        if (item.TryGetProperty("tags", out var tagsElem))
                        {
                            tags = tagsElem.ValueKind == JsonValueKind.String ? tagsElem.GetString() ?? string.Empty : tagsElem.ToString();
                        }

                        string description = string.Empty;
                        if (item.TryGetProperty("description", out var descElem))
                        {
                            description = descElem.ValueKind == JsonValueKind.String ? descElem.GetString() ?? string.Empty : descElem.ToString();
                        }

                        // Parse grabs/files with multiple field name variations
                        int grabs = 0;
                        var grabKeys = new[] { "grabs", "snatches", "snatched", "snatched_count", "snatches_count", "numgrabs", "num_grabs", "grab_count", "times_completed", "time_completed", "downloaded", "times_downloaded", "completed" };
                        foreach (var key in grabKeys)
                        {
                            if (item.TryGetProperty(key, out var gEl))
                            {
                                if (gEl.ValueKind == JsonValueKind.Number)
                                {
                                    grabs = gEl.GetInt32();
                                    break;
                                }
                                else if (gEl.ValueKind == JsonValueKind.String && int.TryParse(gEl.GetString(), out var gtmp))
                                {
                                    grabs = gtmp;
                                    break;
                                }
                            }
                        }

                        int files = 0;
                        if (item.TryGetProperty("files", out var filesElem))
                        {
                            if (filesElem.ValueKind == JsonValueKind.Number)
                            {
                                files = filesElem.GetInt32();
                            }
                            else if (filesElem.ValueKind == JsonValueKind.String && int.TryParse(filesElem.GetString(), out var ftmp))
                            {
                                files = ftmp;
                            }
                        }

                        // Parse PublishDate with multiple field names and formats
                        DateTime? publishDate = null;
                        var dateKeys = new[] { "added", "publishDate", "pubDate", "published", "date", "created", "created_at", "upload_date" };
                        foreach (var key in dateKeys)
                        {
                            if (item.TryGetProperty(key, out var dateElem))
                            {
                                if (dateElem.ValueKind == JsonValueKind.String)
                                {
                                    var dateStr = dateElem.GetString();
                                    if (!string.IsNullOrEmpty(dateStr) && DateTime.TryParse(dateStr, out var dt))
                                    {
                                        publishDate = dt;
                                        break;
                                    }
                                }
                                else if (dateElem.ValueKind == JsonValueKind.Number)
                                {
                                    var timestamp = dateElem.GetInt64();
                                    publishDate = DateTimeOffset.FromUnixTimeSeconds(timestamp).DateTime;
                                    break;
                                }
                            }
                        }

                        // Parse size
                        long size = 0;
                        // Try parsing sizeStr as human-readable format first (e.g., "3.7 GiB")
                        if (!string.IsNullOrEmpty(sizeStr))
                        {
                            size = ExtractSizeFromDescription(sizeStr);
                            if (size > 0)
                            {
                                _logger.LogDebug("Parsed size for MyAnonamouse result '{Title}': {Size} bytes from size field '{SizeStr}'", title, size, sizeStr);
                            }
                            // Fallback: try parsing as plain number (bytes)
                            else if (long.TryParse(sizeStr, out var parsedSize))
                            {
                                size = parsedSize;
                                _logger.LogDebug("Parsed size for MyAnonamouse result '{Title}': {Size} bytes (numeric) from size field '{SizeStr}'", title, size, sizeStr);
                            }
                        }
                        
                        // If still no size, try to extract from description
                        if (size == 0)
                        {
                            size = ExtractSizeFromDescription(description);
                            if (size > 0)
                            {
                                _logger.LogDebug("Parsed size for MyAnonamouse result '{Title}': {Size} bytes from description", title, size);
                            }
                            else
                            {
                                _logger.LogWarning("MyAnonamouse result '{Title}' has no size information", title);
                            }
                        }

                        // Extract author from author_info JSON
                        string? author = null;
                        if (item.TryGetProperty("author_info", out var authorInfo))
                        {
                            var authorJson = authorInfo.GetString();
                            if (!string.IsNullOrEmpty(authorJson))
                            {
                                try
                                {
                                    var authorDoc = JsonDocument.Parse(authorJson);
                                    var authors = new List<string>();
                                    foreach (var prop in authorDoc.RootElement.EnumerateObject())
                                    {
                                        authors.Add(prop.Value.GetString() ?? "");
                                    }
                                    author = string.Join(", ", authors.Where(a => !string.IsNullOrEmpty(a)));
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                                    _logger.LogWarning(ex, "Failed to parse author JSON for search result");
                                }
                            }
                        }

                        // Extract narrator from narrator_info JSON
                        string? narrator = null;
                        if (item.TryGetProperty("narrator_info", out var narratorInfo))
                        {
                            var narratorJson = narratorInfo.GetString();
                            if (!string.IsNullOrEmpty(narratorJson))
                            {
                                try
                                {
                                    var narratorDoc = JsonDocument.Parse(narratorJson);
                                    var narrators = new List<string>();
                                    foreach (var prop in narratorDoc.RootElement.EnumerateObject())
                                    {
                                        narrators.Add(prop.Value.GetString() ?? "");
                                    }
                                    narrator = string.Join(", ", narrators.Where(n => !string.IsNullOrEmpty(n)));
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                                    _logger.LogWarning(ex, "Failed to parse narrator JSON for search result");
                                }
                            }
                        }

                        // Detect quality and format
                        string rawFormatField = string.Empty;
                        foreach (var prop in item.EnumerateObject())
                        {
                            if (prop.Value.ValueKind == JsonValueKind.String && (string.Equals(prop.Name, "format", StringComparison.OrdinalIgnoreCase) || string.Equals(prop.Name, "filetype", StringComparison.OrdinalIgnoreCase)))
                            {
                                rawFormatField = prop.Value.GetString() ?? string.Empty;
                                break;
                            }
                        }

                        var formatFromTags = DetectFormatFromTags(tags ?? "");
                        var formatFromField = !string.IsNullOrEmpty(rawFormatField) ? DetectFormatFromTags(rawFormatField) : null;
                        var finalFormat = (formatFromField != null && formatFromField != "MP3") ? formatFromField : formatFromTags;

                        var qualityFromTags = DetectQualityFromTags(tags ?? "");
                        var qualityFromFormat = !string.IsNullOrEmpty(rawFormatField) ? DetectQualityFromFormat(rawFormatField) : "Unknown";
                        
                        // Prefer bitrate from tags over format-based quality
                        var finalQuality = qualityFromTags != "Unknown" ? qualityFromTags : qualityFromFormat;

                        // Fallback quality detection
                        if (finalQuality == "Unknown" || finalQuality == "Variable")
                        {
                            if (!string.IsNullOrEmpty(description))
                            {
                                var q = DetectQualityFromTags(description);
                                if (q != "Unknown") finalQuality = q;
                            }

                            if (finalQuality == "Unknown" || finalQuality == "Variable")
                            {
                                var q = DetectQualityFromTags(title ?? string.Empty);
                                if (q != "Unknown") finalQuality = q;
                            }
                        }

                        // Build download URL
                        var downloadUrl = "";
                        
                        // First priority: use explicit downloadUrl field if provided
                        if (!string.IsNullOrEmpty(downloadUrlField))
                        {
                            downloadUrl = downloadUrlField;
                            _logger.LogDebug("Using explicit downloadUrl field for '{Title}': {Url}", title, downloadUrl);
                        }
                        // Second priority: build from dlHash
                        else if (!string.IsNullOrEmpty(dlHash))
                        {
                            var baseUrl = (indexer?.Url ?? "https://www.myanonamouse.net").TrimEnd('/');
                            downloadUrl = $"{baseUrl}/tor/download.php/{dlHash}";
                            var mamIdLocal = MyAnonamouseHelper.TryGetMamId(indexer?.AdditionalSettings);
                            if (!string.IsNullOrEmpty(mamIdLocal))
                            {
                                try
                                {
                                    mamIdLocal = Uri.UnescapeDataString(mamIdLocal);
                                }
                                catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException) { 
                                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                }
                                downloadUrl += $"?mam_id={Uri.EscapeDataString(mamIdLocal)}";
                            }
                            _logger.LogDebug("Built downloadUrl from dlHash for '{Title}': {Url}", title, downloadUrl);
                        }
                        // Third priority: build from torrent ID (MAM Direct API pattern)
                        else if (!string.IsNullOrEmpty(torrentId))
                        {
                            var baseUrl = (indexer?.Url ?? "https://www.myanonamouse.net").TrimEnd('/');
                            var mamIdLocal = MyAnonamouseHelper.TryGetMamId(indexer?.AdditionalSettings);
                            if (!string.IsNullOrEmpty(mamIdLocal))
                            {
                                try
                                {
                                    mamIdLocal = Uri.UnescapeDataString(mamIdLocal);
                                }
                                catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException) { 
                                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                                }
                                downloadUrl = $"{baseUrl}/tor/download.php?tid={torrentId}&mam_id={Uri.EscapeDataString(mamIdLocal)}";
                            }
                            else
                            {
                                downloadUrl = $"{baseUrl}/tor/download.php?tid={torrentId}";
                            }
                            _logger.LogDebug("Built downloadUrl from torrent ID for '{Title}': {Url}", title, downloadUrl);
                        }
                        else
                        {
                            _logger.LogWarning("No download URL available for MyAnonamouse result '{Title}' - missing downloadUrl field, dlHash, and torrent ID", title);
                        }
                        
                        _mamDebugIndex++;

                        // Language parsing
                        string rawLangCode = string.Empty;
                        string? language = null;
                        
                        foreach (var prop in item.EnumerateObject())
                        {
                            if ((prop.Name.Equals("lang_code", StringComparison.OrdinalIgnoreCase) || 
                                 prop.Name.Equals("language_code", StringComparison.OrdinalIgnoreCase) || 
                                 prop.Name.Equals("lang", StringComparison.OrdinalIgnoreCase) || 
                                 prop.Name.Equals("language", StringComparison.OrdinalIgnoreCase)) && 
                                prop.Value.ValueKind == JsonValueKind.String)
                            {
                                rawLangCode = prop.Value.GetString() ?? string.Empty;
                                break;
                            }
                        }

                        if (!string.IsNullOrEmpty(rawLangCode))
                        {
                            language = ParseLanguageFromCode(rawLangCode);
                        }

                        var result = new IndexerSearchResult
                        {
                            Id = id,
                            Title = title ?? "Unknown",
                            Artist = author ?? "Unknown Author",
                            Album = narrator != null ? $"Narrated by {narrator}" : "Unknown",
                            Category = category ?? "Audiobook",
                            Size = size,
                            Seeders = seeders > 0 ? seeders : null,
                            Leechers = leechers > 0 ? leechers : null,
                            Source = indexer?.Name ?? "MyAnonamouse",
                            PublishedDate = publishDate?.ToString("o") ?? string.Empty,
                            Quality = finalQuality,
                            Format = finalFormat,
                            TorrentUrl = downloadUrl,
                            ResultUrl = !string.IsNullOrEmpty(id) ? $"https://myanonamouse.net/t/{Uri.EscapeDataString(id)}" : (indexer?.Url ?? ""),
                            MagnetLink = "",
                            NzbUrl = "",
                            DownloadType = "Torrent",
                            IndexerId = indexer?.Id,
                            IndexerImplementation = indexer?.Implementation ?? string.Empty,
                            Grabs = grabs,
                            Files = files,
                            Language = language ?? string.Empty,
                            TorrentFileName = fileNameField ?? string.Empty
                        };

                        // VIP marker
                        if (item.TryGetProperty("vip", out var vipElem))
                        {
                            if (vipElem.ValueKind == JsonValueKind.True || (vipElem.ValueKind == JsonValueKind.String && string.Equals(vipElem.GetString(), "true", StringComparison.OrdinalIgnoreCase)))
                            {
                                result.Title ??= string.Empty;
                                if (!result.Title.EndsWith(" [VIP]")) result.Title = result.Title + " [VIP]";
                            }
                        }

                        // Log critical fields for debugging
                        if (_mamDebugIndex < 3)
                        {
                            _logger.LogInformation("MAM Result #{Index}: Title='{Title}', Size={Size} bytes, Seeders={Seeders}, TorrentUrl='{TorrentUrl}', DownloadType='{DownloadType}'", 
                                _mamDebugIndex, result.Title, result.Size, result.Seeders, result.TorrentUrl, result.DownloadType);
                        }

                        results.Add(result);
                        _mamDebugIndex++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                        _logger.LogWarning(ex, "Failed to parse MyAnonamouse result item");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Failed to parse MyAnonamouse response");
            }

            return results;
        }

        private async Task EnrichMyAnonamouseResultsAsync(Indexer indexer, List<IndexerSearchResult> results, int topN, string? mamId, HttpClient httpClient)
        {
            if (results == null || results.Count == 0) return;
            if (topN <= 0) return;

            var candidates = results.Where(r => (r.Grabs == 0 || r.Files == 0 || string.IsNullOrEmpty(r.Format) || string.IsNullOrEmpty(r.Language))).Take(topN).ToList();
            if (!candidates.Any()) return;

            _logger.LogDebug("Enriching {Count} MyAnonamouse results (topN={TopN})", candidates.Count, topN);

            var sem = new AsyncNonKeyedLocker(4);
            var tasks = candidates.Select(async r =>
            {
                using var _ = await sem.LockAsync();
                try
                {
                    if (string.IsNullOrEmpty(r.ResultUrl)) return;

                    // Extract torrent ID from result URL
                    var idMatch = Regex.Match(r.ResultUrl, @"/t/(\d+)");
                    if (!idMatch.Success) return;
                    var torrentId = idMatch.Groups[1].Value;

                    // Request JSON detail endpoint
                    var detailUrl = $"{indexer.Url.TrimEnd('/')}/tor/js/loadTorrentJSONBasic.php?id={torrentId}";
                    var req = new HttpRequestMessage(HttpMethod.Get, detailUrl);
                    req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                    req.Headers.Accept.ParseAdd("application/json");
                    if (!string.IsNullOrEmpty(mamId)) req.Headers.Add("Cookie", $"mam_id={mamId}");

                    var resp = await httpClient.SendAsync(req);
                    if (!resp.IsSuccessStatusCode) return;
                    var json = await resp.Content.ReadAsStringAsync();

                    // Parse JSON for enrichment fields
                    try
                    {
                        var detail = JsonDocument.Parse(json).RootElement;
                        
                        if (detail.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Object)
                        {
                            detail = dataProp;
                        }
                        else if (detail.TryGetProperty("response", out var respProp) && respProp.ValueKind == JsonValueKind.Object)
                        {
                            detail = respProp;
                        }
                        
                        var grabs = 0;
                        var grabKeys = new[] { "grabs", "snatches", "snatched", "snatched_count", "snatches_count", "numgrabs", "num_grabs", "grab_count", "times_completed", "time_completed", "downloaded", "times_downloaded", "completed" };
                        foreach (var key in grabKeys)
                        {
                            if (detail.TryGetProperty(key, out var gEl))
                            {
                                if (gEl.ValueKind == JsonValueKind.Number)
                                {
                                    grabs = gEl.GetInt32();
                                    break;
                                }
                                else if (gEl.ValueKind == JsonValueKind.String && int.TryParse(gEl.GetString(), out var gtmp))
                                {
                                    grabs = gtmp;
                                    break;
                                }
                            }
                        }
                        
                        var files = 0;
                        if (detail.TryGetProperty("files", out var filesElem) && filesElem.ValueKind == JsonValueKind.Number)
                        {
                            files = filesElem.GetInt32();
                        }
                        
                        var format = "";
                        if (detail.TryGetProperty("filetype", out var formatElem) && formatElem.ValueKind == JsonValueKind.String)
                        {
                            format = formatElem.GetString() ?? "";
                        }
                        
                        var langCode = "";
                        if (detail.TryGetProperty("lang_code", out var langElem) && langElem.ValueKind == JsonValueKind.String)
                        {
                            langCode = langElem.GetString() ?? "";
                        }

                        // Apply values
                        if (grabs > 0) r.Grabs = grabs;
                        if (files > 0) r.Files = files;
                        if (!string.IsNullOrEmpty(format) && string.IsNullOrEmpty(r.Format)) r.Format = format.ToUpper();
                        if (!string.IsNullOrEmpty(langCode) && string.IsNullOrEmpty(r.Language)) r.Language = ParseLanguageFromCode(langCode);
                        
                        _logger.LogDebug("Enriched MyAnonamouse result {Id}: grabs={Grabs}, files={Files}, format={Format}, language={Language}", r.Id, r.Grabs, r.Files, r.Format, r.Language);
                    }
                    catch (Exception exParse) when (exParse is not OperationCanceledException && exParse is not OutOfMemoryException && exParse is not StackOverflowException) {
                        _logger.LogDebug(exParse, "Failed to parse MyAnonamouse detail JSON for {Id}", r.Id);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    _logger.LogDebug(ex, "Failed to enrich MyAnonamouse result {Id}", r.Id);
                }
            }).ToArray();

            await Task.WhenAll(tasks);
        }

        private static (string? title, string? author) ParseTitleAuthorFromQuery(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return (null, null);

            var q = query.Trim();

            // Pattern: "Title by Author"
            var byIndex = q.LastIndexOf(" by ", StringComparison.OrdinalIgnoreCase);
            if (byIndex > 0)
            {
                var title = q.Substring(0, byIndex).Trim();
                var author = q.Substring(byIndex + 4).Trim();
                return (string.IsNullOrWhiteSpace(title) ? null : title, string.IsNullOrWhiteSpace(author) ? null : author);
            }

            // Pattern: "Title - Author"
            var dashParts = q.Split(new[] { " - " }, 2, StringSplitOptions.None);
            if (dashParts.Length == 2)
            {
                var title = dashParts[0].Trim();
                var author = dashParts[1].Trim();
                return (string.IsNullOrWhiteSpace(title) ? null : title, string.IsNullOrWhiteSpace(author) ? null : author);
            }

            // Pattern: "Author, Title"
            var commaParts = q.Split(new[] { ',' }, 2);
            if (commaParts.Length == 2)
            {
                var author = commaParts[0].Trim();
                var title = commaParts[1].Trim();
                return (string.IsNullOrWhiteSpace(title) ? null : title, string.IsNullOrWhiteSpace(author) ? null : author);
            }

            return (null, null);
        }

        private long ExtractSizeFromDescription(string description)
        {
            if (string.IsNullOrEmpty(description)) return 0;

            // Support both binary (GiB, MiB, TiB, KiB) and decimal (GB, MB, TB, KB) units
            var match = Regex.Match(description, @"(\d+(?:\.\d+)?)\s*([KMGT]i?B)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                if (double.TryParse(match.Groups[1].Value, out var value))
                {
                    var unit = match.Groups[2].Value.ToUpperInvariant();
                    return unit switch
                    {
                        "TIB" => (long)(value * 1024 * 1024 * 1024 * 1024),
                        "TB" => (long)(value * 1024 * 1024 * 1024 * 1024),
                        "GIB" => (long)(value * 1024 * 1024 * 1024),
                        "GB" => (long)(value * 1024 * 1024 * 1024),
                        "MIB" => (long)(value * 1024 * 1024),
                        "MB" => (long)(value * 1024 * 1024),
                        "KIB" => (long)(value * 1024),
                        "KB" => (long)(value * 1024),
                        _ => 0
                    };
                }
            }
            return 0;
        }

        private string DetectFormatFromTags(string tags)
        {
            if (string.IsNullOrEmpty(tags)) return "MP3";

            var upper = tags.ToUpperInvariant();
            if (upper.Contains("FLAC")) return "FLAC";
            if (upper.Contains("M4B")) return "M4B";
            if (upper.Contains("M4A")) return "M4A";
            if (upper.Contains("AAC")) return "AAC";
            if (upper.Contains("OGG")) return "OGG";
            if (upper.Contains("OPUS")) return "OPUS";
            if (upper.Contains("WMA")) return "WMA";
            if (upper.Contains("MP3")) return "MP3";

            return "MP3";
        }

        private string DetectQualityFromTags(string tags)
        {
            if (string.IsNullOrEmpty(tags)) return "Unknown";

            var match = Regex.Match(tags, @"(\d+)\s*kbps", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return $"{match.Groups[1].Value} kbps";
            }

            return "Unknown";
        }

        private string DetectQualityFromFormat(string format)
        {
            if (string.IsNullOrEmpty(format)) return "Unknown";

            var upper = format.ToUpperInvariant();
            
            // Try to extract bitrate from format string first (e.g., "M4B 64kbps", "MP3 128kbps")
            var bitrateMatch = Regex.Match(format, @"(\d+)\s*kbps", RegexOptions.IgnoreCase);
            if (bitrateMatch.Success)
            {
                return $"{bitrateMatch.Groups[1].Value} kbps";
            }

            // Check for lossless formats
            if (upper.Contains("FLAC")) return "Lossless";
            
            // For variable bitrate formats, try to indicate the format at least
            if (upper.Contains("M4B")) return "M4B";
            if (upper.Contains("M4A")) return "M4A";
            if (upper.Contains("AAC")) return "AAC";
            if (upper.Contains("MP3")) return "MP3";

            return "Unknown";
        }

        private string? ParseLanguageFromCode(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;

            var upper = code.ToUpperInvariant();
            return upper switch
            {
                "ENG" or "EN" => "English",
                "SPA" or "ES" => "Spanish",
                "FRE" or "FRA" or "FR" => "French",
                "GER" or "DEU" or "DE" => "German",
                "ITA" or "IT" => "Italian",
                "POR" or "PT" => "Portuguese",
                "RUS" or "RU" => "Russian",
                "JPN" or "JA" => "Japanese",
                "CHI" or "ZH" => "Chinese",
                "ARA" or "AR" => "Arabic",
                _ => null
            };
        }
    }
}

