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
using Listenarr.Domain.Common;

namespace Listenarr.Domain.Audiobooks
{
    public enum MoveJobStatus
    {
        Queued,
        Running,
        RetryScheduled,
        NeedsAttention,
        Completed,
        Failed,
        Superseded
    }

    public enum MoveJobPhase
    {
        None,
        Planned,
        Copying,
        Published,
        CleaningSource,
        Finalizing,
        CleaningArtifacts,
        RecordingCompletion
    }

    public enum MoveFailureKind
    {
        None,
        Transient,
        SourceDrift,
        Verification,
        UnsupportedEntry,
        Persistence,
        Unknown
    }

    public enum MoveJobEntryType
    {
        File,
        Directory
    }

    public enum MoveJobEntryCopyState
    {
        Pending,
        Staged,
        Published,
        Verified
    }

    public enum MoveJobEntryCleanupState
    {
        Pending,
        DeleteAuthorized,
        Deleted,
        Retained
    }

    public enum MoveSourceCleanupMode
    {
        RetainSource = 0,
        DeleteAfterVerifiedCopy = 1
    }

    public static class MoveIdentityProtocol
    {
        public const int Current = 2;
    }

    public static class MoveExecutionProtocol
    {
        public const int PreDurableReleased = 0;
        public const int TargetBoundaryMarkerlessDatabaseState = 1;
        public const int MarkerlessDatabaseState = 2;
        public const int Current = MarkerlessDatabaseState;

        public static bool IsCurrent(int version) => version == Current;
    }

    public static class MoveJobStatusExtensions
    {
        public static bool IsActive(this MoveJobStatus status) => status is
            MoveJobStatus.Queued or
            MoveJobStatus.Running or
            MoveJobStatus.RetryScheduled;
    }

    public class MoveJob
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();
        public int AudiobookId { get; set; }
        public string? RequestedPath { get; set; }
        public DateTime EnqueuedAt { get; set; } = DateTime.UtcNow;
        public MoveJobStatus Status { get; set; } = MoveJobStatus.Queued;
        public MoveJobPhase Phase { get; set; } = MoveJobPhase.None;
        public int ExecutionProtocolVersion { get; set; } =
            MoveExecutionProtocol.Current;
        [MaxLength(512)]
        public string? SourceDirectoryObjectIdentity { get; set; }
        [MaxLength(512)]
        public string? TargetDirectoryObjectIdentity { get; set; }
        public MoveJobEntryCleanupState SourceDirectoryCleanupState { get; set; } =
            MoveJobEntryCleanupState.Pending;
        public string? Error { get; set; }
        public MoveFailureKind FailureKind { get; set; } = MoveFailureKind.None;
        public int AttemptCount { get; set; } = 0;
        public DateTime? UpdatedAt { get; set; }
        public string? ActiveDeduplicationKey { get; set; }
        public int IdentityKeyVersion { get; set; } = MoveIdentityProtocol.Current;
        public string? LeaseOwner { get; set; }
        public DateTime? LeaseExpiresAt { get; set; }
        public int LeaseGeneration { get; set; }
        public DateTime? NextAttemptAt { get; set; }
        public Guid? RelocationId { get; set; }
        public RootFolderRelocation? Relocation { get; set; }
        // Optional source path snapshot provided at enqueue time. Persist this so jobs
        // remain durable and can be inspected / resumed across restarts.
        public string? SourcePath { get; set; }
        public FileSystemPathSyntax? SourcePathSyntax { get; set; }
        public FileSystemCaseSensitivity? SourceCaseSensitivity { get; set; }
        public FileSystemCaseSensitivityMode? SourceCaseSensitivityMode { get; set; }
        public string? SourceIdentityBoundary { get; set; }
        public FileSystemPathSyntax? TargetPathSyntax { get; set; }
        public FileSystemCaseSensitivity? TargetCaseSensitivity { get; set; }
        public FileSystemCaseSensitivityMode? TargetCaseSensitivityMode { get; set; }
        public string? TargetIdentityBoundary { get; set; }
        public string? SourceCleanupBoundary { get; set; }
        public bool DeleteEmptySource { get; set; } = true;
        public MoveSourceCleanupMode SourceCleanupMode { get; set; } =
            MoveSourceCleanupMode.RetainSource;
        public bool ForceCopyAndRetainSource { get; set; }
        public int? SourceRootFolderId { get; set; }
        public int? SourcePolicyRevision { get; set; }
        public int? SourceStorageContractRevision { get; set; }
        public int? TargetRootFolderId { get; set; }
        public int? TargetPolicyRevision { get; set; }
        public int? TargetStorageContractRevision { get; set; }
        public ICollection<MoveJobEntry> Entries { get; set; } = new List<MoveJobEntry>();
        public ICollection<MoveJobCreatedDirectory> CreatedDirectories { get; set; } = new List<MoveJobCreatedDirectory>();
        public MoveScanHandoff? ScanHandoff { get; set; }

