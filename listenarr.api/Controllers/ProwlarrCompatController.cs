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
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Models;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Application.Security;
using Listenarr.Api.Attributes;

namespace Listenarr.Api.Controllers
{
    [ApiController]
    [Route("api/v1/prowlarr")]
    [Tags("Prowlarr Compatibility")]
    [ApiExplorerSettings(IgnoreApi = true)]
    [RequireApiKey]
    public class ProwlarrCompatController : ControllerBase
    {
        private StartupConfig GetStartupConfig()
        {
            try
            {
                var cfg = _startupConfigService.GetConfig();
                if (cfg != null) return cfg;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger?.LogDebug(ex, "ProwlarrCompat: Failed to load startup config from IStartupConfigService; falling back");
            }

            return new StartupConfig();
        }

        private readonly ILogger<ProwlarrCompatController> _logger;
        private readonly IIndexerRepository _indexerRepository;
        private readonly IHubBroadcaster _hubBroadcaster;
        private readonly IRealtimeClientRegistry _realtimeClientRegistry;
        private readonly IToastService _toastService;
        private readonly IStartupConfigService _startupConfigService;
        private readonly IApplicationVersionService _applicationVersionService;

        // Suppress update toasts for indexers that were created within this window (in seconds)
        private const int NotificationSuppressionSeconds = 5;

        // Track last toast timestamps per indexer id to avoid duplicate toasts when rapid updates/deletes occur
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, DateTime> _lastToastTimes = new System.Collections.Concurrent.ConcurrentDictionary<int, DateTime>();

        // Track last global toast messages to deduplicate identical messages across indexers (message text -> last sent time)
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastToastMessages = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>();

        private static bool ShouldSendToastForIndexer(int indexerId, string message)
        {
            try
            {
                var now = DateTime.UtcNow;
                if (_lastToastTimes.TryGetValue(indexerId, out var last) && (now - last).TotalSeconds < NotificationSuppressionSeconds)
                {
                    return false;
                }

                // Update last time for this indexer
                _lastToastTimes[indexerId] = now;
                return true;
            }
            catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException)
            {
                // Fallback to sending toast if anything goes wrong with suppression logic
                return true;
            }
        }

