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

namespace Listenarr.Application.Audiobooks.Contracts.Repositories
{
    public sealed record AudiobookBasePathMutation(
        int AudiobookId,
        string? ExpectedCurrentBasePath,
        string? ResultingBasePath);

    public sealed record AudiobookFilePathReferenceSnapshot(
        int AudiobookId,
        string? Path);

    public sealed record AudiobookFilePhysicalGenerationSnapshot(
        long? Size,
        double? DurationSeconds,
        string? Format,
        string? Container,
        string? Codec,
        int? Bitrate,
        int? SampleRate,
        int? Channels,
        string? Source,
        string? PhysicalObjectIdentity,
        int PhysicalIdentityVersion,
        DateTime? PhysicalIdentityObservedAtUtc);

    public sealed record AudiobookFileMetadataRefreshSnapshot(
        int FileId,
        int AudiobookId,
        AudiobookFilePathState PathState,
        string? BasePath)
    {
        public static AudiobookFileMetadataRefreshSnapshot Capture(
            AudiobookFile file,
            string? basePath) =>
            new(file.Id, file.AudiobookId, file.CapturePathState(), basePath);
    }

    public sealed record AudiobookFileRescanCandidate(
        AudiobookFile File,
        string? AudiobookBasePath);

    // The three rescan retry counters of one file, as stored.
    public readonly record struct AudiobookFileRescanCounts(int Failure, int Unavailable, int Blocked);

    // One persisted rescan outcome: the new counters, the next due time and the outcome name.
    public sealed record AudiobookFileRescanScheduleWrite(
        AudiobookFileRescanCounts Counts,
        DateTime DueUtc,
        string LastOutcome);

    public sealed record AudiobookFileRescanForeignRow(int Id, int UnavailableCount);

    public enum AudiobookFileRescanRefreshRecord
    {
        NotRecorded,
        Incomplete,
        Completed
    }

