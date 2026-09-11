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

using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace Listenarr.Domain.Downloads
{
    public enum ProcessingJobStatus
    {
        Pending,
        Processing,
        Completed,
        Failed,
        Retry
    }

    public enum ProcessingJobType
    {
        MoveOrCopyFile,
        ExtractMetadata,
        GenerateFileName,
        CreateAudiobookFile,
        NotifyCompletion
    }

    /// <summary>
    /// Represents a post-processing job for completed downloads
    /// </summary>
    public class DownloadProcessingJob
    {
        [Key]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// The download this job is processing
        /// </summary>
        [Required]
        public string DownloadId { get; set; } = string.Empty;

        public string? ActiveDeduplicationKey { get; set; }

        /// <summary>
        /// Type of processing job
        /// </summary>
        public ProcessingJobType JobType { get; set; }

        /// <summary>
        /// Current status of the job
        /// </summary>
        public ProcessingJobStatus Status { get; set; } = ProcessingJobStatus.Pending;

        /// <summary>
        /// Priority of the job (higher = more important)
        /// </summary>
        public int Priority { get; set; } = 5;

        /// <summary>
        /// Source file path (before processing)
        /// </summary>
        public string? SourcePath { get; set; }

        /// <summary>
        /// Destination file path (after processing)
        /// </summary>
        public string? DestinationPath { get; set; }

        /// <summary>
        /// Download client ID for path mapping
        /// </summary>
        public string? DownloadClientId { get; set; }

        /// <summary>
        /// Number of retry attempts made
        /// </summary>
        public int RetryCount { get; set; } = 0;

        /// <summary>
        /// Maximum number of retries allowed
        /// </summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// Error message if job failed
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// When the job was created
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// When processing started
        /// </summary>
        public DateTime? StartedAt { get; set; }

        /// <summary>
        /// When the job was completed (successfully or failed permanently)
        /// </summary>
        public DateTime? CompletedAt { get; set; }

        /// <summary>
        /// When to retry next (for failed jobs)
        /// </summary>
        public DateTime? NextRetryAt { get; set; }

        /// <summary>
        /// Additional job-specific data (stored as JSON)
        /// </summary>
        public Dictionary<string, object> JobData { get; set; } = new();

        /// <summary>
        /// Processing log entries
        /// </summary>
        public List<string> ProcessingLog { get; set; } = new();

        public string GetOrCreateCorrelationId()
        {
            if (TryGetJobDataString("CorrelationId", out var existing))
            {
                return existing;
            }

            var correlationId = Guid.NewGuid().ToString("N");
            JobData["CorrelationId"] = correlationId;
            return correlationId;
        }

        public bool HasCheckpoint(string checkpoint)
        {
            if (!JobData.TryGetValue(checkpoint, out var value) || value == null) return false;
            return value switch
            {
                bool boolean => boolean,
                JsonElement element when element.ValueKind is JsonValueKind.True or JsonValueKind.False => element.GetBoolean(),
                _ => bool.TryParse(value.ToString(), out var parsed) && parsed
            };
        }

        public void SetCheckpoint(string checkpoint, object? detail = null)
        {
            JobData[checkpoint] = true;
            if (detail != null) JobData[$"{checkpoint}Detail"] = detail;
            AddLogEntry($"Checkpoint completed: {checkpoint}");
        }

        public bool TryGetJobDataString(string key, out string value)
        {
            value = string.Empty;
            if (!JobData.TryGetValue(key, out var raw) || raw == null) return false;
            value = raw is JsonElement element ? element.ToString() : raw.ToString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }

        /// <summary>
        /// Add a log entry with timestamp
        /// </summary>
        public void AddLogEntry(string message)
        {
            if (!string.IsNullOrEmpty(message))
            {
                ProcessingLog.Add($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] {message}");
            }
        }

        /// <summary>
        /// Indicates a job as Pending, effectively setting it for queue processing
        /// </summary>
        public DownloadProcessingJob UnStuck(string message = "")
        {
            Status = ProcessingJobStatus.Pending;
            StartedAt = DateTime.UtcNow;
            AddLogEntry(message);
            return this;
        }

        /// <summary>
        /// Indicates a job has started
        /// </summary>
        public DownloadProcessingJob MarkAsProcessing()
        {
            Status = ProcessingJobStatus.Processing;
            StartedAt = DateTime.UtcNow;
            AddLogEntry("Started processing");
            return this;
        }

        /// <summary>
        /// Mark job as failed with error message
        /// </summary>
        public DownloadProcessingJob MarkAsFailed(string errorMessage)
        {
            Status = ProcessingJobStatus.Failed;
            ErrorMessage = errorMessage;
            CompletedAt = DateTime.UtcNow;
            AddLogEntry($"Job failed: {errorMessage}");
            return this;
        }

        /// <summary>
        /// Mark job as completed successfully
        /// </summary>
        public DownloadProcessingJob MarkAsCompleted()
        {
            Status = ProcessingJobStatus.Completed;
            CompletedAt = DateTime.UtcNow;
            AddLogEntry("Job completed successfully");
            return this;
        }

        /// <summary>
        /// Ceiling on the exponential retry backoff, in seconds. One day.
        /// </summary>
        public const double MaxRetryDelaySeconds = 24 * 60 * 60;

        /// <summary>
        /// Schedule job for retry with exponential backoff.
        /// </summary>
        /// <param name="errorMessage">Why the attempt failed, recorded on the job.</param>
        /// <param name="initialDelaySeconds">
        /// The wait before the first retry. Later retries double it, so 30 gives 30s, 1m, 2m,
        /// up to <see cref="MaxRetryDelaySeconds"/>. Comes from
        /// ApplicationSettings.MissingSourceRetryInitialDelaySeconds; the default here matches
        /// that property's own default so a caller that does not supply it is unchanged.
        /// </param>
        public DownloadProcessingJob ScheduleRetry(string errorMessage = "", int initialDelaySeconds = 30)
        {
            if (!string.IsNullOrEmpty(errorMessage))
            {
                AddLogEntry(errorMessage);
                ErrorMessage = errorMessage;
            }

            if (RetryCount >= MaxRetries)
            {
                MarkAsFailed($"Max retries ({MaxRetries}) exceeded");
                return this;
            }

            RetryCount++;
            Status = ProcessingJobStatus.Pending;

            // RetryCount was just incremented, so the first retry raises the delay to the power of
            // zero and waits exactly initialDelaySeconds. The old expression squared it a step
            // early: it read the incremented count, so the first retry waited a minute while both
            // comments above it claimed thirty seconds.
            var delay = Math.Max(1, initialDelaySeconds) * Math.Pow(2, RetryCount - 1);

            // The doubling has no ceiling of its own, and both of its inputs are operator
            // settable now that MaxRetries is read from settings. The download settings screen
            // offers up to 600 seconds and up to 20 retries, and at both maxima the twentieth
            // retry lands a little over ten years out. Nothing throws, so the only symptom is a
            // job that never runs again and reads as lost to whoever is looking at the queue.
            //
            // A day is the ceiling rather than an hour. Waiting hours for a source that needs an
            // operator to intervene is a reasonable thing to configure, and an hour would cut the
            // ladder short for anyone who deliberately raised the budget; a day is long enough to
            // be a real backoff and short enough that the job is still visibly scheduled. At the
            // shipped defaults nothing reaches it, because 30 seconds doubled over three retries
            // is two minutes.
            delay = Math.Min(delay, MaxRetryDelaySeconds);
            NextRetryAt = DateTime.UtcNow.AddSeconds(delay);

            AddLogEntry($"Scheduled for retry #{RetryCount} at {NextRetryAt}");
            return this;
        }
    }
}
