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
using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Verification
{
    /// <summary>
    /// One audio-verification pass (ADR-0001). A null <see cref="AudiobookIds"/>
    /// means "walk the whole library" (idempotent: only Unverified books are
    /// processed). Explicit ids re-verify agent-written states too, but manual
    /// states (ManuallyVerified/Rejected) are sticky in every mode.
    /// </summary>
    public class VerificationJob
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public List<int>? AudiobookIds { get; set; }
        /// <summary>
        /// What started this job: "manual" (user-triggered batch/per-book) or
        /// "import" (auto-enqueued after a successful download import). Carried
        /// on the SignalR payloads so the FE can ignore background import jobs
        /// it didn't start.
        /// </summary>
        public string Trigger { get; set; } = VerificationTriggers.Manual;
        public DateTime EnqueuedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }
        public string Status { get; set; } = "Queued";
        public string? Error { get; set; }
        /// <summary>
        /// Cooperative cancellation for this job. The worker links it with its own
        /// stopping token, so a cancel takes effect mid-transcription, not just
        /// between books. Cancelled-while-queued jobs are skipped on dequeue.
        /// </summary>
        public CancellationTokenSource CancelSource { get; } = new();
        public int Total { get; set; }
        public int Processed { get; set; }
        public int Verified { get; set; }
        public int Flagged { get; set; }
        /// <summary>Books whose audio never announces itself (NoSpokenCredits) — neutral, not flagged.</summary>
        public int Unverifiable { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
    }

    /// <summary>Well-known values for <see cref="VerificationJob.Trigger"/>.</summary>
    public static class VerificationTriggers
    {
        public const string Manual = "manual";
        public const string Import = "import";
        /// <summary>Auto-enqueued after files were transferred onto the book.</summary>
        public const string Transfer = "transfer";
        /// <summary>Auto-enqueued after a metadata edit changed the book's identity.</summary>
        public const string Metadata = "metadata";
    }

    public interface ILibraryVerificationQueueService
    {
        /// <summary>Enqueue a verification pass; null ids = whole library.</summary>
        Task<Guid> EnqueueAsync(List<int>? audiobookIds, string trigger = VerificationTriggers.Manual);
        bool TryGetJob(Guid id, out VerificationJob? job);
        /// <summary>
        /// Request cancellation of a queued or running job. Returns false when the
        /// job is unknown, expired, or already finished.
        /// </summary>
        bool TryCancel(Guid id);
        ChannelReader<VerificationJob> Reader { get; }
    }

    public class LibraryVerificationQueueService : ILibraryVerificationQueueService
    {
        private static readonly TimeSpan JobTtl = TimeSpan.FromHours(6);

        private readonly ConcurrentDictionary<Guid, VerificationJob> _jobs = new();
        private readonly Channel<VerificationJob> _channel = Channel.CreateUnbounded<VerificationJob>();
        private readonly ILogger<LibraryVerificationQueueService> _logger;

        public LibraryVerificationQueueService(ILogger<LibraryVerificationQueueService> logger)
        {
            _logger = logger;
        }

        public ChannelReader<VerificationJob> Reader => _channel.Reader;

        public async Task<Guid> EnqueueAsync(List<int>? audiobookIds, string trigger = VerificationTriggers.Manual)
        {
            PurgeExpired();

            // Dedupe whole-library walks: a second "verify everything" while one
            // is queued/running would only re-do the same work.
            if (audiobookIds == null)
            {
                var existing = _jobs.Values.FirstOrDefault(j =>
                    j.AudiobookIds == null && j.Status is "Queued" or "Processing");
                if (existing != null)
                {
                    _logger.LogInformation("Deduping library verification job {JobId}", existing.Id);
                    return existing.Id;
                }
            }

            var job = new VerificationJob { AudiobookIds = audiobookIds, Trigger = trigger };
            _jobs[job.Id] = job;
            _logger.LogInformation(
                "Enqueueing verification job {JobId} ({Scope}, trigger {Trigger})",
                job.Id, audiobookIds == null ? "whole library" : $"{audiobookIds.Count} book(s)", trigger);
            await _channel.Writer.WriteAsync(job);
            return job.Id;
        }

        public bool TryGetJob(Guid id, out VerificationJob? job) => _jobs.TryGetValue(id, out job);

        public bool TryCancel(Guid id)
        {
            if (!_jobs.TryGetValue(id, out var job)) return false;
            if (job.Status is not ("Queued" or "Processing")) return false;

            _logger.LogInformation("Cancellation requested for verification job {JobId} ({Status})", id, job.Status);
            job.CancelSource.Cancel();
            return true;
        }

        private void PurgeExpired()
        {
            var cutoff = DateTime.UtcNow - JobTtl;
            foreach (var (id, job) in _jobs)
            {
                if (job.Status is not ("Completed" or "Failed" or "Cancelled")) continue;
                if ((job.CompletedAt ?? job.EnqueuedAt) >= cutoff) continue;
                if (_jobs.TryRemove(id, out var removed))
                {
                    removed.CancelSource.Dispose();
                }
            }
        }
    }
}