    public interface IAudiobookFileRepository
    {
        Task<AudiobookFile?> GetByIdAsync(int id, CancellationToken ct = default);
        Task<List<AudiobookFile>> GetByAudiobookIdAsync(int audiobookId, CancellationToken ct = default);
        Task<List<AudiobookFile>> GetMissingMetadataAsync(int max, CancellationToken ct = default);
        Task<List<AudiobookFileRescanCandidate>> GetMetadataRescanDuePageAsync(
            DateTime nowUtc,
            IReadOnlyCollection<int> excludedIds,
            int pageSize,
            CancellationToken ct = default);
        /// <summary>
        /// Write one rescan outcome only if the file's three counters still equal
        /// <paramref name="expectedCounts"/>. False means another write changed the
        /// schedule first (or the row is gone) and nothing was written.
        /// </summary>
        Task<bool> ApplyMetadataRescanScheduleAsync(
            int fileId,
            AudiobookFileRescanCounts expectedCounts,
            AudiobookFileRescanScheduleWrite write,
            CancellationToken ct = default);
        /// <summary>
        /// Record the outcome of a refresh that updated the row: <paramref name="incompleteWrite"/>
        /// while the row still misses metadata and its counters equal <paramref name="expectedCounts"/>,
        /// otherwise Completed (counters zeroed, due time kept) once the row no longer misses metadata.
        /// </summary>
        Task<AudiobookFileRescanRefreshRecord> RecordMetadataRescanRefreshedAsync(
            int fileId,
            AudiobookFileRescanCounts expectedCounts,
            AudiobookFileRescanScheduleWrite incompleteWrite,
            CancellationToken ct = default);
        /// <summary>
        /// The oldest due rows that miss metadata and whose stored path syntax is not
        /// <paramref name="hostSyntax"/>, in due order, at most <paramref name="bound"/>.
        /// </summary>
        Task<List<AudiobookFileRescanForeignRow>> GetDueForeignSyntaxRescanRowsAsync(
            DateTime nowUtc,
            FileSystemPathSyntax hostSyntax,
            int bound,
            CancellationToken ct = default);
        /// <summary>
        /// Move the given foreign-syntax rows out on the unavailable ladder as NotOnThisHost,
        /// skipping any row whose schedule or path syntax changed since it was read.
        /// Returns the number of rows written.
        /// </summary>
        Task<int> DeferForeignSyntaxRescanRowsAsync(
            IReadOnlyCollection<AudiobookFileRescanForeignRow> rows,
            DateTime nowUtc,
            FileSystemPathSyntax hostSyntax,
            CancellationToken ct = default);
        Task<AudiobookFileClaimResult> ClaimAsync(AudiobookFile file, CancellationToken ct = default);
        Task<AudiobookFileClaimResult> ClaimWithBasePathAsync(
            AudiobookFile file,
            AudiobookBasePathMutation basePathMutation,
            CancellationToken ct = default);
        Task<bool> ApplyBasePathAsync(
            AudiobookBasePathMutation basePathMutation,
            CancellationToken ct = default);
        Task<AudiobookFileOwnershipCheckResult> CheckOwnershipAsync(
            int audiobookId,
            int? fileId,
            AudiobookFilePathIdentity identity,
            CancellationToken ct = default);
        Task UpdateAsync(AudiobookFile file, CancellationToken ct = default);
        /// <summary>
        /// Update metadata only if the complete tracked ownership snapshot and base path still match.
        /// Identity and ownership fields are never written by this operation. <paramref name="size"/>
        /// is the byte length read from the same handle the metadata came from; null leaves the
        /// stored size alone.
        /// </summary>
        Task<bool> RefreshMetadataAsync(
            AudiobookFileMetadataRefreshSnapshot expectedFile,
            AudioMetadata metadata,
            long? size,
            CancellationToken ct = default);
        Task<bool> ReconcilePathIdentityAsync(
            int fileId,
            int audiobookId,
            AudiobookFilePathState expectedPathState,
            string storedPath,
            AudiobookFilePathIdentity identity,
            CancellationToken ct = default);
        Task<bool> ReplacePhysicalGenerationAsync(
            int fileId,
            int audiobookId,
            string? expectedPath,
            string? expectedPhysicalObjectIdentity,
            AudiobookFile replacement,
            CancellationToken ct = default);
        Task<bool> ReplacePhysicalGenerationWithBasePathAsync(
            int fileId,
            int audiobookId,
            string? expectedPath,
            string? expectedPhysicalObjectIdentity,
            AudiobookFile replacement,
            AudiobookBasePathMutation basePathMutation,
            CancellationToken ct = default);
        Task<bool> RestorePhysicalGenerationAsync(
            int fileId,
            int audiobookId,
            string? expectedPath,
            string? expectedPhysicalObjectIdentity,
            AudiobookFilePhysicalGenerationSnapshot predecessor,
            CancellationToken ct = default);
        Task<bool> RestorePhysicalGenerationWithBasePathAsync(
            int fileId,
            int audiobookId,
            string? expectedPath,
            string? expectedPhysicalObjectIdentity,
            AudiobookFilePhysicalGenerationSnapshot predecessor,
            AudiobookBasePathMutation basePathMutation,
            CancellationToken ct = default);
        Task<bool> DeletePhysicalGenerationAsync(
            int fileId,
            int audiobookId,
            string? expectedPath,
            string? expectedPhysicalObjectIdentity,
            CancellationToken ct = default);
        Task<bool> DeletePathStateAsync(
            int fileId,
            int audiobookId,
            AudiobookFilePathState expectedPathState,
            CancellationToken ct = default);
        Task<bool> DeletePhysicalGenerationWithBasePathAsync(
            int fileId,
            int audiobookId,
            string? expectedPath,
            string? expectedPhysicalObjectIdentity,
            AudiobookBasePathMutation basePathMutation,
            CancellationToken ct = default);
        Task DeleteByAudiobookIdAsync(int audiobookId, CancellationToken ct = default);
        Task DeleteAsync(int id, CancellationToken ct = default);
        Task<List<string>> GetAllFilePathsAsync(
            FileSystemPathSemantics comparisonSemantics,
            CancellationToken ct = default);
        Task<List<AudiobookFile>> GetAllAsync(CancellationToken ct = default);
        Task<List<AudiobookFilePathReferenceSnapshot>> GetOtherPathReferenceSnapshotsAsync(
            int audiobookId,
            CancellationToken ct = default);
        Task<List<AudiobookFormatSummary>> GetFormatSummariesAsync(CancellationToken ct = default);
        Task<Dictionary<int, int>> GetCountsByAudiobookIdAsync(CancellationToken ct = default);
    }
}
