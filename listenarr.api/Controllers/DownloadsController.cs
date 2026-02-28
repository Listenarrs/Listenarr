using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Listenarr.Domain.Models;
using Listenarr.Api.Services;
using System.Linq;
using Listenarr.Infrastructure.Models;
using Microsoft.Extensions.Caching.Memory;

namespace Listenarr.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DownloadsController : ControllerBase
{
    private readonly ListenArrDbContext _dbContext;
    private readonly ILogger<DownloadsController> _logger;
    private readonly IConfigurationService _configurationService;
    private readonly IMemoryCache? _cache;

    public DownloadsController(ListenArrDbContext dbContext, ILogger<DownloadsController> logger, IConfigurationService configurationService, IMemoryCache? cache = null)
    {
        _dbContext = dbContext;
        _logger = logger;
        _configurationService = configurationService;
        _cache = cache;
    }
    /// <summary>
    /// Retrieve cached torrent bytes (if cached) for a given download id (synchronous for tests)
    /// </summary>
    [NonAction]
    public IActionResult GetCachedTorrent(string downloadId)
    {
        if (_cache == null)
        {
            return NotFound(new { error = "Cached torrent not found", downloadId });
        }

        if (_cache.TryGetValue($"mam:cachedtorrent:{downloadId}:bytes", out byte[]? bytes) && bytes != null && bytes.Length > 0)
        {
            var fileName = _cache.Get<string>($"mam:cachedtorrent:{downloadId}:name") ?? "download.torrent";
            return new FileContentResult(bytes, "application/x-bittorrent") { FileDownloadName = fileName };
        }

        return NotFound(new { error = "Cached torrent not found", downloadId });
    }

