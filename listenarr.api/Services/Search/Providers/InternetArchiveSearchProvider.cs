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

namespace Listenarr.Api.Services.Search.Providers;

/// <summary>
/// Search provider for Internet Archive (archive.org)
/// Searches public domain audiobooks from collections like LibriVox
/// </summary>
public class InternetArchiveSearchProvider : IIndexerSearchProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<InternetArchiveSearchProvider> _logger;

    public string IndexerType => "InternetArchive";

    public InternetArchiveSearchProvider(
        HttpClient httpClient,
        ILogger<InternetArchiveSearchProvider> logger)
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
            _logger.LogInformation("Searching Internet Archive for: {Query}", query);

            // Parse collection from AdditionalSettings (default: librivoxaudio)
            var collection = "librivoxaudio";

            if (!string.IsNullOrEmpty(indexer.AdditionalSettings))
            {
                try
                {
                    var settings = JsonDocument.Parse(indexer.AdditionalSettings);
                    if (settings.RootElement.TryGetProperty("collection", out var collectionElem))
                    {
                        var parsedCollection = collectionElem.GetString();
                        if (!string.IsNullOrEmpty(parsedCollection))
                            collection = parsedCollection;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to parse Internet Archive settings, using default collection");
                }
            }

            _logger.LogDebug("Using Internet Archive collection: {Collection}", collection);

            // Build search query - search in title and creator (author) fields
            var searchQuery = $"collection:{collection} AND (title:({query}) OR creator:({query}))";
            var searchUrl = $"https://archive.org/advancedsearch.php?q={Uri.EscapeDataString(searchQuery)}&fl=identifier,title,creator,date,downloads,item_size,description&rows=100&output=json";

            _logger.LogInformation("Internet Archive search URL: {Url}", searchUrl);

            var response = await _httpClient.GetAsync(searchUrl);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Internet Archive returned status {Status}", response.StatusCode);
                return new List<IndexerSearchResult>();
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();
            _logger.LogDebug("Internet Archive response length: {Length}", jsonResponse.Length);

            var searchResults = await ParseInternetArchiveSearchResponse(jsonResponse, indexer);

            _logger.LogInformation("Internet Archive returned {Count} results", searchResults.Count);
            return searchResults;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogError(ex, "Error searching Internet Archive indexer {Name}", indexer.Name);
            return new List<IndexerSearchResult>();
        }
    }

    private async Task<List<IndexerSearchResult>> ParseInternetArchiveSearchResponse(string jsonResponse, Indexer indexer)
    {
        var results = new List<IndexerSearchResult>();

        try
        {
            _logger.LogInformation("Parsing Internet Archive response, length: {Length}", jsonResponse.Length);

            var doc = JsonDocument.Parse(jsonResponse);

            if (!doc.RootElement.TryGetProperty("response", out var responseObj))
            {
                _logger.LogWarning("Internet Archive response missing 'response' object");
                return results;
            }

            if (!responseObj.TryGetProperty("docs", out var docsArray))
            {
                _logger.LogWarning("Internet Archive response missing 'docs' array");
                return results;
            }

            _logger.LogInformation("Found {Count} Internet Archive items in response", docsArray.GetArrayLength());

            // Limit to first 20 results to avoid timeout
            var itemsToProcess = Math.Min(20, docsArray.GetArrayLength());
            _logger.LogInformation("Processing first {Count} of {Total} Internet Archive items", itemsToProcess, docsArray.GetArrayLength());

            var processedCount = 0;
            foreach (var item in docsArray.EnumerateArray())
            {
                if (processedCount >= itemsToProcess)
                {
                    break;
                }
                processedCount++;

                try
                {
                    var identifier = item.TryGetProperty("identifier", out var idElem) ? idElem.GetString() : "";
                    var title = item.TryGetProperty("title", out var titleElem) ? titleElem.GetString() : "";
                    var creator = item.TryGetProperty("creator", out var creatorElem) ? creatorElem.GetString() : "";

                    if (string.IsNullOrEmpty(identifier) || string.IsNullOrEmpty(title))
                    {
                        _logger.LogDebug("Skipping item with missing identifier or title");
                        continue;
                    }

                    _logger.LogDebug("Fetching metadata for {Identifier}", identifier);

                    // Fetch detailed metadata to get file information
                    var metadataUrl = $"https://archive.org/metadata/{identifier}";
                    var metadataResponse = await _httpClient.GetAsync(metadataUrl);

                    if (!metadataResponse.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("Failed to fetch metadata for {Identifier}", identifier);
                        continue;
                    }

                    var metadataJson = await metadataResponse.Content.ReadAsStringAsync();
                    var audioFile = GetBestAudioFile(metadataJson, identifier);

                    if (audioFile == null)
                    {
                        _logger.LogDebug("No suitable audio file found for {Identifier}", identifier);
                        continue;
                    }

                    // Build download URL
                    var downloadUrl = $"https://archive.org/download/{identifier}/{audioFile.FileName}";

                    _logger.LogDebug("Found audio file for {Title}: {FileName} ({Format}, {Size} bytes)",
                        title, audioFile.FileName, audioFile.Format, audioFile.Size);

                    var iaResult = new IndexerSearchResult
                    {
                        Id = Guid.NewGuid().ToString(),
                        Title = title,
                        Artist = creator ?? "Unknown",
                        Album = title,
                        Category = "Audiobook",
                        Size = audioFile.Size,
                        Seeders = 0, // N/A for direct downloads
                        Leechers = 0, // N/A for direct downloads
                        TorrentUrl = downloadUrl, // Using TorrentUrl field for direct download URL
                        // Internet Archive item page
                        ResultUrl = !string.IsNullOrEmpty(identifier) ? $"https://archive.org/details/{identifier}" : null,
                        DownloadType = "DDL", // Direct Download Link
                        Format = audioFile.Format,
                        Quality = DetectQualityFromFormat(audioFile.Format),
                        Source = $"{indexer.Name} (Internet Archive)",
                        PublishedDate = string.Empty,
                        IndexerId = indexer.Id,
                        IndexerImplementation = indexer.Implementation
                    };

                    // Ensure ResultUrl is present (fallback to item page or archive details)
                    if (string.IsNullOrEmpty(iaResult.ResultUrl) && !string.IsNullOrEmpty(identifier))
                    {
                        iaResult.ResultUrl = $"https://archive.org/details/{identifier}";
                    }

                    try
                    {
                        var detectedLang = ParseLanguageFromText(title);
                        if (!string.IsNullOrEmpty(detectedLang)) iaResult.Language = detectedLang;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogDebug(ex, "Failed to parse language from title: {Title}", title);
                    }

                    results.Add(iaResult);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "Error processing Internet Archive item");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogError(ex, "Error parsing Internet Archive response");
        }

        return results;
    }

    private class AudioFileInfo
    {
        public string FileName { get; set; } = "";
        public string Format { get; set; } = "";
        public long Size { get; set; }
        public int Priority { get; set; } // Lower = better
    }

    private AudioFileInfo? GetBestAudioFile(string metadataJson, string identifier)
    {
        try
        {
            var doc = JsonDocument.Parse(metadataJson);

            if (!doc.RootElement.TryGetProperty("files", out var filesArray))
            {
                return null;
            }

            var audioFiles = new List<AudioFileInfo>();

            foreach (var file in filesArray.EnumerateArray())
            {
                var fileName = file.TryGetProperty("name", out var nameElem) ? nameElem.GetString() : "";
                var format = file.TryGetProperty("format", out var formatElem) ? formatElem.GetString() : "";

                // Size can be either a string or a number in Internet Archive API
                long size = 0;
                if (file.TryGetProperty("size", out var sizeElem))
                {
                    if (sizeElem.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        long.TryParse(sizeElem.GetString(), out size);
                    }
                    else if (sizeElem.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        size = sizeElem.GetInt64();
                    }
                }

                if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(format))
                    continue;

                // Assign priority based on format (lower = better)
                int priority = format switch
                {
                    "LibriVox Apple Audiobook" => 1,  // M4B - best quality, multi-chapter
                    "M4B" => 1,
                    "128Kbps MP3" => 2,                // Good quality MP3
                    "VBR MP3" => 3,                    // Variable bitrate MP3
                    "Ogg Vorbis" => 4,                 // OGG format
                    "64Kbps MP3" => 5,                 // Lower quality MP3
                    _ => int.MaxValue                  // Unknown format - lowest priority
                };

                // Only include known audio formats
                if (priority < int.MaxValue)
                {
                    audioFiles.Add(new AudioFileInfo
                    {
                        FileName = fileName,
                        Format = format,
                        Size = size,
                        Priority = priority
                    });
                }
            }

            // Return the highest priority (lowest priority number) audio file
            return audioFiles.OrderBy(f => f.Priority).ThenByDescending(f => f.Size).FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogError(ex, "Error parsing Internet Archive metadata for {Identifier}", identifier);
            return null;
        }
    }

    private string DetectQualityFromFormat(string format)
    {
        if (string.IsNullOrEmpty(format))
            return "Unknown";

        var lowerFormat = format.ToLower();

        if (lowerFormat.Contains("flac"))
            return "FLAC";
        else if (lowerFormat.Contains("m4b") || lowerFormat.Contains("apple audiobook"))
            return "M4B";
        else if (lowerFormat.Contains("320kbps") || lowerFormat.Contains("320 kbps"))
            return "MP3 320kbps";
        else if (lowerFormat.Contains("256kbps") || lowerFormat.Contains("256 kbps"))
            return "MP3 256kbps";
        else if (lowerFormat.Contains("192kbps") || lowerFormat.Contains("192 kbps"))
            return "MP3 192kbps";
        else if (lowerFormat.Contains("128kbps") || lowerFormat.Contains("128 kbps"))
            return "MP3 128kbps";
        else if (lowerFormat.Contains("64kbps") || lowerFormat.Contains("64 kbps"))
            return "MP3 64kbps";
        else if (lowerFormat.Contains("vbr mp3") || lowerFormat.Contains("variable bitrate"))
            return "MP3 VBR";
        else if (lowerFormat.Contains("ogg vorbis") || lowerFormat.Contains("ogg"))
            return "OGG Vorbis";
        else if (lowerFormat.Contains("opus"))
            return "OPUS";
        else if (lowerFormat.Contains("aac"))
            return "AAC";
        else if (lowerFormat.Contains("mp3"))
            return "MP3";
        else
            return "Unknown";
    }

    private string ParseLanguageFromText(string text)
    {
        // Simple language detection based on common patterns
        // This is a placeholder - real implementation would use more sophisticated detection
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var lowerText = text.ToLower();

        // Check for language indicators in title/metadata
        if (lowerText.Contains("english") || lowerText.Contains("en"))
            return "English";
        if (lowerText.Contains("spanish") || lowerText.Contains("español") || lowerText.Contains("es"))
            return "Spanish";
        if (lowerText.Contains("french") || lowerText.Contains("français") || lowerText.Contains("fr"))
            return "French";
        if (lowerText.Contains("german") || lowerText.Contains("deutsch") || lowerText.Contains("de"))
            return "German";

        // Default: LibriVox is primarily English
        return "English";
    }
}

