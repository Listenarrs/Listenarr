using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Listenarr.Application.Services;

namespace Listenarr.Api.Services
{
    public class AudiobookshelfService : IAudiobookshelfService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfigurationService _configurationService;
        private readonly ILogger<AudiobookshelfService> _logger;

        public AudiobookshelfService(
            IHttpClientFactory httpClientFactory,
            IConfigurationService configurationService,
            ILogger<AudiobookshelfService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _configurationService = configurationService;
            _logger = logger;
        }

        private async Task<(HttpClient? Client, string? Error)> CreateClientAsync(CancellationToken ct)
        {
            var settings = await _configurationService.GetApplicationSettingsAsync();

            if (!settings.AudiobookshelfEnabled)
                return (null, "Audiobookshelf is disabled");

            if (string.IsNullOrWhiteSpace(settings.AudiobookshelfUrl))
                return (null, "Audiobookshelf URL is not set");

            if (string.IsNullOrWhiteSpace(settings.AudiobookshelfApiKey))
                return (null, "Audiobookshelf API key is not set");

            HttpClient client;

            if (!settings.AudiobookshelfVerifySsl)
            {
                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback =
                        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                };

                client = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(30)
                };
            }
            else
            {
                client = _httpClientFactory.CreateClient();
            }

            client.BaseAddress = new Uri(settings.AudiobookshelfUrl.TrimEnd('/') + "/");
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", settings.AudiobookshelfApiKey);

            return (client, null);
        }

        public async Task<(bool Success, string? Message)> TestConnectionAsync(CancellationToken ct = default)
        {
            try
            {
                var (client, error) = await CreateClientAsync(ct);
                if (client == null)
                    return (false, error);

                var response = await client.GetAsync("api/libraries", ct);
                var raw = await response.Content.ReadAsStringAsync(ct);
                
                _logger.LogInformation(
                    "ABS TestConnection status={StatusCode}, body={Body}",
                    response.StatusCode,
                    raw);

                if (!response.IsSuccessStatusCode)
                {
                    return (false, $"Failed: {response.StatusCode} - {raw}");
                }

                return (true, "Connection successful");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Audiobookshelf connection test failed");
                return (false, ex.Message);
            }
        }

        public async Task<IReadOnlyList<AudiobookshelfLibraryDto>> GetLibrariesAsync(CancellationToken ct = default)
        {
            try
            {
                var (client, error) = await CreateClientAsync(ct);
                if (client == null)
                {
                    _logger.LogWarning("ABS GetLibraries failed before request: {Error}", error);
                    return Array.Empty<AudiobookshelfLibraryDto>();
                }

                var httpResponse = await client.GetAsync("api/libraries", ct);
                var raw = await httpResponse.Content.ReadAsStringAsync(ct);

                _logger.LogDebug(
                    "ABS GetLibraries status={StatusCode}, body={Body}",
                    httpResponse.StatusCode,
                    raw);

                if (!httpResponse.IsSuccessStatusCode)
                {
                    return Array.Empty<AudiobookshelfLibraryDto>();
                }

                var response = System.Text.Json.JsonSerializer.Deserialize<AudiobookshelfLibrariesResponse>(
                    raw,
                    new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                var results = new List<AudiobookshelfLibraryDto>();

                if (response?.Libraries != null)
                {
                    foreach (var lib in response.Libraries)
                    {
                        results.Add(new AudiobookshelfLibraryDto
                        {
                            Id = lib.Id ?? "",
                            Name = lib.Name ?? "",
                            MediaType = lib.MediaType ?? "",
                            Path = lib.Folders?.FirstOrDefault()?.FullPath
                        });
                    }
                }

                _logger.LogInformation("ABS parsed {Count} libraries", results.Count);
                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get Audiobookshelf libraries");
                return Array.Empty<AudiobookshelfLibraryDto>();
            }
        }

        public async Task<(bool Success, string? Message)> TriggerLibraryScanAsync(string libraryId, CancellationToken ct = default)
        {
            try
            {
                var (client, error) = await CreateClientAsync(ct);
                if (client == null)
                    return (false, error);

                if (string.IsNullOrWhiteSpace(libraryId))
                    return (false, "LibraryId is required");

                var response = await client.PostAsync($"api/libraries/{libraryId}/scan", null, ct);

                if (!response.IsSuccessStatusCode)
                {
                    return (false, $"Scan failed: {response.StatusCode}");
                }

                return (true, "Scan triggered");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to trigger Audiobookshelf scan");
                return (false, ex.Message);
            }
        }

        public async Task<IReadOnlyList<AudiobookshelfLibraryItemDto>> GetLibraryItemsAsync(
            string libraryId,
            CancellationToken ct = default)
        {
            try
            {
                var (client, error) = await CreateClientAsync(ct);
                if (client == null)
                {
                    _logger.LogWarning("ABS GetLibraryItems failed before request: {Error}", error);
                    return Array.Empty<AudiobookshelfLibraryItemDto>();
                }

                if (string.IsNullOrWhiteSpace(libraryId))
                {
                    _logger.LogWarning("ABS GetLibraryItems failed: libraryId is empty");
                    return Array.Empty<AudiobookshelfLibraryItemDto>();
                }

                // var response = await client.GetFromJsonAsync<AudiobookshelfLibraryItemsResponse>(
                //     $"api/libraries/{libraryId}/items",
                //     ct);
                var httpResponse = await client.GetAsync($"api/libraries/{libraryId}/items", ct);
                var raw = await httpResponse.Content.ReadAsStringAsync(ct);

                await File.WriteAllTextAsync("/tmp/abs_items.json", raw);

                if (!httpResponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "ABS GetLibraryItems returned non-success status {StatusCode}",
                        httpResponse.StatusCode);

                    return Array.Empty<AudiobookshelfLibraryItemDto>();
                }

                var response = JsonSerializer.Deserialize<AudiobookshelfLibraryItemsResponse>(
                    raw,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                var results = new List<AudiobookshelfLibraryItemDto>();

                if (response?.Results != null)
                {
                    foreach (var item in response.Results)
                    {
                        var rawSeries =
                                    item.Media?.Metadata?.Series?.FirstOrDefault()?.Name
                                    ?? item.Media?.Metadata?.SeriesName;
                        results.Add(new AudiobookshelfLibraryItemDto
                        {
                            Id = item.Id ?? string.Empty,
                            Path = item.Path ?? string.Empty,
                            MediaType = item.MediaType ?? string.Empty,
                            IsFile = item.IsFile ?? false,
                            Size = item.Size,
                            Metadata = new AudiobookshelfBookMetadataDto
                            {
                                Title = item.Media?.Metadata?.Title,
                                Subtitle = item.Media?.Metadata?.Subtitle,
                                Authors =
                                    item.Media?.Metadata?.Authors?
                                        .Where(a => !string.IsNullOrWhiteSpace(a.Name))
                                        .Select(a => a.Name!)
                                        .ToList()
                                    ?? (!string.IsNullOrWhiteSpace(item.Media?.Metadata?.AuthorName)
                                        ? new List<string> { item.Media.Metadata.AuthorName }
                                        : new List<string>()),
                                Narrators =
                                    item.Media?.Metadata?.Narrators
                                    ?? (!string.IsNullOrWhiteSpace(item.Media?.Metadata?.NarratorName)
                                        ? item.Media.Metadata.NarratorName
                                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                            .ToList()
                                        : new List<string>()),
                                

                                Series = CleanSeriesName(rawSeries),
                                SeriesNumber = ExtractSeriesNumber(rawSeries),
                                Publisher = item.Media?.Metadata?.Publisher,
                                Language = item.Media?.Metadata?.Language,
                                Asin = item.Media?.Metadata?.Asin,
                                Isbn = NormalizeIsbn(item.Media?.Metadata?.Isbn),
                                PublishedYear = item.Media?.Metadata?.PublishedYear,
                                PublishedDate = item.Media?.Metadata?.PublishedDate,
                                Description = item.Media?.Metadata?.Description,
                                ImageUrl = null, //BuildAudiobookshelfCoverUrl(item.Media?.CoverPath),
                                Genres = item.Media?.Metadata?.Genres ?? new List<string>(),
                                Tags = item.Media?.Tags ?? new List<string>(),
                                Runtime = item.Media?.Duration != null
                                    ? (int?)Math.Round(item.Media.Duration.Value)
                                    : null,
                                Explicit = item.Media?.Metadata?.Explicit ?? false,
                                Abridged = item.Media?.Metadata?.Abridged ?? false
                            }
                        });
                    }
                }

                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get Audiobookshelf library items");
                return Array.Empty<AudiobookshelfLibraryItemDto>();
            }
        }


        private static string? CleanSeriesName(string? seriesName)
        {
            if (string.IsNullOrWhiteSpace(seriesName))
                return null;

            // Remove patterns like:
            // "Series #4"
            // "Series #4.5"
            var cleaned = Regex.Replace(seriesName, @"\s*#\s*\d+(\.\d+)?", "");

            return cleaned.Trim();
        }

        private static string? ExtractSeriesNumber(string? seriesName)
        {
            if (string.IsNullOrWhiteSpace(seriesName))
                return null;

            var match = System.Text.RegularExpressions.Regex.Match(seriesName, @"#\s*(\d+(\.\d+)?)");
            return match.Success ? match.Groups[1].Value : null;
        }

        private static List<string> NormalizeIsbn(object? isbnValue)
        {
            if (isbnValue == null)
                return new List<string>();

            // case 1: already a list
            if (isbnValue is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Array)
                {
                    return element.EnumerateArray()
                        .Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => x.GetString()!)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToList();
                }

                // case 2: single string
                if (element.ValueKind == JsonValueKind.String)
                {
                    var value = element.GetString();
                    return string.IsNullOrWhiteSpace(value)
                        ? new List<string>()
                        : new List<string> { value };
                }
            }

            // fallback
            return new List<string>();
        }

        private class AudiobookshelfLibraryItemsResponse
        {
            public List<AudiobookshelfLibraryItemResponse>? Results { get; set; }
        }

        private class AudiobookshelfLibraryItemResponse
        {
            public string? Id { get; set; }
            public string? Path { get; set; }
            public string? MediaType { get; set; }
            public bool? IsFile { get; set; }
            public long? Size { get; set; }
            public AudiobookshelfMediaResponse? Media { get; set; }
        }

        private class AudiobookshelfMediaResponse
        {
            public string? CoverPath { get; set; }
            public List<string>? Tags { get; set; }
            public double? Duration { get; set; }
            public AudiobookshelfMetadataResponse? Metadata { get; set; }
        }

        private class AudiobookshelfMetadataResponse
        {
            public string? Title { get; set; }
            public string? Subtitle { get; set; }

            public string? AuthorName { get; set; }
            public string? NarratorName { get; set; }
            public string? SeriesName { get; set; }

            public List<string>? Genres { get; set; }

            public string? PublishedYear { get; set; }
            public string? PublishedDate { get; set; }
            public string? Publisher { get; set; }
            public string? Description { get; set; }
            public object? Isbn { get; set; }
            public string? Asin { get; set; }
            public string? Language { get; set; }
            public bool? Explicit { get; set; }
            public bool? Abridged { get; set; }

            public List<AudiobookshelfAuthorResponse>? Authors { get; set; }
            public List<string>? Narrators { get; set; }
            public List<AudiobookshelfSeriesResponse>? Series { get; set; }
        }

        private class AudiobookshelfAuthorResponse
        {
            public string? Name { get; set; }
        }

        private class AudiobookshelfSeriesResponse
        {
            public string? Name { get; set; }
        }

        // Internal response models (based on ABS API shape)
        private class AudiobookshelfLibrariesResponse
        {
            public List<Library>? Libraries { get; set; }
        }

        private class Library
        {
            public string? Id { get; set; }
            public string? Name { get; set; }
            public string? MediaType { get; set; }
            public List<LibraryFolder>? Folders { get; set; }
        }

        private class LibraryFolder
        {
            public string? FullPath { get; set; }
        }
    }
}