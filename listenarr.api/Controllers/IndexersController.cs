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

using Listenarr.Api.Attributes;
using Listenarr.Api.Dtos;
using Listenarr.Application.Common;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Application.Security;
using Listenarr.Domain.Models;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Listenarr.Api.Controllers
{
    [ApiController]
    [Route("api/v{version:apiVersion}/indexers")]
    [Tags("Indexers")]
    public class IndexersController : ControllerBase
    {
        private readonly IIndexerRepository _indexerRepository;
        private readonly ILogger<IndexersController> _logger;
        private readonly HttpClient _httpClient;
        private readonly HttpClient _httpClientNoRedirect;
        private readonly IConfigurationService _configurationService;

        public IndexersController(IIndexerRepository indexerRepository, ILogger<IndexersController> logger, HttpClient httpClient, IConfigurationService configurationService)
        {
            _indexerRepository = indexerRepository;
            _logger = logger;
            _httpClient = httpClient;
            _httpClientNoRedirect = httpClient;
            _configurationService = configurationService;
        }

        private bool ShouldRedactIndexerSecretsForCaller()
            => SecurityRequestUtils.ShouldRedactSecretsForCaller(HttpContext);

        private Indexer RedactIndexerForCaller(Indexer indexer)
            => ShouldRedactIndexerSecretsForCaller() ? ApiResponseRedactor.RedactIndexer(indexer) : indexer;

        private List<Indexer> RedactIndexersForCaller(IEnumerable<Indexer> indexers)
            => ShouldRedactIndexerSecretsForCaller()
                ? indexers.Select(ApiResponseRedactor.RedactIndexer).ToList()
                : indexers.ToList();

        private string? RedactMamIdForCaller(string? mamId)
            => ShouldRedactIndexerSecretsForCaller() && !string.IsNullOrWhiteSpace(mamId)
                ? ApiResponseRedactor.RedactedValue
                : mamId;

        private Task<string?> ValidateOutboundUrlForCallerAsync(string url)
        {
            // *Arr standard behavior: allow private/loopback destinations for indexer connectivity
            // tests/imports, but still enforce absolute HTTP(S) URLs and block embedded credentials.
            if (!OutboundRequestSecurity.TryValidateExternalHttpUrl(url, out var reason, allowPrivateTargets: true))
            {
                return Task.FromResult<string?>(reason);
            }

            return Task.FromResult<string?>(null);
        }

        private async Task<HttpResponseMessage> SendValidatedAsync(
            Func<Uri, HttpRequestMessage> requestFactory,
            string url,
            HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
            CancellationToken cancellationToken = default)
        {
            var uri = new Uri(url);
            var (response, _) = await OutboundRequestSecurity.SendWithValidatedRedirectsAsync(
                requestFactory,
                uri,
                _httpClientNoRedirect,
                _logger,
                // *Arr standard behavior for indexers: allow private/loopback destinations.
                allowPrivateTargets: true,
                completionOption: completionOption,
                cancellationToken: cancellationToken);
            return response;
        }

        private async Task SaveTestResultAsync(Indexer indexer, bool persist, bool success, string? error)
        {
            // Update the passed indexer instance
            indexer.LastTestedAt = DateTime.UtcNow;
            indexer.LastTestSuccessful = success;
            indexer.LastTestError = error;

            if (persist && indexer.Id != 0)
            {
                // Persist test result back to the database for the stored indexer
                var existing = await _indexerRepository.GetByIdAsync(indexer.Id);
                if (existing != null)
                {
                    existing.LastTestedAt = indexer.LastTestedAt;
                    existing.LastTestSuccessful = success;
                    existing.LastTestError = error;
                    existing.UpdatedAt = DateTime.UtcNow;
                    await _indexerRepository.UpdateAsync(existing);
                }
            }
        }

        private async Task<IActionResult> ExecuteIndexerTestAsync(Indexer indexer, bool persist)
        {
            // Normalize URL first
            indexer.Url = NormalizeIndexerUrl(indexer.Url);

            var impl = (indexer.Implementation ?? string.Empty).Trim().ToLowerInvariant();

            try
            {
                _logger.LogInformation("[IndexerTest] Testing indexer {Name} (impl={Impl}, url={Url})", LogRedaction.SanitizeText(indexer.Name), LogRedaction.SanitizeText(indexer.Implementation), LogRedaction.SanitizeUrl(indexer.Url));
                return impl switch
                {
                    var s when s == "internetarchive" || s == "internet archive" => await TestInternetArchive(indexer, persist),
                    var s when s == "myanonamouse" => await TestMyAnonamouse(indexer, persist),
                    // For Newznab/Torznab/Custom fall back to a generic connectivity check
                    _ => await TestGenericIndexer(indexer, persist)
                };
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Indexer '{Name}' test failed", LogRedaction.SanitizeText(indexer.Name));
                return await BuildIndexerTestBadRequestAsync(indexer, persist, "Indexer test failed", ex);
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogWarning(ex, "Indexer '{Name}' test failed", LogRedaction.SanitizeText(indexer.Name));
                return await BuildIndexerTestBadRequestAsync(indexer, persist, "Indexer test failed", ex);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Indexer '{Name}' test failed", LogRedaction.SanitizeText(indexer.Name));
                return await BuildIndexerTestBadRequestAsync(indexer, persist, "Indexer test failed", ex);
            }
            catch (UriFormatException ex)
            {
                _logger.LogWarning(ex, "Indexer '{Name}' test failed", LogRedaction.SanitizeText(indexer.Name));
                return await BuildIndexerTestBadRequestAsync(indexer, persist, "Indexer test failed", ex);
            }
            catch (System.Net.CookieException ex)
            {
                _logger.LogWarning(ex, "Indexer '{Name}' test failed", LogRedaction.SanitizeText(indexer.Name));
                return await BuildIndexerTestBadRequestAsync(indexer, persist, "Indexer test failed", ex);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Indexer '{Name}' test failed", LogRedaction.SanitizeText(indexer.Name));
                return await BuildIndexerTestBadRequestAsync(indexer, persist, "Indexer test failed", ex);
            }
        }

        private async Task<IActionResult> BuildIndexerTestBadRequestAsync(Indexer indexer, bool persist, string message, Exception ex)
        {
            await SaveTestResultAsync(indexer, persist, false, ex.Message);
            return BadRequest(new
            {
                success = false,
                message,
                error = ex.Message,
                indexer = RedactIndexerForCaller(indexer)
            });
        }

        private async Task<IActionResult> TestGenericIndexer(Indexer indexer, bool persist)
        {
            // Minimal connectivity check: attempt to hit base URL or indexer 'api' endpoint
            try
            {
                var target = indexer.Url?.TrimEnd('/') ?? string.Empty;
                // Prefer /api endpoint if present, otherwise base URL
                var testUrl = target.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? target : target + "/api";

                // If this is a Newznab/Torznab style indexer, append the apikey query parameter and add capabilities query to test auth
                var implName = (indexer.Implementation ?? string.Empty).Trim().ToLowerInvariant();
                var isNewznabStyle = implName == "newznab" || implName == "torznab";

                if (isNewznabStyle)
                {
                    // Newznab/Torznab indexers REQUIRE an API key for authentication
                    if (string.IsNullOrWhiteSpace(indexer.ApiKey))
                    {
                        await SaveTestResultAsync(indexer, persist, false, "API key is required for Newznab/Torznab indexers");
                        return BadRequest(new { success = false, message = "API key is required for Newznab/Torznab indexers", indexer = RedactIndexerForCaller(indexer) });
                    }

                    // Use search endpoint (t=search) instead of capabilities (t=caps) because
                    // many indexers expose t=caps publicly without authentication.
                    // t=search reliably enforces authentication.
                    var separator = testUrl.Contains('?') ? '&' : '?';
                    testUrl = testUrl + separator + "t=search&limit=1&offset=0";
                    testUrl = testUrl + "&apikey=" + System.Net.WebUtility.UrlEncode(indexer.ApiKey);
                }

                // Ensure User-Agent is present even if the injected HttpClient was created without defaults
                var version = typeof(IndexersController).Assembly.GetName().Version?.ToString() ?? "0.0.0";
                var userAgent = $"Listenarr/{version} (+https://github.com/listenarrs/listenarr)";

                _logger.LogInformation("[IndexerTest] GET {Url} UA={UserAgent}", LogRedaction.SanitizeUrl(testUrl), LogRedaction.SanitizeText(userAgent));

                var blockedReason = await ValidateOutboundUrlForCallerAsync(testUrl);
                if (!string.IsNullOrWhiteSpace(blockedReason))
                {
                    await SaveTestResultAsync(indexer, persist, false, $"Blocked outbound target: {blockedReason}");
                    return BadRequest(new { success = false, message = $"Blocked outbound target: {blockedReason}", indexer = RedactIndexerForCaller(indexer) });
                }

                using var response = await SendValidatedAsync(currentUri =>
                {
                    var retryRequest = new HttpRequestMessage(HttpMethod.Get, currentUri);
                    retryRequest.Headers.UserAgent.ParseAdd(userAgent);
                    if (!string.IsNullOrEmpty(indexer.ApiKey))
                    {
                        retryRequest.Headers.Add("X-Api-Key", indexer.ApiKey);
                    }
                    return retryRequest;
                }, testUrl);

                _logger.LogInformation("[IndexerTest] {Name} responded {StatusCode}", LogRedaction.SanitizeText(indexer.Name), (int)response.StatusCode);

                // Check for HTTP-level authentication failures
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                    response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    await SaveTestResultAsync(indexer, persist, false, $"Authentication failed: HTTP {(int)response.StatusCode}");
                    return BadRequest(new { success = false, message = "Authentication failed", status = (int)response.StatusCode, indexer = RedactIndexerForCaller(indexer) });
                }

                if (!response.IsSuccessStatusCode)
                {
                    await SaveTestResultAsync(indexer, persist, false, $"HTTP {(int)response.StatusCode}");
                    return BadRequest(new { success = false, message = "Generic indexer test failed", status = (int)response.StatusCode, indexer = RedactIndexerForCaller(indexer) });
                }

                // For Newznab/Torznab, parse XML response to check for error elements
                if (isNewznabStyle)
                {
                    var xmlContent = await response.Content.ReadAsStringAsync();
                    var errorMessage = ParseNewznabError(xmlContent);

                    if (errorMessage != null)
                    {
                        var isAuthError = errorMessage.Contains("api", StringComparison.OrdinalIgnoreCase) ||
                                         errorMessage.Contains("key", StringComparison.OrdinalIgnoreCase) ||
                                         errorMessage.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
                                         errorMessage.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                                         errorMessage.Contains("authentication", StringComparison.OrdinalIgnoreCase);

                        var failureMessage = isAuthError ? $"Authentication failed: {errorMessage}" : errorMessage;
                        await SaveTestResultAsync(indexer, persist, false, failureMessage);
                        return BadRequest(new { success = false, message = failureMessage, indexer = RedactIndexerForCaller(indexer) });
                    }
                }

                await SaveTestResultAsync(indexer, persist, true, null);
                return Ok(new { success = true, message = "Indexer authentication successful", indexer = RedactIndexerForCaller(indexer) });
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Generic indexer test failed for {Name}", LogRedaction.SanitizeText(indexer.Name));
                return await BuildIndexerTestBadRequestAsync(indexer, persist, "Indexer test failed", ex);
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogWarning(ex, "Generic indexer test failed for {Name}", LogRedaction.SanitizeText(indexer.Name));
                return await BuildIndexerTestBadRequestAsync(indexer, persist, "Indexer test failed", ex);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Generic indexer test failed for {Name}", LogRedaction.SanitizeText(indexer.Name));
                return await BuildIndexerTestBadRequestAsync(indexer, persist, "Indexer test failed", ex);
            }
            catch (UriFormatException ex)
            {
                _logger.LogWarning(ex, "Generic indexer test failed for {Name}", LogRedaction.SanitizeText(indexer.Name));
                return await BuildIndexerTestBadRequestAsync(indexer, persist, "Indexer test failed", ex);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Generic indexer test failed for {Name}", LogRedaction.SanitizeText(indexer.Name));
                return await BuildIndexerTestBadRequestAsync(indexer, persist, "Indexer test failed", ex);
            }
        }

        private string? ParseNewznabError(string xmlContent)
        {
            try
            {
                // Parse XML response to check for error element
                // Newznab spec: <error code="XXX" description="..." />
                var settings = new System.Xml.XmlReaderSettings
                {
                    DtdProcessing = System.Xml.DtdProcessing.Ignore,
                    XmlResolver = null
                };

                using var reader = System.Xml.XmlReader.Create(new System.IO.StringReader(xmlContent), settings);
                var doc = System.Xml.Linq.XDocument.Load(reader);

                // Check for error element (can be at root, under rss, or as a descendant)
                System.Xml.Linq.XElement? errorElement = null;

                // Case 1: Root element is <error>, Case 2: Error is a child or descendant
                errorElement = doc.Root?.Name.LocalName.Equals("error", StringComparison.OrdinalIgnoreCase) == true
                    ? doc.Root
                    : doc.Root?.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("error", StringComparison.OrdinalIgnoreCase));

                if (errorElement != null)
                {
                    var code = errorElement.Attribute("code")?.Value;
                    var description = errorElement.Attribute("description")?.Value ?? errorElement.Value;
                    return string.IsNullOrEmpty(description) ? $"Error code: {code}" : description;
                }

                return null;
            }
            catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException)
            {
                // If we can't parse the XML, assume no error element
                return null;
            }
        }

        /// <summary>
        /// Get all configured indexers.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var indexers = (await _indexerRepository.GetAllAsync())
                .OrderBy(i => i.Priority)
                .ThenBy(i => i.Name)
                .ToList();

            return Ok(RedactIndexersForCaller(indexers));
        }

        /// <summary>
        /// Get an indexer by its database ID.
        /// </summary>
        /// <param name="id">Indexer ID.</param>
        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(int id)
        {
            var indexer = await _indexerRepository.GetByIdAsync(id);
            if (indexer == null)
            {
                return NotFound(new { message = "Indexer not found" });
            }

            return Ok(RedactIndexerForCaller(indexer));
        }

        /// <summary>
        /// Create a new indexer.
        /// </summary>
        /// <param name="indexer">Indexer configuration to create.</param>
        [HttpPost]
        public async Task<IActionResult> Create([FromBody] Indexer indexer)
        {
            indexer.CreatedAt = DateTime.UtcNow;
            indexer.UpdatedAt = DateTime.UtcNow;

            indexer = await _indexerRepository.AddAsync(indexer);

            _logger.LogInformation("Created indexer '{Name}' (ID: {Id}, Type: {Type})",
                indexer.Name, indexer.Id, indexer.Type);

            return CreatedAtAction(nameof(GetById), new { id = indexer.Id }, RedactIndexerForCaller(indexer));
        }

        /// <summary>
        /// Import indexers from a Prowlarr instance.
        /// By default this imports audiobook-related indexers (category 3000/3030),
        /// but a configured tag filter overrides that selection.
        /// </summary>
        /// <param name="request">Prowlarr server URL and API key.</param>
        [HttpPost("prowlarr/import")]
        public async Task<IActionResult> ImportFromProwlarr([FromBody] ProwlarrImportRequestDto request)
        {
            if (request == null)
            {
                return BadRequest(new { message = "Request body is required" });
            }

            var savedConnection = await _configurationService.GetProwlarrImportSettingsAsync(includeSecret: true);
            var effectiveUrl = string.IsNullOrWhiteSpace(request.Url) ? savedConnection.Url : request.Url.Trim();
            var effectivePort = request.ClearPort ? null : request.Port ?? savedConnection.Port;
            var effectiveApiKey = string.IsNullOrWhiteSpace(request.ApiKey) ? savedConnection.ApiKey : request.ApiKey.Trim();
            var effectiveTagFilter = request.TagFilter == null
                ? savedConnection.TagFilter?.Trim()
                : request.TagFilter.Trim();

            if (string.IsNullOrWhiteSpace(effectiveUrl))
            {
                return BadRequest(new { message = "Prowlarr URL is required" });
            }

            if (string.IsNullOrWhiteSpace(effectiveApiKey))
            {
                return BadRequest(new { message = "Prowlarr API key is required" });
            }

            var baseUrl = BuildProwlarrBaseUrl(effectiveUrl, effectivePort);
            var blockedBaseUrlReason = await ValidateOutboundUrlForCallerAsync(baseUrl);
            if (!string.IsNullOrWhiteSpace(blockedBaseUrlReason))
            {
                return BadRequest(new { message = $"Blocked Prowlarr target: {blockedBaseUrlReason}" });
            }

            HttpResponseMessage response;
            string payload;
            try
            {
                (response, payload) = await FetchProwlarrIndexersAsync(baseUrl, effectiveApiKey.Trim());
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Failed to reach Prowlarr at {Url}", LogRedaction.SanitizeUrl(baseUrl));
                return StatusCode(502, new { message = "Failed to reach Prowlarr API" });
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogWarning(ex, "Failed to reach Prowlarr at {Url}", LogRedaction.SanitizeUrl(baseUrl));
                return StatusCode(502, new { message = "Failed to reach Prowlarr API" });
            }
            catch (UriFormatException ex)
            {
                _logger.LogWarning(ex, "Failed to reach Prowlarr at {Url}", LogRedaction.SanitizeUrl(baseUrl));
                return StatusCode(502, new { message = "Failed to reach Prowlarr API" });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Failed to reach Prowlarr at {Url}", LogRedaction.SanitizeUrl(baseUrl));
                return StatusCode(502, new { message = "Failed to reach Prowlarr API" });
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Prowlarr API returned {StatusCode}: {Body}", (int)response.StatusCode, LogRedaction.SanitizeText(payload));
                    return StatusCode((int)response.StatusCode, new { message = "Prowlarr API error", status = (int)response.StatusCode });
                }
            }
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return StatusCode(502, new { message = "Unexpected Prowlarr API response" });
            }

            await _configurationService.SaveProwlarrImportSettingsAsync(new ProwlarrImportConnectionSettings
            {
                Url = effectiveUrl,
                Port = effectivePort,
                ApiKey = string.IsNullOrWhiteSpace(request.ApiKey) ? null : request.ApiKey.Trim(),
                TagFilter = effectiveTagFilter,
            });

            var existingIndexers = await _indexerRepository.GetAllAsync();
            var createdIndexers = new List<Indexer>();
            var skipped = 0;
            Dictionary<string, string>? tagMap = null;

            if (!string.IsNullOrWhiteSpace(effectiveTagFilter))
            {
                tagMap = await TryFetchProwlarrTagMapAsync(baseUrl, effectiveApiKey.Trim());
                if ((tagMap == null || tagMap.Count == 0) && PayloadRequiresProwlarrTagMap(doc.RootElement))
                {
                    _logger.LogWarning(
                        "Prowlarr tag-filtered import for {Url} requires tag label lookup, but tags could not be loaded",
                        LogRedaction.SanitizeUrl(baseUrl));
                    return StatusCode(502, new { message = "Failed to load Prowlarr tags required for tag-filtered import" });
                }
            }

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.Number)
                {
                    skipped++;
                    continue;
                }

                var indexerId = idProp.GetInt32();
                var categoryIds = GetCategoryIdsFromProwlarrIndexer(element);
                var prowlarrTags = GetProwlarrTagValues(element, tagMap);
                var matchesImportFilter = string.IsNullOrWhiteSpace(effectiveTagFilter)
                    ? categoryIds.Contains(3000) || categoryIds.Contains(3030)
                    : prowlarrTags.Any(tag => string.Equals(tag, effectiveTagFilter, StringComparison.OrdinalIgnoreCase));

                if (!matchesImportFilter)
                {
                    skipped++;
                    continue;
                }

                var name = element.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                    ? nameProp.GetString() ?? "Prowlarr Indexer"
                    : "Prowlarr Indexer";
                if (!name.EndsWith(" (Prowlarr)", StringComparison.OrdinalIgnoreCase))
                {
                    name = $"{name} (Prowlarr)";
                }

                var protocol = element.TryGetProperty("protocol", out var protocolProp) && protocolProp.ValueKind == JsonValueKind.String
                    ? protocolProp.GetString() ?? string.Empty
                    : string.Empty;

                var implementation = protocol.Equals("usenet", StringComparison.OrdinalIgnoreCase) ? "Newznab" : "Torznab";

                var proxyUrl = BuildProwlarrProxyUrl(baseUrl, indexerId);
                var normalizedUrl = NormalizeProwlarrProxyUrl(proxyUrl);

                var exists = existingIndexers.FirstOrDefault(i =>
                    NormalizeProwlarrProxyUrl(i.Url) == normalizedUrl &&
                    string.Equals(i.Implementation, implementation, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(i.ApiKey ?? string.Empty, effectiveApiKey ?? string.Empty, StringComparison.Ordinal));

                if (exists != null)
                {
                    skipped++;
                    continue;
                }

                var type = protocol.Equals("usenet", StringComparison.OrdinalIgnoreCase) ? "Usenet" : "Torrent";
                var categories = string.Join(',', categoryIds.Where(c => c == 3000 || c == 3030).OrderBy(c => c));

                var isEnabled = true;
                if (element.TryGetProperty("enable", out var enableProp))
                {
                    isEnabled = enableProp.ValueKind == JsonValueKind.True;
                }
                else if (element.TryGetProperty("enabled", out var enabledProp))
                {
                    isEnabled = enabledProp.ValueKind == JsonValueKind.True;
                }

                var indexer = new Indexer
                {
                    Name = name,
                    Type = type,
                    Implementation = implementation,
                    Url = normalizedUrl,
                    ApiKey = string.IsNullOrWhiteSpace(effectiveApiKey) ? null : effectiveApiKey.Trim(),
                    Categories = categories,
                    EnableRss = true,
                    EnableAutomaticSearch = true,
                    EnableInteractiveSearch = true,
                    IsEnabled = isEnabled,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                createdIndexers.Add(await _indexerRepository.AddAsync(indexer));
            }

            return Ok(new
            {
                addedCount = createdIndexers.Count,
                skippedCount = skipped,
                total = createdIndexers.Count + skipped,
                indexers = createdIndexers.Select(i => new { id = i.Id, name = i.Name, url = i.Url, implementation = i.Implementation })
            });
        }

        /// <summary>
        /// Update an existing indexer.
        /// </summary>
        /// <param name="id">Indexer ID.</param>
        /// <param name="indexer">Updated indexer configuration.</param>
        [HttpPut("{id}")]
        public async Task<IActionResult> Update(int id, [FromBody] Indexer indexer)
        {
            var existing = await _indexerRepository.GetByIdAsync(id);
            if (existing == null)
            {
                return NotFound(new { message = "Indexer not found" });
            }

            // Update properties
            existing.Name = indexer.Name;
            existing.Type = indexer.Type;
            existing.Implementation = indexer.Implementation;
            existing.Url = indexer.Url;
            existing.ApiKey = indexer.ApiKey == ApiResponseRedactor.RedactedValue ? existing.ApiKey : indexer.ApiKey;
            existing.Categories = indexer.Categories;
            existing.AnimeCategories = indexer.AnimeCategories;
            existing.Tags = indexer.Tags;
            existing.EnableRss = indexer.EnableRss;
            existing.EnableAutomaticSearch = indexer.EnableAutomaticSearch;
            existing.EnableInteractiveSearch = indexer.EnableInteractiveSearch;
            existing.EnableAnimeStandardSearch = indexer.EnableAnimeStandardSearch;
            existing.IsEnabled = indexer.IsEnabled;
            existing.Priority = indexer.Priority;
            existing.MinimumAge = indexer.MinimumAge;
            existing.Retention = indexer.Retention;
            existing.MaximumSize = indexer.MaximumSize;
            existing.AdditionalSettings = ApiResponseRedactor.MergeAdditionalSettings(existing.AdditionalSettings, indexer.AdditionalSettings);
            existing.UpdatedAt = DateTime.UtcNow;

            await _indexerRepository.UpdateAsync(existing);

            _logger.LogInformation("Updated indexer '{Name}' (ID: {Id})", existing.Name, existing.Id);

            return Ok(RedactIndexerForCaller(existing));
        }

        /// <summary>
        /// Delete an indexer.
        /// </summary>
        /// <param name="id">Indexer ID.</param>
        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            var indexer = await _indexerRepository.GetByIdAsync(id);
            if (indexer == null)
            {
                return NotFound(new { message = "Indexer not found" });
            }

            await _indexerRepository.DeleteAsync(id);

            _logger.LogInformation("Deleted indexer '{Name}' (ID: {Id})", indexer.Name, indexer.Id);

            return Ok(new { message = "Indexer deleted successfully", id });
        }

        /// <summary>
        /// Test an indexer's connection to verify it is reachable and properly configured.
        /// </summary>
        /// <param name="id">Indexer ID.</param>
        [HttpPost("{id}/test")]
        public async Task<IActionResult> Test(int id)
        {
            var indexer = await _indexerRepository.GetByIdAsync(id);
            if (indexer == null)
            {
                return NotFound(new { message = "Indexer not found" });
            }

            return await ExecuteIndexerTestAsync(indexer, persist: true);
        }

        /// <summary>
        /// Test an indexer configuration without saving it. Useful for validating settings before creating an indexer.
        /// </summary>
        /// <param name="indexer">Indexer configuration to test (not persisted).</param>
        [HttpPost("test")]
        public async Task<IActionResult> TestDraft([FromBody] Indexer indexer)
        {
            if (indexer == null)
            {
                return BadRequest(new { message = "Index data is required" });
            }

            return await ExecuteIndexerTestAsync(indexer, persist: false);
        }

        /// <summary>
        /// Test Internet Archive indexer connection
        /// </summary>
        private async Task<IActionResult> TestInternetArchive(Indexer indexer, bool persist)
        {
            try
            {
                // Parse collection from AdditionalSettings
                string collection = "librivoxaudio"; // Default
                if (!string.IsNullOrEmpty(indexer.AdditionalSettings))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(indexer.AdditionalSettings);
                        if (doc.RootElement.TryGetProperty("collection", out var collectionProperty))
                        {
                            collection = collectionProperty.GetString() ?? "librivoxaudio";
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse AdditionalSettings for Internet Archive indexer");
                    }
                }

                // Build test URL with minimal query
                var testUrl = $"https://archive.org/advancedsearch.php?q=collection:{collection}&rows=1&output=json";

                _logger.LogInformation("Testing Internet Archive indexer '{Name}' with collection '{Collection}'",
                    LogRedaction.SanitizeText(indexer.Name), LogRedaction.SanitizeText(collection));

                // Make HTTP request
                using var response = await SendValidatedAsync(
                    currentUri => new HttpRequestMessage(HttpMethod.Get, currentUri),
                    testUrl);
                response.EnsureSuccessStatusCode();

                // Parse JSON response
                var content = await response.Content.ReadAsStringAsync();
                using var jsonDoc = JsonDocument.Parse(content);

                // Validate response structure
                if (!jsonDoc.RootElement.TryGetProperty("response", out var responseProperty))
                {
                    throw new InvalidOperationException("Invalid response format: missing 'response' property");
                }

                if (!responseProperty.TryGetProperty("docs", out var docsProperty))
                {
                    throw new InvalidOperationException("Invalid response format: missing 'docs' property");
                }

                // Update indexer with success
                await SaveTestResultAsync(indexer, persist, true, null);

                _logger.LogInformation("Internet Archive indexer '{Name}' test succeeded for collection '{Collection}'",
                    LogRedaction.SanitizeText(indexer.Name), LogRedaction.SanitizeText(collection));

                return Ok(new
                {
                    success = true,
                    message = $"Internet Archive connection successful for collection '{collection}'",
                    collection = collection,
                    indexer = RedactIndexerForCaller(indexer)
                });
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Internet Archive indexer '{Name}' test failed", LogRedaction.SanitizeText(indexer.Name));
                return await BuildIndexerTestBadRequestAsync(indexer, persist, "Internet Archive test failed", ex);
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogWarning(ex, "Internet Archive indexer '{Name}' test failed", LogRedaction.SanitizeText(indexer.Name));
                return await BuildIndexerTestBadRequestAsync(indexer, persist, "Internet Archive test failed", ex);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Internet Archive indexer '{Name}' test failed", LogRedaction.SanitizeText(indexer.Name));
                return await BuildIndexerTestBadRequestAsync(indexer, persist, "Internet Archive test failed", ex);
            }
            catch (UriFormatException ex)
            {
                _logger.LogWarning(ex, "Internet Archive indexer '{Name}' test failed", LogRedaction.SanitizeText(indexer.Name));
                return await BuildIndexerTestBadRequestAsync(indexer, persist, "Internet Archive test failed", ex);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Internet Archive indexer '{Name}' test failed", LogRedaction.SanitizeText(indexer.Name));
                return await BuildIndexerTestBadRequestAsync(indexer, persist, "Internet Archive test failed", ex);
            }
        }

        /// <summary>
        /// Test MyAnonamouse indexer connection
        /// </summary>
        private async Task<IActionResult> TestMyAnonamouse(Indexer indexer, bool persist)
        {
            try
            {
                // Parse mam_id from AdditionalSettings
                string mamId = string.Empty;

                if (!string.IsNullOrEmpty(indexer.AdditionalSettings))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(indexer.AdditionalSettings);
                        if (doc.RootElement.TryGetProperty("mam_id", out var mamIdProperty))
                        {
                            mamId = mamIdProperty.GetString() ?? string.Empty;
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse AdditionalSettings for MyAnonamouse indexer");
                    }
                }

                if (string.IsNullOrEmpty(mamId))
                {
                    throw new InvalidOperationException("MAM ID is required for MyAnonamouse");
                }

                // Build test URL (mam_id is sent as a cookie)
                var testUrl = $"https://www.myanonamouse.net/tor/js/loadSearchJSONbasic.php";

                _logger.LogInformation("Testing MyAnonamouse indexer '{Name}' with MAM ID '{MamId}'",
                    LogRedaction.SanitizeText(indexer.Name), LogRedaction.RedactText(mamId, LogRedaction.GetSensitiveValuesFromEnvironment().Concat(new[] { mamId })));

                // Create request with mam_id as cookie
                using var request = new HttpRequestMessage(HttpMethod.Post, testUrl);

                // Add browser-like headers to avoid "invalid request" errors
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                request.Headers.Accept.ParseAdd("application/json, text/javascript, */*; q=0.01");
                request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
                request.Headers.Referrer = new Uri("https://www.myanonamouse.net/");

                // Create form data (without mam_id since it's now in the cookie)
                var formData = new Dictionary<string, string>
                {
                    ["tor[text]"] = "test",
                    ["tor[srchIn][]"] = "title",
                    ["tor[searchType]"] = "all",
                    ["tor[searchIn]"] = "torrents",
                    ["tor[cat][]"] = "0",
                    ["tor[browseFlagsHideVsShow]"] = "0",
                    ["tor[startDate]"] = "",
                    ["tor[endDate]"] = "",
                    ["tor[hash]"] = "",
                    ["tor[sortType]"] = "default",
                    ["tor[startNumber]"] = "0",
                    ["perpage"] = "1",
                    ["thumbnail"] = "false",
                    ["dlLink"] = "",
                    ["description"] = ""
                };

                var formContent = new FormUrlEncodedContent(formData);
                request.Content = formContent;

                // Add mam_id as a cookie for authentication (bind cookie to the indexer's base host)
                var cookieContainer = new System.Net.CookieContainer();
                var baseUrl = indexer.Url.TrimEnd('/');
                var baseUri = new Uri(baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? baseUrl : "https://" + baseUrl);
                cookieContainer.Add(baseUri, new System.Net.Cookie("mam_id", mamId));
                try
                {
                    var host = baseUri.Host;
                    if (!host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                    {
                        var wwwUri = new Uri($"{baseUri.Scheme}://www.{host}");
                        cookieContainer.Add(wwwUri, new System.Net.Cookie("mam_id", mamId));
                    }
                }
                catch (UriFormatException ex)
                {
                    _logger.LogDebug(ex, "Failed to add www host alias cookie for MyAnonamouse test request to {Host}", baseUri.Host);
                }
                catch (System.Net.CookieException ex)
                {
                    _logger.LogDebug(ex, "Failed to add www host alias cookie for MyAnonamouse test request to {Host}", baseUri.Host);
                }

                // Create HttpClientHandler with cookies
                var handler = new HttpClientHandler
                {
                    CookieContainer = cookieContainer,
                    UseCookies = true
                };

                using var cookieClient = new HttpClient(handler);
                cookieClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                cookieClient.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/javascript, */*; q=0.01");
                cookieClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
                cookieClient.DefaultRequestHeaders.Referrer = new Uri("https://www.myanonamouse.net/");

                // Make HTTP request
                using var response = await cookieClient.SendAsync(request);
                response.EnsureSuccessStatusCode();

                // Parse JSON response
                var content = await response.Content.ReadAsStringAsync();
                using var jsonDoc = JsonDocument.Parse(content);

                // Validate response (MyAnonamouse returns JSON with data array)
                if (!jsonDoc.RootElement.TryGetProperty("data", out _))
                {
                    throw new InvalidOperationException("Invalid response format: missing 'data' property");
                }

                // Update indexer with success
                await SaveTestResultAsync(indexer, persist, true, null);

                _logger.LogInformation("MyAnonamouse indexer '{Name}' test succeeded with MAM ID '{MamId}'",
                    LogRedaction.SanitizeText(indexer.Name), LogRedaction.RedactText(mamId, LogRedaction.GetSensitiveValuesFromEnvironment().Concat(new[] { mamId })));

                return Ok(new
                {
                    success = true,
                    message = $"MyAnonamouse authentication successful with MAM ID '{mamId}'",
                    mam_id = RedactMamIdForCaller(mamId),
                    indexer = RedactIndexerForCaller(indexer)
                });
            }
            catch (HttpRequestException ex)
            {
                return await BuildMamTestFailureResultAsync(indexer, persist, ex);
            }
            catch (TaskCanceledException ex)
            {
                return await BuildMamTestFailureResultAsync(indexer, persist, ex);
            }
            catch (UriFormatException ex)
            {
                return await BuildMamTestFailureResultAsync(indexer, persist, ex);
            }
            catch (System.Net.CookieException ex)
            {
                return await BuildMamTestFailureResultAsync(indexer, persist, ex);
            }
            catch (JsonException ex)
            {
                return await BuildMamTestFailureResultAsync(indexer, persist, ex);
            }
            catch (InvalidOperationException ex)
            {
                return await BuildMamTestFailureResultAsync(indexer, persist, ex);
            }
        }

        private async Task<IActionResult> BuildMamTestFailureResultAsync(Indexer indexer, bool persist, Exception ex)
        {
            await SaveTestResultAsync(indexer, persist, false, ex.Message);

            _logger.LogWarning(ex, "MyAnonamouse indexer '{Name}' test failed", LogRedaction.SanitizeText(indexer.Name));

            return BadRequest(new
            {
                success = false,
                message = "MyAnonamouse test failed",
                error = ex.Message,
                indexer = RedactIndexerForCaller(indexer)
            });
        }

        /// <summary>
        /// Debug search against a MyAnonamouse indexer: returns raw response plus parsed results
        /// </summary>
        [HttpPost("{id}/debug-search")]
        [LocalOrAdmin]
        public async Task<IActionResult> DebugMyAnonamouseSearch(int id, [FromBody] JsonElement body)
        {
            var indexer = await _indexerRepository.GetByIdAsync(id);
            if (indexer == null) return NotFound(new { message = "Indexer not found" });

            try
            {
                string query = "test";
                if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("query", out var q))
                {
                    query = q.GetString() ?? "test";
                }

                // Parse mam_id from AdditionalSettings
                string mamId = string.Empty;
                if (!string.IsNullOrEmpty(indexer.AdditionalSettings))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(indexer.AdditionalSettings);
                        if (doc.RootElement.TryGetProperty("mam_id", out var mamIdProperty))
                            mamId = mamIdProperty.GetString() ?? string.Empty;
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogDebug(ex, "Failed parsing AdditionalSettings JSON for indexer {Id} during debug search", id);
                    }
                }

                if (string.IsNullOrEmpty(mamId))
                    return BadRequest(new { success = false, message = "MAM ID missing in indexer settings" });

                var testUrl = $"{indexer.Url.TrimEnd('/')}/tor/js/loadSearchJSONbasic.php";

                var formData = new Dictionary<string, string>
                {
                    ["tor[text]"] = query,
                    ["tor[srchIn][]"] = "title",
                    ["tor[searchType]"] = "all",
                    ["tor[searchIn]"] = "torrents",
                    ["tor[cat][]"] = "0",
                    ["tor[browseFlagsHideVsShow]"] = "0",
                    ["tor[startDate]"] = "",
                    ["tor[endDate]"] = "",
                    ["tor[hash]"] = "",
                    ["tor[sortType]"] = "default",
                    ["tor[startNumber]"] = "0",
                    ["perpage"] = "100",
                    ["thumbnail"] = "false",
                    ["dlLink"] = "",
                    ["description"] = ""
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, testUrl)
                {
                    Content = new FormUrlEncodedContent(formData)
                };

                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                request.Headers.Accept.ParseAdd("application/json, text/javascript, */*; q=0.01");
                request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
                request.Headers.Referrer = new Uri("https://www.myanonamouse.net/");

                var cookieContainer = new System.Net.CookieContainer();
                var baseUrl = indexer.Url.TrimEnd('/');
                var baseUri = new Uri(baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? baseUrl : "https://" + baseUrl);
                cookieContainer.Add(baseUri, new System.Net.Cookie("mam_id", mamId));
                try
                {
                    var host = baseUri.Host;
                    if (!host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                    {
                        var wwwUri = new Uri($"{baseUri.Scheme}://www.{host}");
                        cookieContainer.Add(wwwUri, new System.Net.Cookie("mam_id", mamId));
                    }
                }
                catch (UriFormatException ex)
                {
                    _logger.LogDebug(ex, "Failed to add www host alias cookie for MyAnonamouse debug search request to {Host}", baseUri.Host);
                }
                catch (System.Net.CookieException ex)
                {
                    _logger.LogDebug(ex, "Failed to add www host alias cookie for MyAnonamouse debug search request to {Host}", baseUri.Host);
                }

                var handler = new HttpClientHandler { CookieContainer = cookieContainer, UseCookies = true };
                using var client = new HttpClient(handler);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/javascript, */*; q=0.01");
                client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
                client.DefaultRequestHeaders.Referrer = new Uri("https://www.myanonamouse.net/");

                using var response = await client.SendAsync(request);
                var raw = await response.Content.ReadAsStringAsync();

                // Get parsed results via the Search API on this host
                var parsed = new List<SearchResult>();
                try
                {
                    var scheme = Request.Scheme;
                    var hostVal = Request.Host.Value;
                    var localSearchUrl = $"{scheme}://{hostVal}{ApiVersionPathBuilder.BuildApiPath($"/search/{id}", HttpContext)}?query={Uri.EscapeDataString(query)}";
                    using var localResp = await _httpClient.GetAsync(localSearchUrl);
                    if (localResp.IsSuccessStatusCode)
                    {
                        var json = await localResp.Content.ReadAsStringAsync();
                        var options = new System.Text.Json.JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        };
                        parsed = System.Text.Json.JsonSerializer.Deserialize<List<SearchResult>>(json, options) ?? new List<SearchResult>();
                    }
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogDebug(ex, "Failed to evaluate local parsed search results for indexer {Id}", indexer.Id);
                }
                catch (TaskCanceledException ex)
                {
                    _logger.LogDebug(ex, "Failed to evaluate local parsed search results for indexer {Id}", indexer.Id);
                }
                catch (JsonException ex)
                {
                    _logger.LogDebug(ex, "Failed to evaluate local parsed search results for indexer {Id}", indexer.Id);
                }
                catch (UriFormatException ex)
                {
                    _logger.LogDebug(ex, "Failed to evaluate local parsed search results for indexer {Id}", indexer.Id);
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogDebug(ex, "Failed to evaluate local parsed search results for indexer {Id}", indexer.Id);
                }

                return Ok(new { success = true, status = (int)response.StatusCode, raw, parsedCount = parsed.Count, parsed });
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "MyAnonamouse debug search failed for indexer {Id}", id);
                return BadRequest(new { success = false, error = ex.Message });
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogWarning(ex, "MyAnonamouse debug search failed for indexer {Id}", id);
                return BadRequest(new { success = false, error = ex.Message });
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "MyAnonamouse debug search failed for indexer {Id}", id);
                return BadRequest(new { success = false, error = ex.Message });
            }
            catch (UriFormatException ex)
            {
                _logger.LogWarning(ex, "MyAnonamouse debug search failed for indexer {Id}", id);
                return BadRequest(new { success = false, error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "MyAnonamouse debug search failed for indexer {Id}", id);
                return BadRequest(new { success = false, error = ex.Message });
            }
        }

        /// <summary>
        /// Toggle an indexer's enabled/disabled state.
        /// </summary>
        /// <param name="id">Indexer ID.</param>
        [HttpPut("{id}/toggle")]
        public async Task<IActionResult> Toggle(int id)
        {
            var indexer = await _indexerRepository.GetByIdAsync(id);
            if (indexer == null)
            {
                return NotFound(new { message = "Indexer not found" });
            }

            indexer.IsEnabled = !indexer.IsEnabled;
            indexer.UpdatedAt = DateTime.UtcNow;
            await _indexerRepository.UpdateAsync(indexer);

            _logger.LogInformation("Toggled indexer '{Name}' to {State}",
                indexer.Name, indexer.IsEnabled ? "enabled" : "disabled");

            return Ok(RedactIndexerForCaller(indexer));
        }

        /// <summary>
        /// Get only enabled indexers.
        /// </summary>
        [HttpGet("enabled")]
        public async Task<IActionResult> GetEnabled()
        {
            var indexers = (await _indexerRepository.GetAllAsync())
                .Where(i => i.IsEnabled)
                .OrderBy(i => i.Priority)
                .ThenBy(i => i.Name)
                .ToList();

            return Ok(RedactIndexersForCaller(indexers));
        }

        /// <summary>
        /// Normalize indexer URL by removing duplicate or trailing '/api' segments and ensuring a scheme
        /// </summary>
        private string NormalizeIndexerUrl(string? rawUrl)
        {
            if (string.IsNullOrWhiteSpace(rawUrl)) return rawUrl ?? string.Empty;

            var url = rawUrl.Trim();

            // Add scheme if missing (assume https)
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url;
            }

            // Remove repeated '/api/api' or trailing '/api'
            // Normalize multiple slashes
            while (url.Contains("/api/api", StringComparison.OrdinalIgnoreCase))
            {
                url = url.Replace("/api/api", "/api", StringComparison.OrdinalIgnoreCase);
            }

            // Preserve /api for Prowlarr proxy URLs (/{id}/api or /api/v{version}/indexer/{id}/api)
            var prowlarrProxyPattern = @"/((api/v\d+(?:\.\d+)?/indexer/\d+)|\d+)/api$";
            if (url.EndsWith("/api", StringComparison.OrdinalIgnoreCase) &&
                !Regex.IsMatch(url, prowlarrProxyPattern, RegexOptions.IgnoreCase))
            {
                url = url.Substring(0, url.Length - 4);
            }

            return url.TrimEnd('/');
        }

        private string BuildProwlarrBaseUrl(string rawUrl, int? port)
        {
            var trimmed = rawUrl.Trim();
            if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = "http://" + trimmed;
            }

            var builder = new UriBuilder(trimmed);
            if (port.HasValue && port.Value > 0)
            {
                builder.Port = port.Value;
            }

            return builder.Uri.ToString().TrimEnd('/');
        }

        private string BuildProwlarrProxyUrl(string baseUrl, int indexerId)
        {
            var root = baseUrl.TrimEnd('/');
            return $"{root}/{indexerId}/api";
        }

        private string NormalizeProwlarrProxyUrl(string? rawUrl)
        {
            if (string.IsNullOrWhiteSpace(rawUrl)) return rawUrl ?? string.Empty;
            return rawUrl.Trim().TrimEnd('/');
        }

        private async Task<(HttpResponseMessage Response, string Payload)> FetchProwlarrIndexersAsync(string baseUrl, string apiKey)
        {
            var encodedKey = System.Net.WebUtility.UrlEncode(apiKey);
            // NOTE: This targets external Prowlarr instances, whose API path is /api/v1.
            // It is intentionally independent from Listenarr's own API version segment.
            var endpoints = new List<string>
            {
                $"{baseUrl}/api/v1/indexer",
                $"{baseUrl}/api/v1/indexer?apikey={encodedKey}"
            };

            HttpResponseMessage? lastResponse = null;
            string lastPayload = string.Empty;

            foreach (var endpoint in endpoints)
            {
                var response = await SendValidatedAsync(currentUri =>
                {
                    var retryRequest = new HttpRequestMessage(HttpMethod.Get, currentUri);
                    retryRequest.Headers.Add("X-Api-Key", apiKey);
                    return retryRequest;
                }, endpoint);
                var body = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    return (response, body);
                }

                lastResponse?.Dispose();
                lastResponse = response;
                lastPayload = body;

                if (response.StatusCode != System.Net.HttpStatusCode.MethodNotAllowed &&
                    response.StatusCode != System.Net.HttpStatusCode.Unauthorized &&
                    response.StatusCode != System.Net.HttpStatusCode.Forbidden)
                {
                    break;
                }
            }

            return (lastResponse ?? new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway), lastPayload);
        }

        private async Task<Dictionary<string, string>?> TryFetchProwlarrTagMapAsync(string baseUrl, string apiKey)
        {
            try
            {
                var encodedKey = System.Net.WebUtility.UrlEncode(apiKey);
                var endpoints = new List<string>
                {
                    $"{baseUrl}/api/v1/tag",
                    $"{baseUrl}/api/v1/tag?apikey={encodedKey}"
                };

                foreach (var endpoint in endpoints)
                {
                    using var response = await SendValidatedAsync(currentUri =>
                    {
                        var retryRequest = new HttpRequestMessage(HttpMethod.Get, currentUri);
                        retryRequest.Headers.Add("X-Api-Key", apiKey);
                        return retryRequest;
                    }, endpoint);

                    var body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        if (response.StatusCode != System.Net.HttpStatusCode.MethodNotAllowed &&
                            response.StatusCode != System.Net.HttpStatusCode.Unauthorized &&
                            response.StatusCode != System.Net.HttpStatusCode.Forbidden)
                        {
                            break;
                        }

                        continue;
                    }

                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    {
                        return null;
                    }

                    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var tag in doc.RootElement.EnumerateArray())
                    {
                        if (!tag.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.Number)
                        {
                            continue;
                        }

                        var id = idProp.GetInt32().ToString();
                        var label =
                            tag.TryGetProperty("label", out var labelProp) && labelProp.ValueKind == JsonValueKind.String
                                ? labelProp.GetString()
                                : tag.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                                    ? nameProp.GetString()
                                    : null;

                        if (!string.IsNullOrWhiteSpace(label))
                        {
                            result[id] = label.Trim();
                        }
                    }

                    return result;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to load Prowlarr tags from {Url}", LogRedaction.SanitizeUrl(baseUrl));
            }

            return null;
        }

        private static HashSet<string> GetProwlarrTagValues(JsonElement element, IReadOnlyDictionary<string, string>? tagMap)
        {
            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (element.TryGetProperty("tags", out var rawTags))
            {
                AddTagValues(rawTags, tags, tagMap);
            }

            if (element.TryGetProperty("tagNames", out var tagNames))
            {
                AddTagValues(tagNames, tags, tagMap);
            }

            if (element.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array)
            {
                foreach (var field in fields.EnumerateArray())
                {
                    if (!field.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var fieldName = nameProp.GetString();
                    if (!string.Equals(fieldName, "tags", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(fieldName, "tagNames", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (field.TryGetProperty("value", out var valueProp))
                    {
                        AddTagValues(valueProp, tags, tagMap);
                    }
                }
            }

            return tags;
        }

        private static void AddTagValues(JsonElement value, HashSet<string> tags, IReadOnlyDictionary<string, string>? tagMap)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    foreach (var part in value.GetString()?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Array.Empty<string>())
                    {
                        AddTagValue(part, tags, tagMap);
                    }
                    break;
                case JsonValueKind.Number:
                    AddTagValue(value.ToString(), tags, tagMap);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in value.EnumerateArray())
                    {
                        AddTagValues(item, tags, tagMap);
                    }
                    break;
                case JsonValueKind.Object:
                    if (value.TryGetProperty("label", out var labelProp) && labelProp.ValueKind == JsonValueKind.String)
                    {
                        AddTagValue(labelProp.GetString(), tags, tagMap);
                    }
                    else if (value.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                    {
                        AddTagValue(nameProp.GetString(), tags, tagMap);
                    }
                    else if (value.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
                    {
                        AddTagValue(idProp.GetInt32().ToString(), tags, tagMap);
                    }
                    break;
            }
        }

        private static void AddTagValue(string? rawValue, HashSet<string> tags, IReadOnlyDictionary<string, string>? tagMap)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return;
            }

            var trimmed = rawValue.Trim();
            tags.Add(trimmed);

            if (tagMap != null && tagMap.TryGetValue(trimmed, out var label) && !string.IsNullOrWhiteSpace(label))
            {
                tags.Add(label.Trim());
            }
        }

        private static bool PayloadRequiresProwlarrTagMap(JsonElement payload)
        {
            if (payload.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            return payload.EnumerateArray().Any(ElementRequiresProwlarrTagMap);
        }

        private static bool ElementRequiresProwlarrTagMap(JsonElement element)
        {
            var hasTagData = false;
            var hasTextualTagData = false;

            if (element.TryGetProperty("tags", out var rawTags))
            {
                InspectProwlarrTagValue(rawTags, ref hasTagData, ref hasTextualTagData);
            }

            if (element.TryGetProperty("tagNames", out var tagNames))
            {
                InspectProwlarrTagValue(tagNames, ref hasTagData, ref hasTextualTagData);
            }

            if (element.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array)
            {
                foreach (var field in fields.EnumerateArray())
                {
                    if (!field.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var fieldName = nameProp.GetString();
                    if (!string.Equals(fieldName, "tags", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(fieldName, "tagNames", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (field.TryGetProperty("value", out var valueProp))
                    {
                        InspectProwlarrTagValue(valueProp, ref hasTagData, ref hasTextualTagData);
                    }
                }
            }

            return hasTagData && !hasTextualTagData;
        }

        private static void InspectProwlarrTagValue(JsonElement value, ref bool hasTagData, ref bool hasTextualTagData)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    foreach (var part in value.GetString()?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Array.Empty<string>())
                    {
                        InspectProwlarrTagToken(part, ref hasTagData, ref hasTextualTagData);
                    }
                    break;
                case JsonValueKind.Number:
                    hasTagData = true;
                    break;
                case JsonValueKind.Array:
                    foreach (var item in value.EnumerateArray())
                    {
                        InspectProwlarrTagValue(item, ref hasTagData, ref hasTextualTagData);
                    }
                    break;
                case JsonValueKind.Object:
                    if (value.TryGetProperty("label", out var labelProp) && labelProp.ValueKind == JsonValueKind.String)
                    {
                        InspectProwlarrTagToken(labelProp.GetString(), ref hasTagData, ref hasTextualTagData);
                    }
                    else if (value.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                    {
                        InspectProwlarrTagToken(nameProp.GetString(), ref hasTagData, ref hasTextualTagData);
                    }
                    else if (value.TryGetProperty("id", out var idProp))
                    {
                        if (idProp.ValueKind == JsonValueKind.Number)
                        {
                            hasTagData = true;
                        }
                        else if (idProp.ValueKind == JsonValueKind.String)
                        {
                            InspectProwlarrTagToken(idProp.GetString(), ref hasTagData, ref hasTextualTagData);
                        }
                    }
                    break;
            }
        }

        private static void InspectProwlarrTagToken(string? rawValue, ref bool hasTagData, ref bool hasTextualTagData)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return;
            }

            hasTagData = true;
            if (!long.TryParse(rawValue.Trim(), out _))
            {
                hasTextualTagData = true;
            }
        }

        private static string? GetFieldStringValue(JsonElement element, string fieldName)
        {
            if (!element.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var field in fields.EnumerateArray())
            {
                if (!field.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (!string.Equals(nameProp.GetString(), fieldName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!field.TryGetProperty("value", out var valueProp))
                {
                    continue;
                }

                return valueProp.ValueKind == JsonValueKind.String ? valueProp.GetString() : valueProp.ToString();
            }

            return null;
        }

        private static HashSet<int> GetCategoryIdsFromProwlarrIndexer(JsonElement element)
        {
            var categories = new HashSet<int>();

            if (element.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Object &&
                caps.TryGetProperty("categories", out var catArray) && catArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var cat in catArray.EnumerateArray())
                {
                    TryAddCategoryId(cat, categories);

                    if (cat.ValueKind == JsonValueKind.Object && cat.TryGetProperty("subCategories", out var subCats) && subCats.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var sub in subCats.EnumerateArray())
                        {
                            TryAddCategoryId(sub, categories);
                        }
                    }
                }
            }

            if (element.TryGetProperty("categories", out var directCategories))
            {
                AddCategoryValues(directCategories, categories);
            }

            if (element.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array)
            {
                foreach (var field in fields.EnumerateArray())
                {
                    if (!field.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    if (!string.Equals(nameProp.GetString(), "categories", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (field.TryGetProperty("value", out var valueProp))
                    {
                        AddCategoryValues(valueProp, categories);
                    }
                }
            }

            return categories;
        }

        private static void AddCategoryValues(JsonElement value, HashSet<int> categories)
        {
            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in value.EnumerateArray())
                {
                    TryAddCategoryId(v, categories);
                }
            }
            else
            {
                TryAddCategoryId(value, categories);
            }
        }

        private static void TryAddCategoryId(JsonElement element, HashSet<int> categories)
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("id", out var idProp))
            {
                TryAddCategoryId(idProp, categories);
                return;
            }

            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var id))
            {
                categories.Add(id);
                return;
            }

            if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out var parsed))
            {
                categories.Add(parsed);
            }
        }
    }
}

