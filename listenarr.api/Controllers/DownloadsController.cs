using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Listenarr.Domain.Models;
using Listenarr.Api.Services;
using System.Linq;
using Listenarr.Infrastructure.Models;
using Microsoft.Extensions.Caching.Memory;

namespace Listenarr.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/downloads")]
[Tags("Downloads")]
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

    /// <summary>
    /// List all download records, optionally filtered by status.
    /// </summary>
    /// <param name="status">Optional status filter (e.g., Queued, Downloading, Completed, Failed).</param>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Download>>> GetDownloads([FromQuery] string? status = null)
    {
        try
        {
            IQueryable<Download> query = _dbContext.Downloads;

            var downloadClients = await _configurationService.GetDownloadClientConfigurationsAsync();
            var enabledClientIds = downloadClients
                .Where(c => c.IsEnabled && !string.IsNullOrWhiteSpace(c.Id))
                .Select(c => c.Id)
                .ToList();

            query = query.Where(d =>
                d.DownloadClientId == "DDL" ||
                (!string.IsNullOrEmpty(d.DownloadClientId) && enabledClientIds.Contains(d.DownloadClientId)));

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
    /// Get a specific download record by ID.
    /// </summary>
    /// <param name="id">Download record ID.</param>
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
                metadata = download.Metadata,
                importBlockReason = download.ImportBlockReason,
                importBlockMessages = download.ImportBlockMessages,
                importAttempts = download.ImportAttempts
            };

            return Ok(downloadObj);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
            _logger.LogError(ex, "Error retrieving download {DownloadId}", id);
            return StatusCode(500, new { error = "Failed to retrieve download", message = ex.Message });
        }
      }

    /// <summary>
    /// Retry importing a download that was blocked due to import issues. Resets status to ImportPending.
    /// </summary>
    /// <param name="id">Download record ID.</param>
    [HttpPost("{id}/retry-import")]
    public async Task<ActionResult> RetryBlockedImport(string id)
    {
        try
        {
            var download = await _dbContext.Downloads.FindAsync(id);
            if (download == null)
            {
                return NotFound(new { error = "Download not found", id });
            }

            if (download.Status != DownloadStatus.ImportBlocked)
            {
                return BadRequest(new
                {
                    error = "Download is not import blocked",
                    id,
                    status = download.Status.ToString()
                });
            }

            download.Status = DownloadStatus.ImportPending;
            download.ImportBlockReason = null;
            download.ImportBlockMessages = null;
            download.ImportAttempts = 0;

            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Reset blocked import {DownloadId} back to ImportPending", id);
            return Ok(new
            {
                message = "Import retry queued",
                id,
                status = download.Status.ToString()
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogError(ex, "Error retrying blocked import {DownloadId}", id);
            return StatusCode(500, new { error = "Failed to retry blocked import", message = ex.Message });
        }
    }

    /// <summary>
    /// Get all active downloads (Queued, Downloading, Processing, or ImportPending status).
    /// </summary>
    [HttpGet("active")]
    public async Task<ActionResult<IEnumerable<Download>>> GetActiveDownloads()
    {
        try
        {
            var downloadClients = await _configurationService.GetDownloadClientConfigurationsAsync();
            var enabledClientIds = downloadClients
                .Where(c => c.IsEnabled && !string.IsNullOrWhiteSpace(c.Id))
                .Select(c => c.Id)
                .ToList();

            var activeDownloads = await _dbContext.Downloads
                .Where(d => d.Status == DownloadStatus.Queued ||
                           d.Status == DownloadStatus.Downloading ||
                           d.Status == DownloadStatus.Processing ||
                           d.Status == DownloadStatus.ImportPending)
                .Where(d =>
                    d.DownloadClientId == "DDL" ||
                    (!string.IsNullOrEmpty(d.DownloadClientId) && enabledClientIds.Contains(d.DownloadClientId)))
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


    /// <summary>
    /// Delete a download record from the database. This does not cancel an active download in the client.
    /// </summary>
    /// <param name="id">Download record ID.</param>
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
    /// Delete all download records with Completed status.
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
    /// Delete all download records with Failed or ImportBlocked status.
    /// </summary>
    [HttpDelete("failed")]
    public async Task<ActionResult> ClearFailedDownloads()
    {
        try
        {
            var failedDownloads = await _dbContext.Downloads
                .Where(d => d.Status == DownloadStatus.Failed || d.Status == DownloadStatus.ImportBlocked)
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


