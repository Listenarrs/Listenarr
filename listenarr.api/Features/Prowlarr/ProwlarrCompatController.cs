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
using Listenarr.Api.Attributes;

namespace Listenarr.Api.Features.Prowlarr
{
    [ApiController]
    [Route("api/v1/prowlarr")]
    [Tags("Prowlarr Compatibility")]
    [ApiExplorerSettings(IgnoreApi = true)]
    [RequireApiKey]
    public partial class ProwlarrCompatController : ControllerBase
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
    }
}