        public bool TryGetSourceIdentity(out PathIdentitySnapshot identity)
        {
            if (SourcePathSyntax.HasValue
                && SourceCaseSensitivity.HasValue
                && SourceCaseSensitivityMode.HasValue
                && !string.IsNullOrWhiteSpace(SourceIdentityBoundary))
            {
                identity = new PathIdentitySnapshot(
                    SourcePathSyntax.Value,
                    SourceCaseSensitivity.Value,
                    SourceCaseSensitivityMode.Value,
                    SourceIdentityBoundary);
                return true;
            }

            identity = default;
            return false;
        }

        public bool TryGetTargetIdentity(out PathIdentitySnapshot identity)
        {
            if (TargetPathSyntax.HasValue
                && TargetCaseSensitivity.HasValue
                && TargetCaseSensitivityMode.HasValue
                && !string.IsNullOrWhiteSpace(TargetIdentityBoundary))
            {
                identity = new PathIdentitySnapshot(
                    TargetPathSyntax.Value,
                    TargetCaseSensitivity.Value,
                    TargetCaseSensitivityMode.Value,
                    TargetIdentityBoundary);
                return true;
            }

            identity = default;
            return false;
        }

        public void SetSourceIdentity(PathIdentitySnapshot identity)
        {
            SourcePathSyntax = identity.Syntax;
            SourceCaseSensitivity = identity.CaseSensitivity;
            SourceCaseSensitivityMode = identity.RequestedMode;
            SourceIdentityBoundary = identity.BoundaryPath;
        }

        public void SetTargetIdentity(PathIdentitySnapshot identity)
        {
            TargetPathSyntax = identity.Syntax;
            TargetCaseSensitivity = identity.CaseSensitivity;
            TargetCaseSensitivityMode = identity.RequestedMode;
            TargetIdentityBoundary = identity.BoundaryPath;
        }
    }

    public static class MoveJobManualRetry
    {
        public static void Reset(MoveJob job, string deduplicationKey, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(job);
            ArgumentException.ThrowIfNullOrWhiteSpace(deduplicationKey);

            job.Status = MoveJobStatus.Queued;
            // Preserve the durable phase so retries resume from the last proven checkpoint.
            job.Error = null;
            job.FailureKind = MoveFailureKind.None;
            job.AttemptCount = 0;
            job.NextAttemptAt = null;
            job.LeaseOwner = null;
            job.LeaseExpiresAt = null;
            job.UpdatedAt = nowUtc;
            job.ActiveDeduplicationKey = deduplicationKey;
        }
    }

    public class MoveJobEntry
    {
        public long Id { get; set; }
        public Guid MoveJobId { get; set; }
        public MoveJob MoveJob { get; set; } = null!;
        [Required, MaxLength(2000)]
        public string RelativePath { get; set; } = string.Empty;
        public MoveJobEntryType EntryType { get; set; }
        public long Length { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }
        [MaxLength(64)]
        public string? Sha256 { get; set; }
        public MoveJobEntryCopyState CopyState { get; set; }
        public MoveJobEntryCleanupState CleanupState { get; set; }
        public int CleanupProtectionVersion { get; set; }
        [MaxLength(512)]
        public string? SourcePhysicalObjectIdentity { get; set; }
        [MaxLength(512)]
        public string? TargetPhysicalObjectIdentity { get; set; }
    }
}
