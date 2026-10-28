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
    public sealed record AudiobookPathReferenceSnapshot(
        int AudiobookId,
        string? BasePath,
        string? FilePath);

    public interface IAudiobookRepository
    {
        Task<List<Audiobook>> GetAllAsync();
        Task<AudiobookPathReferenceSnapshot?> GetPathReferenceSnapshotAsync(
            int audiobookId,
            CancellationToken ct = default);
        Task<List<AudiobookPathReferenceSnapshot>> GetOtherPathReferenceSnapshotsAsync(
            int audiobookId,
            CancellationToken ct = default);
        Task<List<Audiobook>> GetLibraryAsync();
        Task<Dictionary<int, List<AudiobookSeriesMembership>>> GetAllSeriesMembershipsGroupedByAudiobookIdAsync(CancellationToken ct = default);
        Task<List<Audiobook>> GetByIdsWithFilesAsync(IEnumerable<int> ids, CancellationToken ct = default);
        Task<List<Audiobook>> GetMonitoredAudiobooksForSearchAsync(DateTime cutoff, CancellationToken ct = default);
        Task NormalizeJsonColumnsAsync(CancellationToken ct = default);
        Task<Audiobook?> GetByAsinAsync(string asin);
        Task<Audiobook?> GetByIsbnAsync(string isbn);
        Task<Audiobook?> GetByIdAsync(int id);
        Task<Audiobook?> GetByIdSnapshotAsync(int id, CancellationToken ct = default);
        Task<Audiobook?> GetForUpdateSnapshotAsync(int id, CancellationToken ct = default);
        Task<Audiobook?> GetForScanAsync(int id, CancellationToken ct = default);
        Task<Audiobook?> GetForScanSnapshotAsync(int id, CancellationToken ct = default);
        Task<bool> TryUpdateBasePathAsync(
            int audiobookId,
            string expectedBasePath,
            string newBasePath,
            CancellationToken ct = default);
        Task<bool> TryUpdateImageUrlAsync(
            int audiobookId,
            string? expectedImageUrl,
            string? newImageUrl,
            CancellationToken ct = default) => Task.FromResult(false);
        Task<string?> GetAuthorAsinByNameAsync(string name);
        Task<AuthorCacheEntry?> GetCachedAuthorByNameAsync(string name, string region);
        Task<AuthorCacheEntry?> GetCachedAuthorByAsinAsync(string asin, string region);
        Task<AuthorCacheEntry> UpsertCachedAuthorAsync(AuthorCacheEntry authorCacheEntry);
        Task<SeriesCacheEntry?> GetCachedSeriesByNameAsync(string name, string region);
        Task<SeriesCacheEntry?> GetCachedSeriesByAsinAsync(string asin, string region);
        Task<SeriesCacheEntry> UpsertCachedSeriesAsync(SeriesCacheEntry seriesCacheEntry);
        Task<Audiobook> AddAsync(Audiobook audiobook);
        Task<bool> UpdateAsync(Audiobook audiobook);
        Task<bool> RewritePathReferencesAsync(
            int audiobookId,
            string? sourceBasePath,
            string targetBasePath,
            FileSystemPathSemantics sourceSemantics,
            FileSystemPathSemantics targetSemantics,
            CancellationToken ct = default,
            FileSystemCaseSensitivityMode targetCaseSensitivityMode = FileSystemCaseSensitivityMode.Auto);
        Task<bool> RewriteMovedPathReferencesAsync(
            int audiobookId,
            string? sourceBasePath,
            string targetBasePath,
            FileSystemPathSemantics sourceSemantics,
            FileSystemPathSemantics targetSemantics,
            IReadOnlyDictionary<string, string> targetPhysicalObjectIdentities,
            DateTime targetPhysicalIdentityObservedAtUtc,
            CancellationToken ct = default,
            FileSystemCaseSensitivityMode targetCaseSensitivityMode = FileSystemCaseSensitivityMode.Auto);
        Task<bool> DeleteByIdAsync(int id);
        Task SaveChangesAsync(CancellationToken ct = default);
        Task<bool> UpdateWithIdentifierReplaceAsync(Audiobook audiobook, List<AudiobookExternalIdentifier> newIdentifiers, CancellationToken ct = default);
    }
}
