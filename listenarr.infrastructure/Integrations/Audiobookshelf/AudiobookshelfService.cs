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

using System.Net.Http.Headers;
using System.Text.Json;
using Listenarr.Application.Integrations.Audiobookshelf.Contracts;
using Listenarr.Application.Integrations.Audiobookshelf.Models;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Integrations.Audiobookshelf;

/// <summary>
/// Client for the Audiobookshelf server API (https://api.audiobookshelf.org/).
/// Used to verify connectivity, enumerate libraries, and request library scans so
/// Audiobookshelf picks up files imported by Listenarr without a manual scan.
/// </summary>
public class AudiobookshelfService : IAudiobookshelfService
{
    private readonly HttpClient _httpClientNoRedirect;
    private readonly IConfigurationService _configurationService;
    private readonly ILogger<AudiobookshelfService> _logger;

    public AudiobookshelfService(
        HttpClient httpClient,
        IConfigurationService configurationService,
        ILogger<AudiobookshelfService> logger)
    {
        _httpClientNoRedirect = httpClient;
        _configurationService = configurationService;
        _logger = logger;
    }

    public async Task<AudiobookshelfActionResult> TestConnectionAsync(string? url, string? apiKey, CancellationToken cancellationToken = default)
    {
        var saved = await _configurationService.GetAudiobookshelfSettingsAsync(includeSecret: true);
        var effectiveUrl = string.IsNullOrWhiteSpace(url) ? saved.Url : url.Trim();
        var effectiveApiKey = string.IsNullOrWhiteSpace(apiKey) || string.Equals(apiKey, ApiResponseRedactor.RedactedValue, StringComparison.Ordinal)
            ? saved.ApiKey
            : apiKey.Trim();

        return await FetchLibrariesAsync(effectiveUrl, effectiveApiKey, cancellationToken);
    }

    public async Task<AudiobookshelfActionResult> GetLibrariesAsync(CancellationToken cancellationToken = default)
    {
        var saved = await _configurationService.GetAudiobookshelfSettingsAsync(includeSecret: true);
        return await FetchLibrariesAsync(saved.Url, saved.ApiKey, cancellationToken);
    }

    public async Task<AudiobookshelfActionResult> TriggerScanAsync(CancellationToken cancellationToken = default)
    {
        var saved = await _configurationService.GetAudiobookshelfSettingsAsync(includeSecret: true);
        var librariesResult = await FetchLibrariesAsync(saved.Url, saved.ApiKey, cancellationToken);
        if (!librariesResult.Success || librariesResult.Libraries == null)
        {
            return librariesResult;
        }

        var targets = string.IsNullOrWhiteSpace(saved.LibraryId)
            ? librariesResult.Libraries.Where(l => string.Equals(l.MediaType, "book", StringComparison.OrdinalIgnoreCase)).ToList()
            : librariesResult.Libraries.Where(l => string.Equals(l.Id, saved.LibraryId.Trim(), StringComparison.Ordinal)).ToList();

        if (targets.Count == 0)
        {
            return new AudiobookshelfActionResult(false, string.IsNullOrWhiteSpace(saved.LibraryId)
                ? "No book libraries found on the Audiobookshelf server"
                : "The configured Audiobookshelf library no longer exists");
        }

        var baseUrl = NormalizeBaseUrl(saved.Url);
        var scanned = new List<string>();
        var failed = new List<string>();

        foreach (var library in targets)
        {
            var scanUrl = $"{baseUrl}/api/libraries/{Uri.EscapeDataString(library.Id)}/scan";
            try
            {
                using var response = await SendAsync(HttpMethod.Post, scanUrl, saved.ApiKey!, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    scanned.Add(library.Name);
                }
                else
                {
                    _logger.LogWarning(
                        "Audiobookshelf scan request for library {LibraryName} returned {StatusCode}",
                        library.Name,
                        (int)response.StatusCode);
                    failed.Add($"{library.Name} (HTTP {(int)response.StatusCode})");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "Failed to request Audiobookshelf scan for library {LibraryName}", library.Name);
                failed.Add(library.Name);
            }
        }

        if (scanned.Count == 0)
        {
            return new AudiobookshelfActionResult(false, $"Audiobookshelf scan failed for {string.Join(", ", failed)}");
        }

        var message = failed.Count == 0
            ? $"Audiobookshelf scan started for {string.Join(", ", scanned)}"
            : $"Audiobookshelf scan started for {string.Join(", ", scanned)}; failed for {string.Join(", ", failed)}";
        _logger.LogInformation("{Message}", message);
        return new AudiobookshelfActionResult(true, message, librariesResult.Libraries);
    }

