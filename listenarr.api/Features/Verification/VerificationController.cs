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

using Listenarr.Application.Audiobooks.Verification;

using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Verification
{
    /// <summary>
    /// Audio-based library verification (ADR-0001): trigger agent passes and
    /// set/clear the sticky manual verification states. The agent FLAGS ONLY —
    /// every corrective action stays in the existing manual UI.
    /// </summary>
    [ApiController]

    [Route("api/v{version:apiVersion}/verification")]
    [Tags("Verification")]
    public class VerificationController : ControllerBase
    {
        public record BatchVerifyRequest(List<int>? AudiobookIds);
        public record ManualVerificationRequest(string Action);

        private readonly ILibraryVerificationQueueService _queue;
        private readonly IAudiobookRepository _audiobookRepository;
        private readonly ILogger<VerificationController> _logger;

        public VerificationController(
            ILibraryVerificationQueueService queue,
            IAudiobookRepository audiobookRepository,
            ILogger<VerificationController> logger)
        {
            _queue = queue;
            _audiobookRepository = audiobookRepository;
            _logger = logger;
        }

        /// <summary>
        /// Start a verification pass. Empty/absent ids = walk the whole library
        /// (idempotent: only Unverified books are processed). Explicit ids
        /// re-verify agent-judged books too; manual states are always sticky.
        /// </summary>
        [HttpPost("batch")]
        public async Task<IActionResult> StartBatch([FromBody] BatchVerifyRequest? request)
        {
            var ids = request?.AudiobookIds is { Count: > 0 } list ? list : null;
            var jobId = await _queue.EnqueueAsync(ids);
            return Ok(new { jobId = jobId.ToString() });
        }

        /// <summary>Re-run verification for a single book (manual states stay sticky).</summary>
        [HttpPost("audiobook/{id}")]
        public async Task<IActionResult> VerifyOne(int id)
        {
            var audiobook = await _audiobookRepository.GetByIdAsync(id);
            if (audiobook == null) return NotFound(new { error = "Audiobook not found" });

            if (!audiobook.VerificationStatus.IsAgentWritable())
            {
                return Conflict(new { error = $"Verification status is {audiobook.VerificationStatus} — manual states are sticky. Clear it first to re-run the agent." });
            }

            var jobId = await _queue.EnqueueAsync(new List<int> { id });
            return Ok(new { jobId = jobId.ToString() });
        }

        /// <summary>
        /// Set or clear manual verification. Action: "verify" (ManuallyVerified),
        /// "reject" (Rejected), or "clear" (back to Unverified, eligible for the
        /// next agent pass). Manual states outrank agent verdicts.
        /// </summary>
        [HttpPost("audiobook/{id}/manual")]
        public async Task<IActionResult> SetManual(int id, [FromBody] ManualVerificationRequest request)
        {
            var audiobook = await _audiobookRepository.GetByIdAsync(id);
            if (audiobook == null) return NotFound(new { error = "Audiobook not found" });

            var actor = User?.Identity?.IsAuthenticated == true ? User.Identity!.Name : null;
            switch (request.Action?.ToLowerInvariant())
            {
                case "verify":
                    audiobook.VerificationStatus = VerificationStatus.ManuallyVerified;
                    break;
                case "reject":
                    audiobook.VerificationStatus = VerificationStatus.Rejected;
                    break;
                case "clear":
                    audiobook.VerificationStatus = VerificationStatus.Unverified;
                    audiobook.VerificationConfidence = null;
                    audiobook.VerifiedAt = null;
                    audiobook.VerifiedBy = null;
                    audiobook.VerificationMethod = null;
                    // Keep transcript + detail: they are the audit trail of the
                    // last agent pass and harmless once status is Unverified.
                    await _audiobookRepository.UpdateAsync(audiobook);
                    return Ok(MapVerification(audiobook));
                default:
                    return BadRequest(new { error = "Action must be 'verify', 'reject', or 'clear'" });
            }

            audiobook.VerificationConfidence = null; // human certainty isn't a score
            audiobook.VerifiedAt = DateTime.UtcNow;
            audiobook.VerifiedBy = actor ?? "user";
            audiobook.VerificationMethod = "manual";
            await _audiobookRepository.UpdateAsync(audiobook);

            _logger.LogInformation(
                "Audiobook {Id} manually marked {Status} by {Actor}", id, audiobook.VerificationStatus, actor ?? "user");
            return Ok(MapVerification(audiobook));
        }

        /// <summary>
        /// Cancel a queued or running verification job. The current book's
        /// transcription is aborted; already-persisted verdicts are kept.
        /// </summary>
        [HttpPost("jobs/{jobId}/cancel")]
        public IActionResult CancelJob(Guid jobId)
        {
            if (!_queue.TryGetJob(jobId, out var job) || job == null)
                return NotFound(new { error = "Job not found or expired" });

            if (!_queue.TryCancel(jobId))
                return Conflict(new { error = $"Job is {job.Status} — only queued or running jobs can be cancelled" });

            _logger.LogInformation("Verification job {JobId} cancellation requested via API", jobId);
            return Ok(new { jobId = jobId.ToString(), status = "Cancelling" });
        }

        /// <summary>Status of a previously enqueued verification job.</summary>
        [HttpGet("jobs/{jobId}")]
        public IActionResult GetJob(Guid jobId)
        {
            if (!_queue.TryGetJob(jobId, out var job) || job == null)
                return NotFound(new { error = "Job not found or expired" });

            return Ok(new
            {
                jobId = job.Id.ToString(),
                status = job.Status,
                total = job.Total,
                processed = job.Processed,
                verified = job.Verified,
                flagged = job.Flagged,
                skipped = job.Skipped,
                failed = job.Failed,
                error = job.Error
            });
        }

        private static object MapVerification(Audiobook audiobook) => new
        {
            id = audiobook.Id,
            verificationStatus = audiobook.VerificationStatus,
            verificationConfidence = audiobook.VerificationConfidence,
            verifiedAt = audiobook.VerifiedAt,
            verifiedBy = audiobook.VerifiedBy,
            verificationMethod = audiobook.VerificationMethod
        };
    }
}
