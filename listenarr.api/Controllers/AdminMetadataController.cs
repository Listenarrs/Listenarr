using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;

namespace Listenarr.Api.Controllers
{
    [ApiController]
    [Route("api/admin")]
    public class AdminMetadataController : ControllerBase
    {
        private readonly ListenArrDbContext _db;
        private readonly IAudioFileService _audioFileService;
        private readonly IServiceScopeFactory _scopeFactory;

        private readonly IStartupConfigService _startupConfigService;
        private readonly Microsoft.AspNetCore.Antiforgery.IAntiforgery _antiforgery;
        private readonly IMetadataService _metadataService;

        public AdminMetadataController(ListenArrDbContext db, IAudioFileService audioFileService, IServiceScopeFactory scopeFactory, IStartupConfigService startupConfigService, Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery, IMetadataService metadataService)
        {
            _db = db;
            _audioFileService = audioFileService;
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _startupConfigService = startupConfigService;
            _antiforgery = antiforgery;
            _metadataService = metadataService;
        }

        // POST /api/admin/reextract-file/123
        [HttpPost("reextract-file/{audiobookFileId}")]
        public async Task<IActionResult> ReextractFile(int audiobookFileId)
        {
            // Validate antiforgery token; middleware also enforces CSRF but validate here for clearer message
            try
            {
                await _antiforgery.ValidateRequestAsync(HttpContext);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                return BadRequest(new { message = "Invalid or missing CSRF token", detail = ex.Message });
            }
            // Load the tracked entity so we can update it
            var file = await _db.AudiobookFiles
                .FirstOrDefaultAsync(f => f.Id == audiobookFileId);

            if (file == null)
                return NotFound(new { message = "AudiobookFile not found" });

            // Ensure path is non-null
            var path = file.Path ?? string.Empty;
            if (string.IsNullOrWhiteSpace(path))
                return BadRequest(new { message = "AudiobookFile has no path" });

            // Extract metadata using the metadata service
            AudioMetadata? meta = null;
            try
            {
                meta = await _metadataService.ExtractFileMetadataAsync(path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                return StatusCode(500, new { message = "Metadata extraction failed", detail = ex.Message });
            }

            // Update the existing AudiobookFile row with extracted metadata
            var fi = new System.IO.FileInfo(path);
            file.Size = fi.Exists ? fi.Length : file.Size;
            file.DurationSeconds = meta?.Duration.TotalSeconds ?? file.DurationSeconds;
            file.Format = string.IsNullOrEmpty(meta?.Format) ? file.Format : meta?.Format;
            file.Bitrate = (meta?.Bitrate != 0) ? meta?.Bitrate : file.Bitrate;
            file.SampleRate = meta?.SampleRate ?? file.SampleRate;
            file.Channels = meta?.Channels ?? file.Channels;

            await _db.SaveChangesAsync();

            return Ok(new { message = "Re-extraction completed", audiobookFileId = audiobookFileId });
        }

        // POST /api/admin/trigger-rescan
        [HttpPost("trigger-rescan")]
        public async Task<IActionResult> TriggerRescan([FromQuery] int max = 50)
        {
            try
            {
                await _antiforgery.ValidateRequestAsync(HttpContext);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                return BadRequest(new { message = "Invalid or missing CSRF token", detail = ex.Message });
            }

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();
            var metadataService = scope.ServiceProvider.GetRequiredService<IMetadataService>();

            var candidates = await db.AudiobookFiles
                .Where(f => f.DurationSeconds == null || f.Format == null || f.SampleRate == null)
                .Take(max)
                .ToListAsync();

            var updated = 0;
            foreach (var f in candidates)
            {
                try
                {
                    var meta = await metadataService.ExtractFileMetadataAsync(f.Path ?? string.Empty);
                    if (meta != null)
                    {
                        var fi = new System.IO.FileInfo(f.Path ?? string.Empty);
                        f.Size = fi.Exists ? fi.Length : f.Size;
                        f.DurationSeconds = meta.Duration.TotalSeconds != 0 ? meta.Duration.TotalSeconds : f.DurationSeconds;
                        f.Format = !string.IsNullOrEmpty(meta.Format) ? meta.Format : f.Format;
                        f.Bitrate = meta.Bitrate != 0 ? meta.Bitrate : f.Bitrate;
                        f.SampleRate = meta.SampleRate != 0 ? meta.SampleRate : f.SampleRate;
                        f.Channels = meta.Channels != 0 ? meta.Channels : f.Channels;
                        updated++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    // log and continue
                    var logger = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AdminMetadataController>>();
                    logger.LogWarning(ex, "Failed to re-extract for file id={Id} path={Path}", f.Id, f.Path);
                }
            }

            await db.SaveChangesAsync();
            return Ok(new { message = "Triggered rescan", examined = candidates.Count, updated });
        }
    }
}


