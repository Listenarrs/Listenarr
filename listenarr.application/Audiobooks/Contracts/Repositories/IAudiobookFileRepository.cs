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
        string? PhysicalObjectIdentity,
        int PhysicalIdentityVersion,
        DateTime? PhysicalIdentityObservedAtUtc,
        string? BasePath)
    {
        public static AudiobookFileMetadataRefreshSnapshot Capture(AudiobookFile file, string? basePath) =>
            new(file.Id, file.AudiobookId, file.CapturePathState(), file.PhysicalObjectIdentity,
                file.PhysicalIdentityVersion, file.PhysicalIdentityObservedAtUtc, basePath);
    }

    public interface IAudiobookFileRepository
    {
        Task<AudiobookFile?> GetByIdAsync(int id, CancellationToken ct = default);
        Task<List<AudiobookFile>> GetByAudiobookIdAsync(int audiobookId, CancellationToken ct = default);
        Task<List<AudiobookFile>> GetMissingMetadataAsync(int max, CancellationToken ct = default);
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
        /// Identity and ownership fields are never written by this operation.
        /// </summary>
        Task<bool> RefreshMetadataAsync(
            AudiobookFileMetadataRefreshSnapshot expectedFile,
            AudioMetadata metadata,
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