        private static bool ShouldSendToastForMessage(string message)
        {
            try
            {
                var now = DateTime.UtcNow;
                var key = message ?? string.Empty;
                if (_lastToastMessages.TryGetValue(key, out var last) && (now - last).TotalSeconds < NotificationSuppressionSeconds)
                {
                    return false;
                }

                _lastToastMessages[key] = now;
                return true;
            }
            catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException)
            {
                return true;
            }
        }

        public ProwlarrCompatController(
            ILogger<ProwlarrCompatController> logger,
            IIndexerRepository indexerRepository,
            IHubBroadcaster hubBroadcaster,
            IRealtimeClientRegistry realtimeClientRegistry,
            IToastService toastService,
            IStartupConfigService startupConfigService,
            IApplicationVersionService applicationVersionService)
        {
            _logger = logger;
            _indexerRepository = indexerRepository;
            _hubBroadcaster = hubBroadcaster;
            _realtimeClientRegistry = realtimeClientRegistry;
            _toastService = toastService;
            _startupConfigService = startupConfigService;
            _applicationVersionService = applicationVersionService;
        }

        private string GetApplicationVersion()
        {
            return _applicationVersionService.Resolve();
        }

        /// <summary>
        /// GET /api/v1/system/status
        /// Minimal Prowlarr-compatible system status endpoint.
        /// </summary>
        [HttpGet("system/status")]
        [AllowAnonymous]
        [Produces("application/json")]
        public IActionResult GetSystemStatus()
        {
            Response.ContentType = "application/json";
            var dto = new SystemStatusDto
            {
                Status = "ok",
                Version = GetApplicationVersion(),
                Api = "Listenarr"
            };
            return Ok(dto);
        }

        /// <summary>
        /// POST /api/v1/indexer/test
        /// Responds with JSON and includes X-Application-Version header (useful for Prowlarr client checks)
        /// </summary>
        [HttpPost("indexer/test")]
        [IgnoreAntiforgeryToken]
        [AllowAnonymous]
        [Produces("application/json")]
        public IActionResult PostIndexerTest()
        {
            _logger?.LogInformation("Prowlarr indexer test invoked (POST)");
            Response.ContentType = "application/json";
            var version = GetApplicationVersion();
            Response.Headers["X-Application-Version"] = version;
            var dto = new IndexerTestResponseDto
            {
                Success = true,
                Message = "Test OK",
                Version = version
            };
            return Ok(dto);
        }

        [HttpGet("indexer/test")]
        [AllowAnonymous]
        [Produces("application/json")]
        public IActionResult GetIndexerTest()
        {
            _logger?.LogInformation("Prowlarr indexer test invoked (GET)");
            Response.ContentType = "application/json";
            var version = GetApplicationVersion();
            Response.Headers["X-Application-Version"] = version;
            var dto = new IndexerTestResponseDto
            {
                Success = true,
                Message = "Test OK (GET)",
                Version = version
            };
            return Ok(dto);
        }

        // Debug-only POST to verify POST handling bypasses antiforgery and auth middleware
        [HttpPost("debug/test")]
        [AllowAnonymous]
        [LocalOrAdmin]
        [IgnoreAntiforgeryToken]
        [Produces("application/json")]
        public IActionResult PostDebugTest()
        {
            Response.ContentType = "application/json";
            return Ok(new { ok = true });
        }

        /// <summary>
        /// GET /api/v1/indexer
        /// Returns the list of configured indexers (Prowlarr expects a JSON array here).
        /// Maintained for Prowlarr compatibility: returns persisted indexers from the DB.
        /// </summary>
        [HttpGet("indexer")]
        [AllowAnonymous]
        [Produces("application/json")]
        public async Task<IActionResult> GetIndexers()
        {
            var cfg = GetStartupConfig();
            var authEnabled = cfg.IsAuthenticationEnabled();
            if (HttpContext?.Response != null) HttpContext.Response.ContentType = "application/json";
            var indexers = (await _indexerRepository.GetAllAsync())
                .OrderBy(i => i.Priority)
                .ThenBy(i => i.Name)
                .Select(i => new
                {
                    id = i.Id,
                    name = i.Name,
                    implementation = i.Implementation,
                    baseUrl = i.Url,
                    apiKey = authEnabled ? i.ApiKey : null,
                    categories = string.IsNullOrEmpty(i.Categories) ? System.Array.Empty<string>() : i.Categories.Split(',').Select(s => s.Trim()).ToArray(),
                    settings = new
                    {
                        baseUrl = i.Url,
                        apiKey = authEnabled ? i.ApiKey : null,
                        apiPath = string.Empty,
                        categories = string.IsNullOrEmpty(i.Categories) ? System.Array.Empty<string>() : i.Categories.Split(',').Select(s => s.Trim()).ToArray()
                    },
                    fields = new[]
                    {
                        new FieldDto("baseUrl", i.Url ?? string.Empty),
                        new FieldDto("apiKey", authEnabled ? i.ApiKey : null),
                        new FieldDto("apiPath", string.Empty),
                        new FieldDto("categories", string.IsNullOrEmpty(i.Categories) ? System.Array.Empty<string>() : i.Categories.Split(',').Select(s => s.Trim()).ToArray())
                    },
                    tags = System.Array.Empty<int>()
                })
                .ToArray();
            return Ok(indexers);
        }

        /// <summary>
        /// GET /api/v1/indexer/{id}
        /// Returns a detailed indexer object for a specific indexer id. Includes a `settings` object for compatibility with consumers expecting nested settings.
        /// </summary>
        [HttpGet("indexer/{id:int}")]
        [AllowAnonymous]
        [Produces("application/json")]
        public async Task<IActionResult> GetIndexerById(int id)
        {
            var cfg = GetStartupConfig();
            var authEnabled = cfg.IsAuthenticationEnabled();
            Response.ContentType = "application/json";
            var i = await _indexerRepository.GetByIdAsync(id);
            if (i == null)
            {
                var fallback = new
                {
                    id = id,
                    name = "Prowlarr Indexer",
                    implementation = "Newznab",
                    baseUrl = string.Empty,
                    apiKey = (string?)null,
                    categories = System.Array.Empty<string>(),
                    settings = new
                    {
                        baseUrl = string.Empty,
                        apiKey = (string?)null,
                        apiPath = string.Empty,
                        categories = System.Array.Empty<string>()
                    },
                    fields = new[]
                    {
                        new FieldDto("baseUrl", string.Empty),
                        new FieldDto("apiKey", null),
                        new FieldDto("apiPath", string.Empty),
                        new FieldDto("categories", System.Array.Empty<string>())
                    },
                    tags = System.Array.Empty<int>()
                };
                return Ok(fallback);
            }
            var dto = new
            {
                id = i.Id,
                name = i.Name,
                implementation = i.Implementation,
                baseUrl = i.Url,
                apiKey = authEnabled ? i.ApiKey : null,
                categories = string.IsNullOrEmpty(i.Categories) ? System.Array.Empty<string>() : i.Categories.Split(',').Select(s => s.Trim()).ToArray(),
                settings = new
                {
                    baseUrl = i.Url,
                    apiKey = authEnabled ? i.ApiKey : null,
                    apiPath = string.Empty,
                    categories = string.IsNullOrEmpty(i.Categories) ? System.Array.Empty<string>() : i.Categories.Split(',').Select(s => s.Trim()).ToArray()
                },
                fields = new[]
                {
                    new FieldDto("baseUrl", i.Url ?? string.Empty),
                    new FieldDto("apiKey", authEnabled ? i.ApiKey : null),
                    new FieldDto("apiPath", string.Empty),
                    new FieldDto("categories", string.IsNullOrEmpty(i.Categories) ? System.Array.Empty<string>() : i.Categories.Split(',').Select(s => s.Trim()).ToArray())
                },
                tags = System.Array.Empty<int>()
            };
            return Ok(dto);
        }

        /// <summary>
        /// GET /api/v1/indexer/info
        /// Compatibility endpoint that returns metadata about supported implementations and schema endpoint.
        /// </summary>
        [HttpGet("indexer/info")]
        [AllowAnonymous]
        [Produces("application/json")]
        public IActionResult GetIndexersInfo()
        {
            Response.ContentType = "application/json";
            var payload = new
            {
                implementations = new[] { "Newznab", "Torznab" },
                schema = "/api/v1/indexer/schema"
            };
            return Ok(payload);
        }

        /// <summary>
        /// GET /api/v1/indexers
        /// Returns the list of configured indexers (Prowlarr expects a JSON array here).
        /// </summary>
        [HttpGet("indexers")]
        [AllowAnonymous]
        [Produces("application/json")]
        public async Task<IActionResult> GetIndexersList()
        {
            Response.ContentType = "application/json";
            // Frontend and compatibility clients both call this endpoint. Return persisted
            // indexers in the standard shape used by the UI so versioned routing does not
            // accidentally break first-party indexer listing.
            var indexers = (await _indexerRepository.GetAllAsync())
                .OrderBy(i => i.Priority)
                .ThenBy(i => i.Name)
                .ToList();

            if (HttpSecurityRequestUtils.ShouldRedactSecretsForCaller(HttpContext))
            {
                indexers = indexers.Select(ApiResponseRedactor.RedactIndexer).ToList();
            }

            return Ok(indexers);
        }

        /// <summary>
        /// DELETE /api/v1/indexer/{id}
        /// Removes a persisted indexer by id. Matches standard semantics: id must be > 0 and the endpoint returns an empty JSON object on success.
        /// Maintained for Prowlarr compatibility so remote apps can delete indexers.
        /// </summary>
        [HttpDelete("indexer/{id:int}")]
        [AllowAnonymous]
        [IgnoreAntiforgeryToken]
        [Produces("application/json")]
        public async Task<IActionResult> DeleteIndexer(int id)
        {
            Response.ContentType = "application/json";
            try
            {
                // Validate id (reject id <= 0), but be tolerant for external clients that may send 0.
                if (id <= 0)
                {
                    var remoteIp = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";
                    _logger?.LogWarning("Prowlarr: Delete requested with invalid id {Id} from {RemoteIp}", id, remoteIp);
                    return Ok(new { });
                }
                var i = await _indexerRepository.GetByIdAsync(id);
                if (i != null)
                {
                    await _indexerRepository.DeleteAsync(id);
                    _logger?.LogInformation("Prowlarr: Deleted indexer {Id} (name={Name})", i.Id, i.Name);
                    try
                    {
                        await _hubBroadcaster.BroadcastAsync(RealtimeHubTarget.Settings, "IndexersUpdated", new { created = 0, skipped = 0, indexers = new[] { new { id = i.Id, name = i.Name, baseUrl = i.Url } } });
                        var deleteMessage = $"Removed indexer: {i.Name}";
                        if (ShouldSendToastForIndexer(i.Id, deleteMessage) && ShouldSendToastForMessage(deleteMessage))
                        {
                            await _toastService.PublishNotificationAsync("Indexers", deleteMessage, icon: null, timeoutMs: 8000);
                        }
                        else
                        {
                            _logger?.LogDebug("Suppressing delete toast for indexer {Id} due to recent toast or duplicate message", i.Id);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger?.LogWarning(ex, "Failed to broadcast IndexersUpdated after delete");
                    }
                }
                else
                {
                    _logger?.LogInformation("Prowlarr: Delete requested for non-existent indexer {Id}", id);
                }
                return Ok(new { });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger?.LogError(ex, "Failed to delete indexer {Id}", id);
                return StatusCode(500, new { error = "Failed to delete indexer" });
            }
        }

        /// <summary>
        /// PUT /api/v1/indexer/{id}
        /// Update an existing indexer by id. Accepts same tolerant payload shapes as POST.
        /// </summary>
        [HttpPut("indexer/{id:int}")]
        [AllowAnonymous]
        [IgnoreAntiforgeryToken]
        [Produces("application/json")]
        public async Task<IActionResult> PutIndexer(int id, [FromBody] System.Text.Json.JsonElement payload)
        {
            if (HttpContext?.Response != null) HttpContext.Response.ContentType = "application/json";

            try
            {
                try
                {
                    var raw = payload.GetRawText();
                    var redacted = LogRedaction.RedactText(raw, LogRedaction.GetSensitiveValuesFromEnvironment());
                    _logger?.LogInformation("Prowlarr indexer update payload body: {Payload}", redacted);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    System.Diagnostics.Debug.WriteLine($"ProwlarrCompatController payload logging failed (PUT indexer): {ex.Message}");
                }

                var indexer = await _indexerRepository.GetByIdAsync(id);
                var created = false;
                if (indexer == null)
                {
                    // Parse payload for upsert/create (tolerant to fields/settings shapes)
                    var nameFromPayload = GetStringProperty(payload, "name", "title");
                    var implementationFromPayload = GetStringProperty(payload, "implementation", "type");
                    var baseUrlFromPayload = GetStringProperty(payload, "baseUrl", "url");
                    var apiPathFromPayload = GetStringProperty(payload, "apiPath", null);
                    var apiKeyFromPayload = GetStringProperty(payload, "apiKey", null);
                    var categoriesFromPayload = ParseCategories(payload);

                    // Try settings object
                    if (string.IsNullOrEmpty(baseUrlFromPayload) && payload.TryGetProperty("settings", out var settingsPayload) && settingsPayload.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        baseUrlFromPayload = GetStringProperty(settingsPayload, "baseUrl", "url");
                        if (string.IsNullOrEmpty(apiKeyFromPayload)) apiKeyFromPayload = GetStringProperty(settingsPayload, "apiKey", "apikey");
                        if (string.IsNullOrEmpty(apiPathFromPayload)) apiPathFromPayload = GetStringProperty(settingsPayload, "apiPath", null);
                    }

                    // Try fields array
                    if (payload.TryGetProperty("fields", out var fieldsArray) && fieldsArray.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var f in fieldsArray.EnumerateArray().Where(field => field.ValueKind == System.Text.Json.JsonValueKind.Object))
                        {
                            var fname = GetStringProperty(f, "name", null);
                            if (string.IsNullOrEmpty(fname)) continue;

                            if (fname.Equals("baseUrl", System.StringComparison.InvariantCultureIgnoreCase) &&
                                f.TryGetProperty("value", out var baseUrlValue) &&
                                baseUrlValue.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                baseUrlFromPayload = baseUrlValue.GetString() ?? baseUrlFromPayload;
                            }

                            if (fname.Equals("apiKey", System.StringComparison.InvariantCultureIgnoreCase) &&
                                f.TryGetProperty("value", out var apiKeyValue) &&
                                apiKeyValue.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                apiKeyFromPayload = apiKeyValue.GetString() ?? apiKeyFromPayload;
                            }

                            if (fname.Equals("apiPath", System.StringComparison.InvariantCultureIgnoreCase) &&
                                f.TryGetProperty("value", out var apiPathValue) &&
                                apiPathValue.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                apiPathFromPayload = apiPathValue.GetString() ?? apiPathFromPayload;
                            }

                            if (fname.Equals("categories", System.StringComparison.InvariantCultureIgnoreCase))
                            {
                                if (f.TryGetProperty("value", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Array)
                                {
                                    var parts = v.EnumerateArray().Select(x => x.ValueKind == System.Text.Json.JsonValueKind.Number ? x.GetInt32().ToString() : x.GetString() ?? string.Empty).Where(s => !string.IsNullOrEmpty(s));
                                    categoriesFromPayload = string.Join(',', parts);
                                }
                                else if (f.TryGetProperty("value", out var vs) && vs.ValueKind == System.Text.Json.JsonValueKind.String)
                                {
                                    categoriesFromPayload = vs.GetString() ?? categoriesFromPayload;
                                }
                            }
                        }
                    }

                    var urlFromPayload = (baseUrlFromPayload ?? string.Empty).Trim();
                    if (!string.IsNullOrEmpty(apiPathFromPayload) && !string.IsNullOrEmpty(urlFromPayload))
                    {
                        urlFromPayload = urlFromPayload.TrimEnd('/') + "/" + apiPathFromPayload.Trim('/');
                    }

                    var normalized = NormalizeIndexerUrl(urlFromPayload);
                    var allIndexers = await _indexerRepository.GetAllAsync();
                    var existing = allIndexers.FirstOrDefault(i => NormalizeIndexerUrl(i.Url) == normalized && (i.ApiKey ?? string.Empty) == (apiKeyFromPayload ?? string.Empty));
                    if (existing != null)
                    {
                        indexer = await _indexerRepository.GetByIdAsync(existing.Id);
                    }
                    else
                    {
                        // Not found: create new indexer entry from parsed payload (upsert behavior)
                        indexer = new Indexer
                        {
                            Name = string.IsNullOrEmpty(nameFromPayload) ? (string.IsNullOrEmpty(baseUrlFromPayload) ? "Prowlarr Indexer" : baseUrlFromPayload) : nameFromPayload,
                            Implementation = string.IsNullOrEmpty(implementationFromPayload) ? "Custom" : implementationFromPayload,
                            Url = urlFromPayload,
                            ApiKey = string.IsNullOrEmpty(apiKeyFromPayload) ? null : apiKeyFromPayload,
                            Categories = categoriesFromPayload ?? string.Empty,
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow,
                            IsEnabled = true,
                            Tags = string.Empty,
                            AdditionalSettings = string.Empty
                        };

                        var implLower = (indexer.Implementation ?? string.Empty).ToLowerInvariant();
                        indexer.Type = implLower.Contains("newznab") ? "Usenet" : (implLower.Contains("torznab") ? "Torrent" : "Custom");

                        indexer = await _indexerRepository.AddAsync(indexer);
                        created = true;

                        _logger?.LogInformation("Prowlarr: Created indexer (upsert from PUT) (name={Name}, url={Url}, apiKeyPresent={HasApiKey})", indexer.Name, indexer.Url, !string.IsNullOrEmpty(indexer.ApiKey));

                        // NOTE: Do not broadcast notifications here. We will broadcast once at the end of the handler
                        // after dedupe to avoid duplicate notifications when concurrent PUTs create duplicate entries.
                    }
                }

                // Defensive: indexer should be non-null after upsert logic; if it is still null, return an error instead of throwing
                if (indexer == null)
                {
                    _logger?.LogError("Prowlarr: Indexer was null after upsert logic for id={Id}", id);
                    return StatusCode(500, new { error = "Failed to locate or create indexer" });
                }

                if (payload.ValueKind != System.Text.Json.JsonValueKind.Object)
                {
                    return BadRequest(new { message = "Expected JSON object for indexer update" });
                }

                // Extract tolerant fields from payload
                var name = GetStringProperty(payload, "name", "title");
                var implementation = GetStringProperty(payload, "implementation", "type");
                var baseUrl = GetStringProperty(payload, "baseUrl", "url");
                var apiPath = GetStringProperty(payload, "apiPath", null);
                var apiKey = GetStringProperty(payload, "apiKey", null);

                // Try settings object
                if (string.IsNullOrEmpty(baseUrl) && payload.TryGetProperty("settings", out var settings) && settings.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    baseUrl = GetStringProperty(settings, "baseUrl", "url");
                    if (string.IsNullOrEmpty(apiKey)) apiKey = GetStringProperty(settings, "apiKey", "apikey");
                    if (string.IsNullOrEmpty(apiPath)) apiPath = GetStringProperty(settings, "apiPath", null);
                }

                // Try fields array
                var categories = ParseCategories(payload);

                if (payload.TryGetProperty("fields", out var fieldsProp) && fieldsProp.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var f in fieldsProp.EnumerateArray().Where(field => field.ValueKind == System.Text.Json.JsonValueKind.Object))
                    {
                        var fname = GetStringProperty(f, "name", null);
                        if (string.IsNullOrEmpty(fname)) continue;

                        if (fname.Equals("baseUrl", System.StringComparison.InvariantCultureIgnoreCase) &&
                            f.TryGetProperty("value", out var baseUrlValue) &&
                            baseUrlValue.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            baseUrl = baseUrlValue.GetString() ?? baseUrl;
                        }

                        if (fname.Equals("apiKey", System.StringComparison.InvariantCultureIgnoreCase) &&
                            f.TryGetProperty("value", out var apiKeyValue) &&
                            apiKeyValue.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            apiKey = apiKeyValue.GetString() ?? apiKey;
                        }

                        if (fname.Equals("apiPath", System.StringComparison.InvariantCultureIgnoreCase) &&
                            f.TryGetProperty("value", out var apiPathValue) &&
                            apiPathValue.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            apiPath = apiPathValue.GetString() ?? apiPath;
                        }

                        if (fname.Equals("categories", System.StringComparison.InvariantCultureIgnoreCase))
                        {
                            if (f.TryGetProperty("value", out var categoriesValue) && categoriesValue.ValueKind == System.Text.Json.JsonValueKind.Array)
                            {
                                var parts = categoriesValue.EnumerateArray().Select(x => x.ValueKind == System.Text.Json.JsonValueKind.Number ? x.GetInt32().ToString() : x.GetString() ?? string.Empty).Where(s => !string.IsNullOrEmpty(s));
                                categories = string.Join(',', parts);
                            }
                            else if (f.TryGetProperty("value", out var vs) && vs.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                categories = vs.GetString() ?? categories;
                            }
                        }
                    }
                }

                // Apply updates
                if (!string.IsNullOrEmpty(name)) indexer.Name = name;
                if (!string.IsNullOrEmpty(implementation)) indexer.Implementation = implementation;

                var url = (baseUrl ?? string.Empty).Trim();
                if (!string.IsNullOrEmpty(apiPath) && !string.IsNullOrEmpty(url))
                {
                    url = url.TrimEnd('/') + "/" + apiPath.Trim('/');
                }

                if (!string.IsNullOrEmpty(url)) indexer.Url = url;
                indexer.ApiKey = string.IsNullOrEmpty(apiKey) ? null : apiKey;
                indexer.Categories = categories ?? indexer.Categories;
                indexer.UpdatedAt = DateTime.UtcNow;

                await _indexerRepository.UpdateAsync(indexer);

                // After saving, ensure we dedupe any other entries with same normalized URL + ApiKey (concurrent upsert safety)
                try
                {
                    var normalizedUrl = NormalizeIndexerUrl(indexer.Url);
                    var apiKeyCompare = indexer.ApiKey ?? string.Empty;
                    await CleanupDuplicateIndexersAsync(normalizedUrl, apiKeyCompare);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger?.LogWarning(ex, "Failed to dedupe indexers after update for {Id}", indexer.Id);
                }

                // Notify clients (compute whether the created indexer still exists after dedupe to avoid duplicate notifications)
                try
                {
                    var stillExists = (await _indexerRepository.GetByIdAsync(indexer.Id)) != null;
                    var createdForBroadcast = (created && stillExists) ? 1 : 0;
                    await _hubBroadcaster.BroadcastAsync(RealtimeHubTarget.Settings, "IndexersUpdated", new { created = createdForBroadcast, skipped = 0, indexers = new[] { new { id = indexer.Id, name = indexer.Name, baseUrl = indexer.Url } } });

                    // Determine toast message. If the indexer was created very recently (by a prior POST or PUT),
                    // suppress an additional 'Updated' toast to avoid duplicate notifications for rapid import/update flows.
                    var toastMessage = createdForBroadcast == 1 ? $"Imported indexer from PUT: {indexer.Name}" : $"Updated indexer: {indexer.Name}";
                    var publishToast = true;
                    try
                    {
                        if (createdForBroadcast == 0 && indexer.CreatedAt != default && (DateTime.UtcNow - indexer.CreatedAt).TotalSeconds < NotificationSuppressionSeconds)
                        {
                            publishToast = false;
                            _logger?.LogDebug("Suppressing update toast for indexer {Id} since it was created recently", indexer.Id);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger?.LogDebug(ex, "Failed to evaluate recent-create toast suppression for Prowlarr indexer {Id}", indexer.Id);
                    }

                    if (publishToast)
                    {
                        // Further suppress toasts if a recent toast for this indexer was already sent OR the same message was recently sent globally
                        bool sendByIndexer = false;
                        bool sendByMessage = false;
                        try
                        {
                            sendByIndexer = ShouldSendToastForIndexer(indexer.Id, toastMessage);
                        }
                        catch (Exception caughtEx_4) when (caughtEx_4 is not OperationCanceledException && caughtEx_4 is not OutOfMemoryException && caughtEx_4 is not StackOverflowException) { sendByIndexer = true; }
                        try
                        {
                            sendByMessage = ShouldSendToastForMessage(toastMessage);
                        }
                        catch (Exception caughtEx_5) when (caughtEx_5 is not OperationCanceledException && caughtEx_5 is not OutOfMemoryException && caughtEx_5 is not StackOverflowException) { sendByMessage = true; }

                        _logger?.LogDebug("Toast suppression check for indexer {Id}: byIndexer={ByIndexer}, byMessage={ByMessage}", indexer.Id, sendByIndexer, sendByMessage);

                        if (sendByIndexer && sendByMessage)
                        {
                            await _toastService.PublishNotificationAsync("Indexers", toastMessage, icon: null, timeoutMs: 8000);
                        }
                        else
                        {
                            _logger?.LogDebug("Suppressing toast for indexer {Id} due to recent toast or duplicate message", indexer.Id);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger?.LogWarning(ex, "Failed to broadcast IndexersUpdated after update");
                }

                // Return updated DTO (consistent with GetIndexerById shape)
                var dto = new
                {
                    id = indexer.Id,
                    name = indexer.Name,
                    implementation = indexer.Implementation,
                    baseUrl = indexer.Url,
                    apiKey = indexer.ApiKey,
                    categories = string.IsNullOrEmpty(indexer.Categories) ? System.Array.Empty<string>() : indexer.Categories.Split(',').Select(s => s.Trim()).ToArray(),
                    settings = new
                    {
                        baseUrl = indexer.Url,
                        apiKey = indexer.ApiKey,
                        apiPath = string.Empty,
                        categories = string.IsNullOrEmpty(indexer.Categories) ? System.Array.Empty<string>() : indexer.Categories.Split(',').Select(s => s.Trim()).ToArray()
                    },
                    fields = new[]
                    {
                        new FieldDto("baseUrl", indexer.Url ?? string.Empty),
                        new FieldDto("apiKey", indexer.ApiKey ?? string.Empty),
                        new FieldDto("apiPath", string.Empty),
                        new FieldDto("categories", string.IsNullOrEmpty(indexer.Categories) ? System.Array.Empty<string>() : indexer.Categories.Split(',').Select(s => s.Trim()).ToArray())
                    },
                    tags = System.Array.Empty<int>()
                };

                if (created)
                {
                    return CreatedAtAction(nameof(GetIndexerById), new { id = indexer.Id }, dto);
                }

                return Ok(dto);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger?.LogError(ex, "Failed to update indexer {Id}", id);
                return StatusCode(500, new { error = "Failed to update indexer" });
            }
        }

        // Remove duplicate persisted indexers that share the same normalized URL + ApiKey.
        // Keeps the earliest created (lowest Id) and removes the rest.
        private async Task CleanupDuplicateIndexersAsync(string normalizedUrl, string apiKey)
        {
            try
            {
                var all = await _indexerRepository.GetAllAsync();
                var duplicates = all
                    .Where(i => NormalizeIndexerUrl(i.Url) == normalizedUrl && (i.ApiKey ?? string.Empty) == apiKey)
                    .OrderBy(i => i.Id)
                    .ToList();

                if (duplicates.Count <= 1) return;

                // Keep the first, remove the rest
                var remove = duplicates.Skip(1).ToList();

                _logger?.LogInformation("Dedupe: Removing {Count} duplicate indexer(s) for url={Url}", remove.Count, normalizedUrl);

                foreach (var r in remove)
                    await _indexerRepository.DeleteAsync(r.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger?.LogWarning(ex, "Failed to cleanup duplicate indexers for {Url}", normalizedUrl);
            }
        }

        /// <summary>
        /// POST /api/v1/indexers
        /// Accepts an array of indexers from Prowlarr. Expects a JSON array; returns 200 OK if received.
        /// </summary>
        [HttpPost("indexers")]
        [AllowAnonymous]
        [IgnoreAntiforgeryToken]
        [Produces("application/json")]
        public async Task<IActionResult> PostIndexers([FromBody] System.Text.Json.JsonElement payload)
        {
            _logger?.LogInformation("Prowlarr indexers payload received: {Kind}", payload.ValueKind.ToString());
            // Log raw request body (redacted) to aid debugging; truncate/sanitize sensitive values
            try
            {
                var raw = payload.GetRawText();
                var redacted = LogRedaction.RedactText(raw, LogRedaction.GetSensitiveValuesFromEnvironment());
                _logger?.LogInformation("Prowlarr indexers payload body: {Payload}", redacted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                System.Diagnostics.Debug.WriteLine($"ProwlarrCompatController payload logging failed (POST indexers): {ex.Message}");
            }

            if (HttpContext?.Response != null) HttpContext.Response.ContentType = "application/json";

            // Accept a single object payload as well as arrays. This keeps the endpoint
            // tolerant when callers post one indexer at a time.
            if (payload.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                return await PostIndexer(payload);
            }

            if (payload.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return BadRequest(new { message = "Expected JSON array of indexers" });
            }

            var created = 0;
            var skipped = 0;
            var createdIndexers = new List<Indexer>();
            var existingIndexers = await _indexerRepository.GetAllAsync();

            foreach (var item in payload.EnumerateArray().Where(item => item.ValueKind == System.Text.Json.JsonValueKind.Object))
            {
                // Extract common fields with tolerant mapping
                string getString(System.Text.Json.JsonElement el, string prop1, string? prop2 = null)
                {
                    if (el.TryGetProperty(prop1, out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String)
                        return p.GetString() ?? string.Empty;
                    if (prop2 != null && el.TryGetProperty(prop2, out var p2) && p2.ValueKind == System.Text.Json.JsonValueKind.String)
                        return p2.GetString() ?? string.Empty;
                    return string.Empty;
                }

                var name = getString(item, "name", "title");
                var implementation = getString(item, "implementation", "type");
                var baseUrl = getString(item, "baseUrl", "url");
                var apiPath = getString(item, "apiPath", null);
                var apiKey = getString(item, "apiKey", null);

                // categories can be array or string
                string? categories = null;
                if (item.TryGetProperty("categories", out var cats))
                {
                    if (cats.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        var parts = cats.EnumerateArray().Select(x => x.ValueKind == System.Text.Json.JsonValueKind.Number ? x.GetInt32().ToString() : x.GetString() ?? string.Empty).Where(s => !string.IsNullOrEmpty(s));
                        categories = string.Join(',', parts);
                    }
                    else if (cats.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        categories = cats.GetString();
                    }
                }

                if (!string.IsNullOrEmpty(apiPath) && !string.IsNullOrEmpty(baseUrl))
                {
                    baseUrl = baseUrl.TrimEnd('/') + "/" + apiPath.Trim('/');
                }

                // If baseUrl/apiKey/apiPath absent, try to look for a settings object with baseUrl/apiKey
                if (string.IsNullOrEmpty(baseUrl) && item.TryGetProperty("settings", out var settings) && settings.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    baseUrl = GetStringProperty(settings, "baseUrl", "url");
                    if (string.IsNullOrEmpty(apiKey)) apiKey = GetStringProperty(settings, "apiKey", "apikey");
                    if (string.IsNullOrEmpty(apiPath)) apiPath = GetStringProperty(settings, "apiPath", null);
                }

                // If still missing, try to extract from a "fields" array (Prowlarr sends baseUrl/apiKey/categories within fields)
                if ((string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiPath) || string.IsNullOrEmpty(categories)) &&
                    item.TryGetProperty("fields", out var fields) && fields.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var f in fields.EnumerateArray().Where(field => field.ValueKind == System.Text.Json.JsonValueKind.Object))
                    {
                        var fname = getString(f, "name", null);
                        if (string.IsNullOrEmpty(fname))
                            continue;

                        // baseUrl, apiKey, apiPath are strings inside field.value
                        if (string.IsNullOrEmpty(baseUrl) &&
                            fname.Equals("baseUrl", StringComparison.InvariantCultureIgnoreCase) &&
                            f.TryGetProperty("value", out var v) &&
                            v.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            baseUrl = v.GetString() ?? string.Empty;
                        }

                        if (string.IsNullOrEmpty(apiKey) &&
                            fname.Equals("apiKey", StringComparison.InvariantCultureIgnoreCase) &&
                            f.TryGetProperty("value", out v) &&
                            v.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            apiKey = v.GetString() ?? string.Empty;
                        }

                        if (string.IsNullOrEmpty(apiPath) &&
                            fname.Equals("apiPath", StringComparison.InvariantCultureIgnoreCase) &&
                            f.TryGetProperty("value", out v) &&
                            v.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            apiPath = v.GetString() ?? string.Empty;
                        }

                        if (string.IsNullOrEmpty(categories) && fname.Equals("categories", StringComparison.InvariantCultureIgnoreCase))
                        {
                            if (f.TryGetProperty("value", out var categoriesValue) && categoriesValue.ValueKind == System.Text.Json.JsonValueKind.Array)
                            {
                                var parts = categoriesValue.EnumerateArray().Select(x => x.ValueKind == System.Text.Json.JsonValueKind.Number ? x.GetInt32().ToString() : x.GetString() ?? string.Empty).Where(s => !string.IsNullOrEmpty(s));
                                categories = string.Join(',', parts);
                            }
                            else if (f.TryGetProperty("value", out var vs) && vs.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                categories = vs.GetString();
                            }
                        }

                        if (!string.IsNullOrEmpty(baseUrl) && !string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(apiPath) && !string.IsNullOrEmpty(categories))
                        {
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(name)) name = baseUrl ?? "Prowlarr Indexer";
                if (string.IsNullOrEmpty(implementation)) implementation = "Custom";

                // Normalize URLs
                var url = (baseUrl ?? string.Empty).Trim();

                // Deduplicate by normalized URL + ApiKey (normalizes trailing slash and trailing /api)
                var normalizedUrl = NormalizeIndexerUrl(url);
                var exists = existingIndexers.FirstOrDefault(i => NormalizeIndexerUrl(i.Url) == normalizedUrl && (i.ApiKey ?? string.Empty) == (apiKey ?? string.Empty));
                if (exists != null)
                {
                    skipped++;
                    _logger?.LogInformation("Prowlarr: Skipping existing indexer (name={Name}, url={Url}, apiKeyPresent={HasApiKey})", name, exists.Url, !string.IsNullOrEmpty(apiKey));
                    continue;
                }

                var indexer = new Indexer
                {
                    Name = name,
                    Implementation = implementation,
                    Url = url,
                    ApiKey = string.IsNullOrEmpty(apiKey) ? null : apiKey,
                    Categories = categories ?? string.Empty,
                    Tags = string.Empty,
                    AdditionalSettings = string.Empty,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    IsEnabled = true
                };

                // Guess Type from implementation
                var implLower = (implementation ?? string.Empty).ToLowerInvariant();
                indexer.Type = implLower.Contains("newznab") ? "Usenet" : (implLower.Contains("torznab") ? "Torrent" : "Custom");

                indexer = await _indexerRepository.AddAsync(indexer);
                existingIndexers.Add(indexer);
                created++;
                createdIndexers.Add(indexer);
                _logger?.LogInformation("Prowlarr: Created indexer (name={Name}, url={Url}, apiKeyPresent={HasApiKey})", indexer.Name, indexer.Url, !string.IsNullOrEmpty(indexer.ApiKey));
            }

            if (created > 0)
            {

                // Cleanup any duplicates caused by concurrent upserts (dedupe by normalized URL + ApiKey)
                foreach (var ci in createdIndexers.ToList())
                {
                    try
                    {
                        var normalizedUrl = NormalizeIndexerUrl(ci.Url);
                        var apiKey = ci.ApiKey ?? string.Empty;
                        await CleanupDuplicateIndexersAsync(normalizedUrl, apiKey);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger?.LogWarning(ex, "Failed to dedupe indexers for {Name}", ci.Name);
                    }
                }

                // Notify connected clients that indexers changed so the UI can refresh
                try
                {
                    var createdInfo = createdIndexers.Select(i => new { id = i.Id, name = i.Name, baseUrl = i.Url }).ToArray();

                    _logger?.LogInformation("Broadcasting IndexersUpdated to clients: created={Created}, skipped={Skipped}, indexerCount={Count}", created, skipped, createdInfo.Length);

                    await _hubBroadcaster.BroadcastAsync(RealtimeHubTarget.Settings, "IndexersUpdated", new { created, skipped, indexers = createdInfo });

                    _logger?.LogInformation("IndexersUpdated broadcast complete");

                    // Publish a toast + dropdown notification so the activity bell receives the update
                    try
                    {
                        var names = createdIndexers.Select(i => i.Name).ToArray();
                        var message = names.Length > 0 ? $"Imported {created} indexer(s): {string.Join(", ", names)}" : $"Imported {created} indexer(s) successfully";
                        if (ShouldSendToastForMessage(message))
                        {
                            await _toastService.PublishNotificationAsync("Indexers", message, icon: null, timeoutMs: 8000);
                        }
                        else
                        {
                            _logger?.LogDebug("Suppressing batch import toast due to recent identical message");
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger?.LogWarning(ex, "Failed to publish indexer import notification");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger?.LogWarning(ex, "Failed to broadcast IndexersUpdated to realtime clients");
                }
            }

            // Log a summary for diagnostics
            _logger?.LogInformation("Prowlarr: Indexers processed - created={Created}, skipped={Skipped}", created, skipped);

            // Include created indexers in the response (id will be populated after SaveChanges)
            var createdDtos = createdIndexers.Select(i => new
            {
                id = i.Id,
                name = i.Name,
                implementation = i.Implementation,
                baseUrl = i.Url,
                apiKey = i.ApiKey,
                categories = string.IsNullOrEmpty(i.Categories) ? System.Array.Empty<string>() : i.Categories.Split(',').Select(s => s.Trim()).ToArray(),
                settings = new
                {
                    baseUrl = i.Url,
                    apiKey = i.ApiKey,
                    apiPath = string.Empty,
                    categories = string.IsNullOrEmpty(i.Categories) ? System.Array.Empty<string>() : i.Categories.Split(',').Select(s => s.Trim()).ToArray()
                },
                fields = new object[]
                {
                    new FieldDto("baseUrl", i.Url ?? string.Empty),
                    new FieldDto("apiKey", i.ApiKey ?? string.Empty),
                    new FieldDto("apiPath", string.Empty),
                    new FieldDto("categories", string.IsNullOrEmpty(i.Categories) ? System.Array.Empty<string>() : i.Categories.Split(',').Select(s => s.Trim()).ToArray())
                }
            }).ToArray();



            return Ok(new { accepted = true, created, skipped, indexers = createdDtos });
        }

        /// <summary>
        /// DEBUG: POST /api/v1/debug/indexers/publish
        /// Manually trigger an IndexersUpdated realtime broadcast for testing client connectivity.
        /// </summary>
        [HttpPost("debug/indexers/publish")]
        [AllowAnonymous]
        [LocalOrAdmin]
        [ApiExplorerSettings(IgnoreApi = true)]
        public async Task<IActionResult> DebugPublishIndexers([FromBody] System.Text.Json.JsonElement? payload)
        {
            // Build a small payload from optional incoming body or a default sample
            var created = 0;
            var indexers = new List<object>();

            if (payload.HasValue && payload.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                try
                {
                    if (payload.Value.TryGetProperty("indexers", out var idxs) && idxs.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var i in idxs.EnumerateArray())
                        {
                            var id = i.TryGetProperty("id", out var pid) && pid.ValueKind == System.Text.Json.JsonValueKind.Number ? pid.GetInt32() : 0;
                            var name = i.TryGetProperty("name", out var pname) && pname.ValueKind == System.Text.Json.JsonValueKind.String ? pname.GetString() ?? string.Empty : string.Empty;
                            var baseUrl = i.TryGetProperty("baseUrl", out var pbase) && pbase.ValueKind == System.Text.Json.JsonValueKind.String ? pbase.GetString() ?? string.Empty : string.Empty;

                            indexers.Add(new { id, name, baseUrl });
                        }

                        created = indexers.Count;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger?.LogDebug(ex, "Failed parsing debug indexers publish payload");
                }
            }

            if (!indexers.Any())
            {
                created = 1;
                indexers.Add(new { id = 999999, name = "Debug Indexer", baseUrl = "http://debug" });
            }

            _logger?.LogInformation("DEBUG: Broadcasting IndexersUpdated (manual test): created={Created}", created);

            await _hubBroadcaster.BroadcastAsync(RealtimeHubTarget.Settings, "IndexersUpdated", new { created, skipped = 0, indexers });

            _logger?.LogInformation("DEBUG: IndexersUpdated broadcast sent");

            // Also publish a toast/notification to show up in the activity dropdown
            try
            {
                var names = indexers.Select(i => i.GetType().GetProperty("name")?.GetValue(i)?.ToString() ?? string.Empty).Where(s => !string.IsNullOrEmpty(s)).ToArray();
                var message = names.Length > 0 ? $"Imported {created} indexer(s): {string.Join(", ", names)}" : $"Imported {created} indexer(s) successfully";
                await _toastService.PublishNotificationAsync("Indexers", message, icon: null, timeoutMs: 8000);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger?.LogWarning(ex, "Failed to publish debug indexer notification");
            }

            return Ok(new { sent = true, created, indexers });
        }

        /// <summary>
        /// DEBUG: GET /api/v1/debug/settings/clients
        /// Returns the list and count of currently connected settings realtime clients.
        /// </summary>
        [HttpGet("debug/settings/clients")]
        [AllowAnonymous]
        [LocalOrAdmin]
        [ApiExplorerSettings(IgnoreApi = true)]
        public IActionResult GetSettingsRealtimeClients()
        {
            try
            {
                var clients = _realtimeClientRegistry.GetSettingsClientIds();
                return Ok(new { connected = clients.Count, clients });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger?.LogWarning(ex, "Failed to retrieve settings realtime clients");
                return StatusCode(500, new { error = "Failed to retrieve clients" });
            }
        }

        /// <summary>
        /// POST /api/v1/indexer
        /// Accepts a single indexer object (or an array) for compatibility with some clients that POST to the singular route.
        /// Delegates to PostIndexers for the actual processing so persistence and realtime broadcasts happen in one place.
        /// </summary>
        [HttpPost("indexer")]
        [AllowAnonymous]
        [IgnoreAntiforgeryToken]
        [Produces("application/json")]
        public async Task<IActionResult> PostIndexer([FromBody] System.Text.Json.JsonElement payload)
        {
            _logger?.LogInformation("Prowlarr indexer payload (single) received: {Kind}", payload.ValueKind.ToString());
            try
            {
                var raw = payload.GetRawText();
                var redacted = LogRedaction.RedactText(raw, LogRedaction.GetSensitiveValuesFromEnvironment());
                _logger?.LogInformation("Prowlarr indexer payload body: {Payload}", redacted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                System.Diagnostics.Debug.WriteLine($"ProwlarrCompatController payload logging failed (single indexer POST): {ex.Message}");
            }

            if (HttpContext?.Response != null) HttpContext.Response.ContentType = "application/json";

            if (payload.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                // Wrap single object into an array and delegate to existing handler (preserve original PostIndexers response shape)
                var arrJson = "[" + payload.GetRawText() + "]";
                using var doc = System.Text.Json.JsonDocument.Parse(arrJson);
                var res = await PostIndexers(doc.RootElement);
                return res;
            }

            if (payload.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                return await PostIndexers(payload);
            }

            return BadRequest(new { message = "Expected JSON object or array for indexer(s)" });
        }

        /// <summary>
        /// GET /api/v1/indexer/schema
        /// Returns a minimal list of indexer fields / schema entries.
        /// </summary>
        [HttpGet("indexer/schema")]
        [AllowAnonymous]
        [Produces("application/json")]
        public IActionResult GetIndexerSchema()
        {
            Response.ContentType = "application/json";

            var fields = new[]
            {
                new IndexerFieldDto { Name = "name", Type = "string", Required = true, Description = "Indexer name" },
                new IndexerFieldDto { Name = "baseUrl", Type = "string", Required = true, Description = "Base URL of indexer" },
                new IndexerFieldDto { Name = "apiPath", Type = "string", Required = true, Description = "API path (e.g. /api or /torznab)" },
                new IndexerFieldDto { Name = "apiKey", Type = "string", Required = false, Description = "API key or token" },
                new IndexerFieldDto { Name = "categories", Type = "array", Required = false, Description = "Optional categories filter (array of integers or strings)" }
            };

            // Return an array of schema entries, one per supported implementation (Prowlarr expects a JSON array here)
            var schemaArray = new[]
            {
                new { fields = fields, implementation = "Newznab" },
                new { fields = fields, implementation = "Torznab" }
            };

            return Ok(schemaArray);
        }

        /// <summary>
        /// GET /api/v1/indexers/schema
        /// Backwards-compatible plural route to return the same minimal schema array as /api/v1/indexer/schema.
        /// </summary>
        [HttpGet("indexers/schema")]
        [AllowAnonymous]
        [Produces("application/json")]
        public IActionResult GetIndexersSchema()
        {
            // Delegate to singular route handler to avoid duplication and ensure consistent output
            return GetIndexerSchema();
        }

        private static string NormalizeIndexerUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return string.Empty;

            try
            {
                var uri = new Uri(url);
                var path = uri.AbsolutePath ?? string.Empty;
                // Trim trailing slash
                path = path.TrimEnd('/');

                // Remove trailing /api if present
                if (path.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
                {
                    path = path.Substring(0, path.Length - 4);
                }

                var port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port;
                var normalized = $"{uri.Scheme}://{uri.Host}{port}{path}";
                return normalized.TrimEnd('/');
            }
            catch (Exception caughtEx_6) when (caughtEx_6 is not OperationCanceledException && caughtEx_6 is not OutOfMemoryException && caughtEx_6 is not StackOverflowException)
            {
                return url.TrimEnd('/');
            }
        }

        // Helper to read tolerant string properties from a JSON element
        private static string GetStringProperty(System.Text.Json.JsonElement el, string prop1, string? prop2 = null)
        {
            if (el.ValueKind != System.Text.Json.JsonValueKind.Object) return string.Empty;

            if (el.TryGetProperty(prop1, out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String)
                return p.GetString() ?? string.Empty;
            if (prop2 != null && el.TryGetProperty(prop2, out var p2) && p2.ValueKind == System.Text.Json.JsonValueKind.String)
                return p2.GetString() ?? string.Empty;
            return string.Empty;
        }

        // Helper to parse categories property (array or string) into a comma-separated string
        private static string? ParseCategories(System.Text.Json.JsonElement el)
        {
            if (el.TryGetProperty("categories", out var cats))
            {
                if (cats.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var parts = cats.EnumerateArray().Select(x => x.ValueKind == System.Text.Json.JsonValueKind.Number ? x.GetInt32().ToString() : x.GetString() ?? string.Empty).Where(s => !string.IsNullOrEmpty(s));
                    return string.Join(',', parts);
                }
                else if (cats.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return cats.GetString();
                }
            }

            return null;
        }

        // DTOs
        public record SystemStatusDto
        {
            public string Status { get; init; } = string.Empty;
            public string Version { get; init; } = string.Empty;
            public string Api { get; init; } = string.Empty;
        }

        public record IndexerTestResponseDto
        {
            public bool Success { get; init; }
            public string Message { get; init; } = string.Empty;
            public string Version { get; init; } = string.Empty;
        }

        public record IndexerSchemaDto
        {
            public IndexerFieldDto[] Fields { get; init; } = System.Array.Empty<IndexerFieldDto>();
            /// <summary>
            /// The implementations supported by this indexer schema (Prowlarr expects at least one of 'Newznab' or 'Torznab').
            /// </summary>
            public string[] Implementations { get; init; } = new[] { "Newznab", "Torznab" };
        }

        public record IndexerFieldDto
        {
            public string Name { get; init; } = string.Empty;
            public string Type { get; init; } = string.Empty;
            public bool Required { get; init; }
            public string Description { get; init; } = string.Empty;
        }

        // Simple field DTO to match Listenarr/Prowlarr field shape (Name/Value)
        private record FieldDto(string Name, object? Value);
    }
}
