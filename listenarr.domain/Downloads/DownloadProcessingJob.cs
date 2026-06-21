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
        /// Schedule job for retry with exponential backoff
        /// </summary>
        public DownloadProcessingJob ScheduleRetry(string errorMessage = "")
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

            // Exponential backoff: 30s, 2m, 8m, etc.
            var backoffMinutes = Math.Pow(2, RetryCount) * 0.5; // 0.5, 1, 2, 4, 8 minutes
            NextRetryAt = DateTime.UtcNow.AddMinutes(backoffMinutes);

            AddLogEntry($"Scheduled for retry #{RetryCount} at {NextRetryAt}");
            return this;
        }
    }
}
