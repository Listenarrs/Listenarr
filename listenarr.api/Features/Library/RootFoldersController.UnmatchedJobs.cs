using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library;

public partial class RootFoldersController
{
    /// <summary>
    /// Returns the status and results of a previously enqueued unmatched scan job.
    /// </summary>
    [HttpGet("unmatched-results/{jobId}")]
    public IActionResult GetUnmatchedResults(Guid jobId)
    {
        if (!_unmatchedQueue.TryGetJob(jobId, out var job) || job == null)
            return NotFound(new { message = "Scan job not found" });

        return Ok(new
        {
            jobId = job.Id.ToString(),
            status = job.Status,
            error = UnmatchedScanPublicError.FromInternal(job.Error),
            enqueuedAt = job.EnqueuedAt,
            completedAt = job.CompletedAt,
            warnings = job.Warnings,
            items = job.Results ?? new List<UnmatchedFileResult>()
        });
    }

    /// <summary>
    /// Cancels a queued or running unmatched scan job. A queued job is skipped; a running one
    /// stops at its next checkpoint. A job that already finished keeps its status.
    /// </summary>
    [HttpPost("unmatched-jobs/{jobId}/cancel")]
    public IActionResult CancelUnmatchedScan(Guid jobId)
    {
        if (!_unmatchedQueue.TryCancel(jobId, out var job) || job == null)
            return NotFound(new { message = "Scan job not found" });

        return Ok(new { jobId = job.Id.ToString(), status = job.Status });
    }
}