    /// <summary>
    /// Retrieve cached announce URLs (sync for tests)
    /// </summary>
    [NonAction]
    public IActionResult GetCachedAnnounces(string downloadId)
    {
        if (_cache == null)
            return NotFound(new { error = "Cached announces not found", downloadId });

        if (_cache.TryGetValue($"mam:cachedtorrent:{downloadId}:announces", out System.Collections.Generic.List<string>? announces) && announces != null && announces.Count > 0)
        {
            return Ok(new { downloadId, announces });
        }

        return NotFound(new { error = "Cached announces not found", downloadId });
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Download>>> GetDownloads([FromQuery] string? status = null)
    {
        try
        {
            IQueryable<Download> query = _dbContext.Downloads;

            if (!string.IsNullOrEmpty(status))
            {
                if (Enum.TryParse<DownloadStatus>(status, true, out var parsedStatus))
                {
                    query = query.Where(d => d.Status == parsedStatus);
                }
            }

            var downloads = await query
                .OrderByDescending(d => d.StartedAt)
                .ToListAsync();

            var enhancedDownloads = await EnhanceDownloadsWithClientNames(downloads);

            _logger.LogInformation("Retrieved {Count} downloads", downloads.Count);
            return Ok(enhancedDownloads);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
            _logger.LogError(ex, "Error retrieving downloads");
            return StatusCode(500, new { error = "Failed to retrieve downloads", message = ex.Message });
        }
    }

    /// <summary>
    /// Get a specific download by ID
    /// </summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<Download>> GetDownload(string id)
    {
        try
        {
            var download = await _dbContext.Downloads.FindAsync(id);

            if (download == null)
            {
                return NotFound(new { error = "Download not found", id });
            }

            // Remove downloadPath before returning to client
            var downloadObj = new
            {
                id = download.Id,
                audiobookId = download.AudiobookId,
                title = download.Title,
                artist = download.Artist,
                album = download.Album,
                originalUrl = download.OriginalUrl,
                status = download.Status.ToString(),
                progress = download.Progress,
                totalSize = download.TotalSize,
                downloadedSize = download.DownloadedSize,
                finalPath = download.FinalPath,
                startedAt = download.StartedAt,
                completedAt = download.CompletedAt,
                errorMessage = download.ErrorMessage,
                downloadClientId = download.DownloadClientId,
                metadata = download.Metadata
            };

            return Ok(downloadObj);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
            _logger.LogError(ex, "Error retrieving download {DownloadId}", id);
            return StatusCode(500, new { error = "Failed to retrieve download", message = ex.Message });
        }
      }

    /// <summary>
    /// Get active downloads (Queued or Downloading status)
    /// </summary>
    [HttpGet("active")]
    public async Task<ActionResult<IEnumerable<Download>>> GetActiveDownloads()
    {
        try
        {
            var activeDownloads = await _dbContext.Downloads
                .Where(d => d.Status == DownloadStatus.Queued ||
                           d.Status == DownloadStatus.Downloading ||
                           d.Status == DownloadStatus.Processing)
                .OrderByDescending(d => d.StartedAt)
                .ToListAsync();

            var enhancedActiveDownloads = await EnhanceDownloadsWithClientNames(activeDownloads);

            _logger.LogInformation("Retrieved {Count} active downloads", activeDownloads.Count);
            return Ok(enhancedActiveDownloads);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
            _logger.LogError(ex, "Error retrieving active downloads");
            return StatusCode(500, new { error = "Failed to retrieve active downloads", message = ex.Message });
        }
    }


    /// <summary>    /// Delete a download record (does not cancel active download)
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<ActionResult> DeleteDownload(string id)
    {
        try
        {
            var download = await _dbContext.Downloads.FindAsync(id);

            if (download == null)
            {
                return NotFound(new { error = "Download not found", id });
            }

            _dbContext.Downloads.Remove(download);
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Deleted download record {DownloadId}", id);
            return Ok(new { message = "Download deleted successfully", id });
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
            _logger.LogError(ex, "Error deleting download {DownloadId}", id);
            return StatusCode(500, new { error = "Failed to delete download", message = ex.Message });
        }
    }

    /// <summary>
    /// Clear completed downloads
    /// </summary>
    [HttpDelete("completed")]
    public async Task<ActionResult> ClearCompletedDownloads()
    {
        try
        {
            var completedDownloads = await _dbContext.Downloads
                .Where(d => d.Status == DownloadStatus.Completed)
                .ToListAsync();

            _dbContext.Downloads.RemoveRange(completedDownloads);
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Cleared {Count} completed downloads", completedDownloads.Count);
            return Ok(new { message = "Completed downloads cleared", count = completedDownloads.Count });
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
            _logger.LogError(ex, "Error clearing completed downloads");
            return StatusCode(500, new { error = "Failed to clear completed downloads", message = ex.Message });
        }
    }

    /// <summary>
    /// Clear failed downloads
    /// </summary>
    [HttpDelete("failed")]
    public async Task<ActionResult> ClearFailedDownloads()
    {
        try
        {
            var failedDownloads = await _dbContext.Downloads
                .Where(d => d.Status == DownloadStatus.Failed)
                .ToListAsync();

            _dbContext.Downloads.RemoveRange(failedDownloads);
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Cleared {Count} failed downloads", failedDownloads.Count);
            return Ok(new { message = "Failed downloads cleared", count = failedDownloads.Count });
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
            _logger.LogError(ex, "Error clearing failed downloads");
            return StatusCode(500, new { error = "Failed to clear failed downloads", message = ex.Message });
        }
    }

    /// <summary>
    /// Enhance downloads with resolved client names
    /// </summary>
    private async Task<List<object>> EnhanceDownloadsWithClientNames(List<Download> downloads)
    {
        var downloadClients = await _configurationService.GetDownloadClientConfigurationsAsync();
        var clientLookup = downloadClients.ToDictionary(c => c.Id, c => c.Name);

        return downloads.Select(d =>
        {
            // Remove any client-local content path information before returning to the frontend.
            // Server keeps `DownloadPath`/metadata internally for mapping/monitoring, but must not transmit
            // client-local paths (for example ClientContentPath) to user browsers.
            object? sanitizedMetadata = null;
            if (d.Metadata != null)
            {
                var dict = new Dictionary<string, object>();
                foreach (var kvp in d.Metadata)
                {
                    if (!string.Equals(kvp.Key, "ClientContentPath", StringComparison.OrdinalIgnoreCase))
                    {
                        dict[kvp.Key] = kvp.Value!;
                    }
                }
                sanitizedMetadata = dict;
            }

            return new
            {
                id = d.Id,
                audiobookId = d.AudiobookId,
                title = d.Title,
                artist = d.Artist,
                album = d.Album,
                originalUrl = d.OriginalUrl,
                status = d.Status.ToString(),
                progress = d.Progress,
                totalSize = d.TotalSize,
                downloadedSize = d.DownloadedSize,
                finalPath = d.FinalPath,
                startedAt = d.StartedAt,
                completedAt = d.CompletedAt,
                errorMessage = d.ErrorMessage,
                downloadClientId = d.DownloadClientId,
                downloadClientName = d.DownloadClientId == "DDL" ? "Direct Download" :
                                   clientLookup.TryGetValue(d.DownloadClientId, out var clientName) ? clientName : "Unknown Client",
                metadata = sanitizedMetadata,
                // Sprint 2: Error handling and import blocking fields
                importBlockReason = d.ImportBlockReason,
                importBlockMessages = d.ImportBlockMessages,
                importAttempts = d.ImportAttempts
            };
        }).Cast<object>().ToList();
    }
}


