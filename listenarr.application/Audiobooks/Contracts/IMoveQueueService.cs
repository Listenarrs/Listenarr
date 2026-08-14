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
using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Contracts
{
    public sealed record MoveEnqueueCommand(
        int AudiobookId,
        string SourcePath,
        PathIdentitySnapshot SourceIdentity,
        IReadOnlyList<MoveSourceManifestEntry> SourceEntries,
        string TargetPath,
        PathIdentitySnapshot TargetIdentity,
        int SourceBoundaryDirectoryObjectIdentityVersion,
        string SourceBoundaryDirectoryObjectIdentity,
        int TargetBoundaryDirectoryObjectIdentityVersion,
        string TargetBoundaryDirectoryObjectIdentity,
        bool DeleteEmptySource = true,
        string? SourceCleanupBoundary = null,
        Guid? RelocationId = null);

    public enum MoveHeartbeatOutcome
    {
        Renewed,
        Terminal,
        Lost
    }

    public sealed class MoveLeaseLostException(Guid jobId, int leaseGeneration)
        : InvalidOperationException($"Move job {jobId} no longer owns lease generation {leaseGeneration}.");

    public sealed record MoveRetryScheduleResult(
        MoveJobStatus Status,
        int AttemptCount,
        DateTimeOffset? NextAttemptAt);

    public interface IMoveQueueService
    {
        Task<Guid> EnqueueMoveAsync(
            MoveEnqueueCommand command,
            CancellationToken cancellationToken = default);
        Task<Guid?> RequeueMoveAsync(
            Guid jobId,
            CancellationToken cancellationToken = default);
        Task<int?> TryClaimJobAsync(Guid jobId, string leaseOwner, CancellationToken cancellationToken = default);
        Task<MoveHeartbeatOutcome> HeartbeatJobAsync(Guid jobId, string leaseOwner, int leaseGeneration, CancellationToken cancellationToken = default);
        Task RecoverActiveJobsAsync(CancellationToken cancellationToken = default);
        Task<IReadOnlyList<MoveJob>> GetActiveJobsAsync(CancellationToken cancellationToken = default);
        Task<MoveRecoveryState> GetRecoveryStateForAudiobookAsync(
            int audiobookId,
            CancellationToken cancellationToken = default);
        Task<IReadOnlyList<MoveJob>> GetFilesystemBlockingJobsAsync(
            CancellationToken cancellationToken = default);
        Task EnsureFilesystemMutationAllowedAsync(
            int audiobookId,
            CancellationToken cancellationToken = default,
            bool allowActiveDeletionIntent = false);
        Task<MoveQueueHealthSnapshot> GetQueueHealthAsync(CancellationToken cancellationToken = default);
        Task<MoveJob?> GetJobAsync(Guid id, CancellationToken cancellationToken = default);
        Task IncrementAttemptAsync(Guid id, string leaseOwner, int leaseGeneration, CancellationToken cancellationToken = default);
        Task<MoveRetryScheduleResult> ScheduleRetryAsync(
            Guid id,
            string leaseOwner,
            int leaseGeneration,
            string error,
            CancellationToken cancellationToken = default);
        Task<MoveRetryScheduleResult> ScheduleRetryWithoutNotificationAsync(
            Guid id,
            string leaseOwner,
            int leaseGeneration,
            string error,
            CancellationToken cancellationToken = default);
        Task UpdateJobStatusAsync(Guid id, string leaseOwner, int leaseGeneration, MoveJobStatus status, string? error = null, CancellationToken cancellationToken = default);
        Task UpdateJobStatusWithoutNotificationAsync(Guid id, string leaseOwner, int leaseGeneration, MoveJobStatus status, string? error = null, CancellationToken cancellationToken = default);
        Task NotifyPersistedJobStateAsync(Guid id, MoveJobStatus status, string? error = null, CancellationToken cancellationToken = default);
        Task PublishProgressAsync(
            Guid id,
            double progress,
            string phase,
            CancellationToken cancellationToken = default);
        System.Threading.Channels.ChannelReader<MoveJob> Reader { get; }
    }
}
