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
        private readonly ProwlarrIndexerUpsertWorkflow _indexerUpsertWorkflow;
        private readonly ProwlarrIndexerNotificationWorkflow _indexerNotificationWorkflow;

        // Preserve the existing private reflection seam used by controller tests to reset toast state.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, DateTime> _lastToastTimes = ProwlarrToastThrottler.LastToastTimes;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastToastMessages = ProwlarrToastThrottler.LastToastMessages;

        public ProwlarrCompatController(
            ILogger<ProwlarrCompatController> logger,
            IIndexerRepository indexerRepository,
            IHubBroadcaster hubBroadcaster,
            IRealtimeClientRegistry realtimeClientRegistry,
            IToastService toastService,
            IStartupConfigService startupConfigService,
            IApplicationVersionService applicationVersionService,
            ProwlarrIndexerUpsertWorkflow? indexerUpsertWorkflow = null,
            ProwlarrIndexerNotificationWorkflow? indexerNotificationWorkflow = null)
        {
            _logger = logger;
            _indexerRepository = indexerRepository;
            _hubBroadcaster = hubBroadcaster;
            _realtimeClientRegistry = realtimeClientRegistry;
            _toastService = toastService;
            _startupConfigService = startupConfigService;
            _applicationVersionService = applicationVersionService;
            _indexerUpsertWorkflow = indexerUpsertWorkflow ?? new ProwlarrIndexerUpsertWorkflow(
                indexerRepository,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ProwlarrIndexerUpsertWorkflow>.Instance);
            _indexerNotificationWorkflow = indexerNotificationWorkflow ?? new ProwlarrIndexerNotificationWorkflow(
                hubBroadcaster,
                toastService,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ProwlarrIndexerNotificationWorkflow>.Instance);
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
                .Select(i => ProwlarrCompatIndexerResponseBuilder.BuildReadIndexer(i, authEnabled))
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
                return Ok(ProwlarrCompatIndexerResponseBuilder.BuildFallbackIndexer(id));
            }
            var dto = ProwlarrCompatIndexerResponseBuilder.BuildReadIndexer(i, authEnabled);
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
            return Ok(ProwlarrCompatSchemaBuilder.BuildInfo());
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
                    await _indexerNotificationWorkflow.NotifyDeletedAsync(i);
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

                if (payload.ValueKind != System.Text.Json.JsonValueKind.Object)
                {
                    return BadRequest(new { message = "Expected JSON object for indexer update" });
                }

                var upsertResult = await _indexerUpsertWorkflow.UpsertFromPutAsync(
                    id,
                    ProwlarrIndexerPayloadReader.ParseForPut(payload));
                var indexer = upsertResult.Indexer;
                var created = upsertResult.Created;

                // Notify clients (compute whether the created indexer still exists after dedupe to avoid duplicate notifications)
                var createdForBroadcast = (created && upsertResult.StillExists) ? 1 : 0;
                await _indexerNotificationWorkflow.NotifyPutAsync(indexer, createdForBroadcast);

                // Return updated DTO (consistent with GetIndexerById shape)
                var dto = ProwlarrCompatIndexerResponseBuilder.BuildSavedIndexer(indexer);

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

            var importResult = await _indexerUpsertWorkflow.ImportManyAsync(
                payload.EnumerateArray()
                    .Where(item => item.ValueKind == System.Text.Json.JsonValueKind.Object)
                    .Select(ProwlarrIndexerPayloadReader.ParseForBulkPost));
            var created = importResult.Created;
            var skipped = importResult.Skipped;
            var createdIndexers = importResult.CreatedIndexers;
            foreach (var createdIndexer in createdIndexers)
            {
                _logger?.LogInformation("Prowlarr: Created indexer (name={Name}, url={Url}, apiKeyPresent={HasApiKey})", createdIndexer.Name, createdIndexer.Url, !string.IsNullOrEmpty(createdIndexer.ApiKey));
            }

            await _indexerNotificationWorkflow.NotifyImportedAsync(created, skipped, createdIndexers);

            // Log a summary for diagnostics
            _logger?.LogInformation("Prowlarr: Indexers processed - created={Created}, skipped={Skipped}", created, skipped);

            // Include created indexers in the response (id will be populated after SaveChanges)
            var createdDtos = createdIndexers
                .Select(ProwlarrCompatIndexerResponseBuilder.BuildSavedIndexer)
                .ToArray();

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

            await _indexerNotificationWorkflow.NotifyDebugIndexersAsync(created, indexers);

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
            return Ok(ProwlarrCompatSchemaBuilder.BuildSchema());
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
    }
}
