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
using Microsoft.Extensions.Caching.Memory;

namespace Listenarr.Api.Features.Downloads;

[ApiController]
[Route("api/v{version:apiVersion}/downloads")]
[Tags("Downloads")]
public class DownloadsController : ControllerBase
{
    private readonly IDownloadRepository _downloadRepository;
    private readonly IDownloadService _downloadService;
    private readonly ILogger<DownloadsController> _logger;
    private readonly IConfigurationService _configurationService;
    private readonly IDownloadProcessingJobService _downloadProcessingJobService;
    private readonly IMemoryCache? _cache;

    public DownloadsController(IDownloadRepository downloadRepository, IDownloadService downloadService, ILogger<DownloadsController> logger, IConfigurationService configurationService, IDownloadProcessingJobService downloadProcessingJobService, IMemoryCache? cache = null)
    {
        _downloadRepository = downloadRepository;
        _downloadService = downloadService;
        _logger = logger;
        _configurationService = configurationService;
        _downloadProcessingJobService = downloadProcessingJobService;
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

        if (_cache.TryGetValue($"mam:cachedtorrent:{downloadId}:announces", out List<string>? announces) && announces != null && announces.Count > 0)
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
            var downloadClients = await _configurationService.GetDownloadClientConfigurationsAsync();
            var enabledClientIds = downloadClients
                .Where(c => c.IsEnabled && !string.IsNullOrWhiteSpace(c.Id))
                .Select(c => c.Id)
                .ToList();

            var all = await _downloadRepository.GetAllAsync();
            var filtered = all.Where(d =>
                d.DownloadClientId == "DDL" ||
                (!string.IsNullOrEmpty(d.DownloadClientId) && enabledClientIds.Contains(d.DownloadClientId)));

            if (!string.IsNullOrEmpty(status) && Enum.TryParse<DownloadStatus>(status, true, out var parsedStatus))
            {
                filtered = filtered.Where(d => d.Status == parsedStatus);
            }

            var downloads = filtered
                .OrderByDescending(d => d.StartedAt)
                .ToList();

            var enhancedDownloads = await EnhanceDownloadsWithClientNames(downloads);

            _logger.LogInformation("Retrieved {Count} downloads", downloads.Count);
            return Ok(enhancedDownloads);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
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
            var download = await _downloadRepository.FindAsync(id);

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
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogError(ex, "Error retrieving download {DownloadId}", LogRedaction.SanitizeText(id));
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
            var download = await _downloadRepository.FindAsync(id);
            if (download == null)
            {
                return NotFound(new { error = "Download not found", id });
            }

            // ImportPending is accepted as well as ImportBlocked, but only when nothing is
            // actually working on it. Before this endpoint queued anything it still cleared the
            // block and set ImportPending, so anyone who called it is left with downloads that no
            // job will ever pick up. Those are worse off than blocked ones: AutomaticSearchService
            // and DownloadDuplicateGuard both count ImportPending as an active download and skip
            // the book, while neither counts ImportBlocked, so the book is never re-searched
            // either. Refusing them would leave every existing victim stranded permanently.
            var stranded = download.Status == DownloadStatus.ImportPending
                && await _downloadProcessingJobService.GetActiveJobAsync(download.Id) == null;

            if (download.Status != DownloadStatus.ImportBlocked && !stranded)
            {
                return BadRequest(new
                {
                    error = "Download is not import blocked",
                    id,
                    status = download.Status.ToString()
                });
            }

            // At most one active download per audiobook: EfDownloadRepository sets
            // ActiveAudiobookDeduplicationKey from the audiobook id whenever the status is active,
            // and a filtered unique index enforces it. ImportPending counts as active and
            // ImportBlocked does not, so unblocking this one collides with any other active
            // download for the same book. Without this check that surfaces as a SQLite constraint
            // violation from deep inside SaveChanges, which tells the caller nothing.
            if (download.AudiobookId.HasValue)
            {
                var siblings = await _downloadRepository.GetByAudiobookIdAsync(download.AudiobookId.Value);
                var active = siblings.FirstOrDefault(other =>
                    other.Id != download.Id
                    && other.ActiveAudiobookDeduplicationKey.HasValue);
                if (active != null)
                {
                    return Conflict(new
                    {
                        error = "Another download for this audiobook is already active",
                        id,
                        conflictingDownloadId = active.Id,
                        conflictingStatus = active.Status.ToString()
                    });
                }
            }

            download.Unblock();

            // Queue the work before persisting the unblock. Clearing the blocked status is what
            // makes the download eligible for import, but nothing watches that field: the only
            // thing that imports a download is a processing job, and the job that would have
            // created one fires on the download client reporting completion, which already
            // happened and will not happen again. Persisting first and queueing second would leave
            // a download that reads as retrying and never is if the queue call failed.
            var jobId = await _downloadProcessingJobService.RequeueAsync(download);

            await _downloadService.UpdateAsync(download);

            _logger.LogInformation("Requeued blocked import {DownloadId} as job {JobId}", LogRedaction.SanitizeText(id), jobId);
            return Ok(new
            {
                message = "Import retry queued",
                id,
                status = download.Status.ToString(),
                jobId
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

            var allDownloads = await _downloadRepository.GetAllAsync();
            var activeDownloads = allDownloads
                .Where(d => d.Status == DownloadStatus.Queued ||
                           d.Status == DownloadStatus.Downloading ||
                           d.Status == DownloadStatus.Processing ||
                           d.Status == DownloadStatus.ImportPending)
                .Where(d =>
                    d.DownloadClientId == "DDL" ||
                    (!string.IsNullOrEmpty(d.DownloadClientId) && enabledClientIds.Contains(d.DownloadClientId)))
                .OrderByDescending(d => d.StartedAt)
                .ToList();

            var enhancedActiveDownloads = await EnhanceDownloadsWithClientNames(activeDownloads);

            _logger.LogInformation("Retrieved {Count} active downloads", activeDownloads.Count);
            return Ok(enhancedActiveDownloads);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
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
            var download = await _downloadRepository.FindAsync(id);

            if (download == null)
            {
                return NotFound(new { error = "Download not found", id });
            }

            await _downloadRepository.RemoveAsync(id);

            _logger.LogInformation("Deleted download record {DownloadId}", LogRedaction.SanitizeText(id));
            return Ok(new { message = "Download deleted successfully", id });
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogError(ex, "Error deleting download {DownloadId}", LogRedaction.SanitizeText(id));
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
            var all = await _downloadRepository.GetAllAsync();
            var completedDownloads = all.Where(d => d.Status == DownloadStatus.Completed).ToList();
            foreach (var d in completedDownloads) await _downloadRepository.RemoveAsync(d.Id);

            _logger.LogInformation("Cleared {Count} completed downloads", completedDownloads.Count);
            return Ok(new { message = "Completed downloads cleared", count = completedDownloads.Count });
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
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
            var all = await _downloadRepository.GetAllAsync();
            var failedDownloads = all.Where(d => d.Status == DownloadStatus.Failed || d.Status == DownloadStatus.ImportBlocked).ToList();
            foreach (var d in failedDownloads) await _downloadRepository.RemoveAsync(d.Id);

            _logger.LogInformation("Cleared {Count} failed downloads", failedDownloads.Count);
            return Ok(new { message = "Failed downloads cleared", count = failedDownloads.Count });
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
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
                foreach (var kvp in d.Metadata.Where(kvp => !string.Equals(kvp.Key, "ClientContentPath", StringComparison.OrdinalIgnoreCase)))
                {
                    dict[kvp.Key] = kvp.Value!;
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
