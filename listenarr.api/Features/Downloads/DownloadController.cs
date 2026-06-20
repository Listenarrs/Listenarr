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

using Listenarr.Application.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Downloads
{
    [ApiController]
    [Route("api/v{version:apiVersion}/download")]
    [Tags("Downloads")]
    public class DownloadController : ControllerBase
    {
        private readonly IDownloadService _downloadService;
        private readonly IDownloadQueueService _downloadQueueService;
        private readonly IDownloadProcessingJobService _downloadProcessingJobService;
        private readonly ILogger<DownloadController> _logger;
        private readonly IDownloadReferenceService? _downloadReferenceService;

        public DownloadController(
            IDownloadService downloadService,
            IDownloadQueueService downloadQueueService,
            IDownloadProcessingJobService downloadProcessingJobService,
            ILogger<DownloadController> logger,
            IDownloadReferenceService? downloadReferenceService = null)
        {
            _downloadService = downloadService;
            _downloadQueueService = downloadQueueService;
            _downloadProcessingJobService = downloadProcessingJobService;
            _logger = logger;
            _downloadReferenceService = downloadReferenceService;
        }

        /// <summary>
        /// Search all enabled indexers for an audiobook and automatically send the best match to a download client.
        /// </summary>
        /// <param name="request">Request containing the audiobook ID to search for.</param>
        [HttpPost("search-and-download")]
        public async Task<ActionResult<SearchAndDownloadResult>> SearchAndDownload([FromBody] SearchAndDownloadRequest request)
        {
            try
            {
                var result = await _downloadService.SearchAndDownloadAsync(request.AudiobookId);
                return Ok(result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error in search and download for audiobook {AudiobookId}", request.AudiobookId);
                return StatusCode(500, new { message = "Failed to search and download", error = ex.Message });
            }
        }

        /// <summary>
        /// Send a specific search result to a download client (torrent or NZB).
        /// </summary>
        /// <param name="request">The search result to download, optional download client ID, and optional audiobook ID to associate.</param>
        [HttpPost("send")]
        public async Task<ActionResult<string>> SendToDownloadClient([FromBody] SendDownloadRequest request)
        {
            try
            {
                _logger.LogInformation("=== SendToDownloadClient RECEIVED REQUEST ===");
                if (_downloadReferenceService == null)
                {
                    return StatusCode(
                        StatusCodes.Status503ServiceUnavailable,
                        new { message = "Download reference service is unavailable" });
                }

                var candidate = _downloadReferenceService.Read(request.DownloadReference);
                _logger.LogInformation("Title: {Title}", LogRedaction.SanitizeText(candidate.Title));
                _logger.LogInformation("Protocol: {Protocol}", candidate.SourceDescriptor.Protocol);
                _logger.LogInformation("Source: {Source}", LogRedaction.SanitizeText(candidate.Source));

                var downloadId = await _downloadService.SendToDownloadClientAsync(
                    candidate,
                    request.DownloadClientId,
                    request.AudiobookId
                );

                if (string.IsNullOrEmpty(downloadId))
                {
                    return Conflict(new { message = "A download for this audiobook is already active" });
                }

                return Ok(new { downloadId, message = "Sent to download client successfully" });
            }
            catch (DownloadReferenceException ex)
            {
                _logger.LogWarning("Rejected download reference: {Message}", ex.Message);
                return StatusCode(
                    ex.IsExpired ? StatusCodes.Status410Gone : StatusCodes.Status400BadRequest,
                    new { message = "Unable to use download reference", error = ex.Message });
            }
            catch (DownloadClientSubmissionException ex)
            {
                _logger.LogWarning(ex, "Download client submission failed");
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    message = "Failed to send torrent to download client",
                    error = ex.Message
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error sending to download client");
                return StatusCode(500, new { message = "Failed to send to download client", error = ex.Message });
            }
        }

        /// <summary>
        /// Get the current download queue from all enabled download clients.
        /// </summary>
        [HttpGet("queue")]
        public async Task<ActionResult<QueueSnapshot>> GetQueue()
        {
            try
            {
                var snapshot = await _downloadQueueService.GetQueueSnapshotAsync();
                return Ok(snapshot);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error getting download queue");
                return StatusCode(500, new { message = "Failed to get download queue", error = ex.Message });
            }
        }

        /// <summary>
        /// Retrieve cached torrent file bytes for a download, if available.
        /// </summary>
        /// <param name="downloadId">Download ID.</param>
        /// <returns>The torrent file as a binary download.</returns>
        [HttpGet("cached/{downloadId}/torrent")]
        public async Task<IActionResult> GetCachedTorrent(string downloadId)
        {
            try
            {
                var tuple = await _downloadService.GetCachedTorrentAsync(downloadId);
                var bytes = tuple.Bytes;
                var fileName = tuple.FileName ?? "download.torrent";
                if (bytes != null && bytes.Length > 0)
                {
                    _logger.LogInformation("Served cached torrent for download {DownloadId}", LogRedaction.SanitizeText(downloadId));
                    return File(bytes, "application/x-bittorrent", fileName);
                }

                return NotFound(new { error = "Cached torrent not found", downloadId });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error retrieving cached torrent for download {DownloadId}", LogRedaction.SanitizeText(downloadId));
                return StatusCode(500, new { message = "Failed to retrieve cached torrent", error = ex.Message });
            }
        }

        /// <summary>
        /// Retrieve cached tracker announce URLs for a download.
        /// </summary>
        /// <param name="downloadId">Download ID.</param>
        [HttpGet("cached/{downloadId}/announces")]
        public async Task<IActionResult> GetCachedAnnounces(string downloadId)
        {
            try
            {
                var announces = await _downloadService.GetCachedAnnouncesAsync(downloadId);
                if (announces != null && announces.Count > 0)
                {
                    _logger.LogInformation("Served cached announces for download {DownloadId}", LogRedaction.SanitizeText(downloadId));
                    return Ok(new { downloadId, announces });
                }
                return NotFound(new { error = "Cached announces not found", downloadId });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error retrieving cached announces for download {DownloadId}", LogRedaction.SanitizeText(downloadId));
                return StatusCode(500, new { message = "Failed to retrieve cached announces", error = ex.Message });
            }
        }

        /// <summary>
        /// Remove a download from the queue and optionally from the download client.
        /// </summary>
        /// <param name="downloadId">Download ID to remove.</param>
        /// <param name="downloadClientId">Optional download client ID to target.</param>
        /// <param name="force">When true, remove the database record even if the download client removal fails.</param>
        [HttpDelete("queue/{downloadId}")]
        public async Task<ActionResult> RemoveFromQueue(string downloadId, [FromQuery] string? downloadClientId = null, [FromQuery] bool force = false)
        {
            try
            {
                var removed = await _downloadService.RemoveFromQueueAsync(downloadId, downloadClientId, force);
                if (removed)
                {
                    return Ok(new { message = "Removed from queue successfully" });
                }
                return NotFound(new { message = "Download not found in queue or removal failed. Try force=true to remove from database only." });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error removing from queue");
                return StatusCode(500, new { message = "Failed to remove from queue", error = ex.Message });
            }
        }

        /// <summary>
        /// Re-queue a completed download for reprocessing (file import).
        /// </summary>
        /// <param name="downloadId">Download ID to reprocess.</param>
        [HttpPost("reprocess/{downloadId}")]
        public async Task<ActionResult> ReprocessDownload(string downloadId)
        {
            try
            {
                var jobId = await _downloadService.ReprocessDownloadAsync(downloadId);
                if (jobId != null)
                {
                    return Ok(new { message = "Download queued for reprocessing", jobId });
                }
                return NotFound(new { message = "Download not found or not eligible for reprocessing" });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error reprocessing download {DownloadId}", downloadId);
                return StatusCode(500, new { message = "Failed to reprocess download", error = ex.Message });
            }
        }

        /// <summary>
        /// Bulk re-queue multiple completed downloads for reprocessing.
        /// </summary>
        /// <param name="request">List of download IDs to reprocess.</param>
        [HttpPost("reprocess/bulk")]
        public async Task<ActionResult> ReprocessDownloads([FromBody] ReprocessRequest request)
        {
            try
            {
                var results = await _downloadService.ReprocessDownloadsAsync(request.DownloadIds);
                return Ok(new
                {
                    message = "Bulk reprocessing initiated",
                    processed = results.Count(r => r.Success),
                    failed = results.Count(r => !r.Success),
                    results
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error in bulk reprocess");
                return StatusCode(500, new { message = "Failed to bulk reprocess downloads", error = ex.Message });
            }
        }

        /// <summary>
        /// Re-queue all completed downloads matching the specified criteria for reprocessing.
        /// </summary>
        /// <param name="request">Optional filters: include already-processed downloads and maximum age.</param>
        [HttpPost("reprocess/all")]
        public async Task<ActionResult> ReprocessAllDownloads([FromBody] ReprocessAllRequest? request = null)
        {
            try
            {
                var results = await _downloadService.ReprocessAllCompletedDownloadsAsync(
                    request?.IncludeProcessed ?? false,
                    request?.MaxAge ?? TimeSpan.FromDays(30)
                );

                return Ok(new
                {
                    message = "Reprocessing initiated for all eligible downloads",
                    processed = results.Count(r => r.Success),
                    failed = results.Count(r => !r.Success),
                    results
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error in reprocess all");
                return StatusCode(500, new { message = "Failed to reprocess all downloads", error = ex.Message });
            }
        }

        /// <summary>
        /// Get download processing queue statistics (pending, in-progress, completed counts).
        /// </summary>
        [HttpGet("processing/stats")]
        public async Task<ActionResult> GetProcessingStats()
        {
            try
            {
                var stats = await _downloadProcessingJobService.GetStatsAsync();
                return Ok(stats);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error getting processing stats");
                return StatusCode(500, new { message = "Failed to get processing stats", error = ex.Message });
            }
        }

        /// <summary>
        /// Get recent download processing activity.
        /// </summary>
        /// <param name="count">Maximum number of activity entries to return (default 50).</param>
        [HttpGet("processing/activity")]
        public async Task<ActionResult> GetProcessingActivity([FromQuery] int count = 50)
        {
            try
            {
                var activity = await _downloadProcessingJobService.GetRecentActivityAsync(count);
                return Ok(activity);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error getting processing activity");
                return StatusCode(500, new { message = "Failed to get processing activity", error = ex.Message });
            }
        }
    }

    public class SearchAndDownloadRequest
    {
        public int AudiobookId { get; set; }
    }

    public class SendDownloadRequest
    {
        public string DownloadReference { get; set; } = string.Empty;
        public string? DownloadClientId { get; set; }
        public int? AudiobookId { get; set; }
    }

    public class ReprocessRequest
    {
        public List<string> DownloadIds { get; set; } = new();
    }

    public class ReprocessAllRequest
    {
        /// <summary>
        /// Include downloads that have already been processed
        /// </summary>
        public bool IncludeProcessed { get; set; } = false;

        /// <summary>
        /// Maximum age of downloads to include (default: 30 days)
        /// </summary>
        public TimeSpan MaxAge { get; set; } = TimeSpan.FromDays(30);
    }
}
