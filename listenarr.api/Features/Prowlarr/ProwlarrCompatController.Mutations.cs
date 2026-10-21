/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Listenarr.Api.Attributes;

namespace Listenarr.Api.Features.Prowlarr
{
    public partial class ProwlarrCompatController : ControllerBase
    {
        /// <summary>
        /// Update an existing indexer by id.
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
                    // Nothing is logged here: the call that failed is the logging call itself.
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
                // Nothing is logged here: the call that failed is the logging call itself.
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
                // Nothing is logged here: the call that failed is the logging call itself.
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