    private async Task<AudiobookshelfActionResult> FetchLibrariesAsync(string? url, string? apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return new AudiobookshelfActionResult(false, "Audiobookshelf URL is not configured");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new AudiobookshelfActionResult(false, "Audiobookshelf API token is not configured");
        }

        var baseUrl = NormalizeBaseUrl(url);
        if (!OutboundRequestSecurity.TryValidateExternalHttpUrl(baseUrl, out var blockedReason, allowPrivateTargets: true))
        {
            return new AudiobookshelfActionResult(false, $"Blocked Audiobookshelf target: {blockedReason}");
        }

        try
        {
            using var response = await SendAsync(HttpMethod.Get, $"{baseUrl}/api/libraries", apiKey, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                return new AudiobookshelfActionResult(false, "Audiobookshelf rejected the API token");
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Audiobookshelf API at {Url} returned {StatusCode}",
                    LogRedaction.SanitizeUrl(baseUrl),
                    (int)response.StatusCode);
                return new AudiobookshelfActionResult(false, $"Audiobookshelf API error (HTTP {(int)response.StatusCode})");
            }

            var libraries = ParseLibraries(body);
            if (libraries == null)
            {
                return new AudiobookshelfActionResult(false, "Unexpected response from Audiobookshelf; is the URL an Audiobookshelf server?");
            }

            return new AudiobookshelfActionResult(true, $"Connected; found {libraries.Count} libraries", libraries);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException or JsonException)
        {
            _logger.LogWarning(ex, "Failed to reach Audiobookshelf at {Url}", LogRedaction.SanitizeUrl(baseUrl));
            return new AudiobookshelfActionResult(false, $"Failed to reach Audiobookshelf: {ex.Message}");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string apiKey, CancellationToken cancellationToken)
    {
        var (response, _) = await OutboundRequestSecurity.SendWithValidatedRedirectsAsync(
            currentUri =>
            {
                var request = new HttpRequestMessage(method, currentUri);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                return request;
            },
            new Uri(url),
            _httpClientNoRedirect,
            _logger,
            allowPrivateTargets: true,
            cancellationToken: cancellationToken);
        return response;
    }

    private static List<AudiobookshelfLibrary>? ParseLibraries(string payload)
    {
        using var doc = JsonDocument.Parse(payload);

        // Current servers return { "libraries": [...] }; very old ones returned a bare array.
        var root = doc.RootElement;
        JsonElement array;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("libraries", out var librariesProp) && librariesProp.ValueKind == JsonValueKind.Array)
        {
            array = librariesProp;
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            array = root;
        }
        else
        {
            return null;
        }

        var result = new List<AudiobookshelfLibrary>();
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty("id", out var idProp)
                || idProp.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = element.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                ? nameProp.GetString() ?? string.Empty
                : string.Empty;
            var mediaType = element.TryGetProperty("mediaType", out var mediaTypeProp) && mediaTypeProp.ValueKind == JsonValueKind.String
                ? mediaTypeProp.GetString() ?? string.Empty
                : string.Empty;

            result.Add(new AudiobookshelfLibrary(idProp.GetString()!, name, mediaType));
        }

        return result;
    }

    private static string NormalizeBaseUrl(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = $"http://{trimmed}";
        }

        return trimmed;
    }
}
