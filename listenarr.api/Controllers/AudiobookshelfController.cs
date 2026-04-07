using System.Threading;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Listenarr.Api.Services;
using Listenarr.Application.Services;

namespace listenarr.api.Controllers
{
    [ApiController]
    [Route("api/v1/audiobookshelf")]
    [Tags("Audiobookshelf")]
    public class AudiobookshelfController : ControllerBase
    {
        private readonly IAudiobookshelfService _audiobookshelfService;
        private readonly IConfigurationService _configurationService;
        private readonly ILogger<AudiobookshelfController> _logger;

        public AudiobookshelfController(
            IAudiobookshelfService audiobookshelfService,
            IConfigurationService configurationService,
            ILogger<AudiobookshelfController> logger)
        {
            _audiobookshelfService = audiobookshelfService;
            _configurationService = configurationService;
            _logger = logger;
        }

        /// <summary>
        /// Test connection to Audiobookshelf
        /// </summary>
        [HttpPost("test")]
        public async Task<IActionResult> TestConnection(CancellationToken ct)
        {
            var (success, message) = await _audiobookshelfService.TestConnectionAsync(ct);

            return Ok(new
            {
                success,
                message
            });
        }

        /// <summary>
        /// Get available Audiobookshelf libraries
        /// </summary>
        [HttpGet("libraries")]
        public async Task<IActionResult> GetLibraries(CancellationToken ct)
        {
            var libraries = await _audiobookshelfService.GetLibrariesAsync(ct);

            return Ok(libraries);
        }

        [HttpGet("libraries/{libraryId}/items")]
        public async Task<IActionResult> GetLibraryItems(string libraryId, CancellationToken ct)
        {
            var items = await _audiobookshelfService.GetLibraryItemsAsync(libraryId, ct);
            return Ok(items);
        }

        /// <summary>
        /// Trigger a scan on a library
        /// </summary>
        [HttpPost("scan")]
        public async Task<IActionResult> TriggerScan(
            [FromBody] AudiobookshelfScanRequest request,
            CancellationToken ct)
        {
            var settings = await _configurationService.GetApplicationSettingsAsync();

            var libraryId = request?.LibraryId;

            // fallback to saved setting if not provided
            if (string.IsNullOrWhiteSpace(libraryId))
            {
                libraryId = settings.AudiobookshelfLibraryId;
            }

            if (string.IsNullOrWhiteSpace(libraryId))
            {
                return BadRequest(new
                {
                    success = false,
                    message = "No library ID provided or configured"
                });
            }

            var (success, message) = await _audiobookshelfService
                .TriggerLibraryScanAsync(libraryId, ct);

            return Ok(new
            {
                success,
                message
            });
        }

        [HttpPost("import/preview")]
        public async Task<IActionResult> PreviewImport(
            [FromServices] IAudiobookshelfImportService importService,
            [FromBody] AudiobookshelfImportPreviewRequest request,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request.LibraryId))
                return BadRequest(new { error = "LibraryId is required" });

            var preview = await importService.PreviewImportAsync(request.LibraryId, ct);
            return Ok(preview);
        }

        [HttpPost("import")]
        public async Task<IActionResult> ImportItems(
            [FromServices] IAudiobookshelfImportService importService,
            [FromBody] AudiobookshelfImportRequest request,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request.LibraryId))
                return BadRequest(new { error = "LibraryId is required" });

            var result = await importService.ImportAsync(new AudiobookshelfImportRequestDto
            {
                LibraryId = request.LibraryId,
                ItemIds = request.ItemIds ?? new List<string>(),
                QualityProfileId = request.QualityProfileId,
                Monitored = request.Monitored,
                SkipExisting = request.SkipExisting
            }, ct);

            return Ok(result);
        }
    }

    public class AudiobookshelfScanRequest
    {
        public string? LibraryId { get; set; }
    }

    public class AudiobookshelfImportPreviewRequest
    {
        public string LibraryId { get; set; } = string.Empty;
    }

    public class AudiobookshelfImportRequest
    {
        public string LibraryId { get; set; } = string.Empty;
        public List<string>? ItemIds { get; set; }
        public int? QualityProfileId { get; set; }
        public bool Monitored { get; set; } = true;
        public bool SkipExisting { get; set; } = true;
    }
}