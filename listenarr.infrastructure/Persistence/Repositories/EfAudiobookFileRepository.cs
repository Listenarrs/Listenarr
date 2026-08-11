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
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories
{
    public partial class EfAudiobookFileRepository : IAudiobookFileRepository
    {
        private readonly ListenArrDbContext _db;

        public EfAudiobookFileRepository(ListenArrDbContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public async Task<AudiobookFile?> GetByIdAsync(int id, CancellationToken ct = default)
        {
            return await _db.AudiobookFiles.FindAsync(new object[] { id }, ct);
        }

        public async Task<List<AudiobookFile>> GetByAudiobookIdAsync(int audiobookId, CancellationToken ct = default)
        {
            return await _db.AudiobookFiles
                .AsNoTracking()
                .Where(f => f.AudiobookId == audiobookId)
                .ToListAsync(ct);
        }

        public async Task<List<AudiobookFile>> GetMissingMetadataAsync(int max, CancellationToken ct = default)
        {
            if (max <= 0)
            {
                return [];
            }

            var hostSyntax = OperatingSystem.IsWindows()
                ? FileSystemPathSyntax.Windows
                : FileSystemPathSyntax.Unix;
            var results = new List<AudiobookFile>(max);
            var lastSeenId = 0;
            var pageSize = Math.Max(1024, max * 8);

            while (results.Count < max)
            {
                var page = await _db.AudiobookFiles
                    .AsNoTracking()
                    .Where(f => f.Id > lastSeenId)
                    .Where(f => f.DurationSeconds == null || f.Format == null || f.SampleRate == null)
                    .Where(f => f.PathSyntax == null || f.PathSyntax == hostSyntax)
                    .OrderBy(f => f.Id)
                    .Take(pageSize)
                    .Select(f => new
                    {
                        File = f,
                        BasePath = f.Audiobook == null ? null : f.Audiobook.BasePath
                    })
                    .ToListAsync(ct);
                if (page.Count == 0)
                {
                    break;
                }

                foreach (var candidate in page)
                {
                    if (CanRescanOnCurrentHost(candidate.File, candidate.BasePath, hostSyntax))
                    {
                        results.Add(candidate.File);
                        if (results.Count == max)
                        {
                            break;
                        }
                    }
                }

                lastSeenId = page[^1].File.Id;
                if (page.Count < pageSize)
                {
                    break;
                }
            }

            return results;
        }

        private static bool CanRescanOnCurrentHost(
            AudiobookFile file,
            string? audiobookBasePath,
            FileSystemPathSyntax hostSyntax)
        {
            if (file.PathSyntax.HasValue)
            {
                return file.PathSyntax.Value == hostSyntax;
            }

            if (string.IsNullOrWhiteSpace(file.Path))
            {
                return false;
            }

            if (FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                    file.Path,
                    out var detectedSyntax))
            {
                return detectedSyntax == hostSyntax;
            }

            if (file.Path.StartsWith("//", StringComparison.Ordinal))
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(audiobookBasePath)
                && FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                    audiobookBasePath,
                    out var baseSyntax)
                && baseSyntax == hostSyntax;
        }

        internal async Task<AudiobookFile> AddUnresolvedForTestingAsync(
            AudiobookFile file,
            CancellationToken cancellationToken = default)
        {
            _db.AudiobookFiles.Add(file);
            await _db.SaveChangesAsync(cancellationToken);
            return file;
        }

        public async Task<AudiobookFileClaimResult> ClaimAsync(
            AudiobookFile file,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(file);
            if (file.PathIdentityState != PathIdentityState.Valid
                || string.IsNullOrWhiteSpace(file.PathIdentityLookupKey)
                || string.IsNullOrWhiteSpace(file.PathOwnershipKey))
            {
                return new AudiobookFileClaimResult(
                    AudiobookFileClaimOutcome.IdentityUnavailable,
                    Reason: file.PathIdentityReason ?? "A valid filesystem identity is required before claiming an audiobook file.");
            }

            var ownership = await CheckOwnershipAsync(
                file.AudiobookId,
                file.Id == 0 ? null : file.Id,
                ToIdentity(file),
                ct);
            var existingResult = ToClaimResult(ownership);
            if (existingResult != null)
            {
                return existingResult;
            }

            _db.AudiobookFiles.Add(file);
            try
            {
                await _db.SaveChangesAsync(ct);
                return new AudiobookFileClaimResult(
                    AudiobookFileClaimOutcome.Created,
                    file);
            }
            catch (UniqueConstraintViolationException)
            {
                _db.Entry(file).State = EntityState.Detached;
                var concurrentOwnership = await CheckOwnershipAsync(
                    file.AudiobookId,
                    null,
                    ToIdentity(file),
                    ct);
                return ToClaimResult(concurrentOwnership)
                    ?? new AudiobookFileClaimResult(
                        AudiobookFileClaimOutcome.IdentityConflict,
                        Reason: "The audiobook file ownership claim conflicted with another persistence operation.");
            }
        }

        public async Task<AudiobookFileOwnershipCheckResult> CheckOwnershipAsync(
            int audiobookId,
            int? fileId,
            AudiobookFilePathIdentity identity,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(identity);
            if (identity.State != PathIdentityState.Valid
                || string.IsNullOrWhiteSpace(identity.OwnershipKey))
            {
                return new AudiobookFileOwnershipCheckResult(
                    AudiobookFileOwnershipCheckOutcome.IdentityUnavailable,
                    Reason: identity.Reason ?? "A valid filesystem identity is required.");
            }

            var unresolvedCandidates = await _db.AudiobookFiles
                .AsNoTracking()
                .Where(candidate => candidate.PathIdentityLookupKey == identity.LookupKey)
                .Where(candidate => !fileId.HasValue || candidate.Id != fileId.Value)
                .Where(candidate => candidate.PathIdentityState != PathIdentityState.Valid)
                .OrderBy(candidate => candidate.Id)
                .ToListAsync(ct);
            var unresolved = unresolvedCandidates.FirstOrDefault(
                candidate => AudiobookFileOwnershipValidator.UnresolvedIdentityOverlaps(
                    candidate,
                    identity.Syntax,
                    identity.CaseSensitivity,
                    identity.CanonicalPath));
            if (unresolved != null)
            {
                var outcome = unresolved.PathIdentityState == PathIdentityState.Conflict
                    ? AudiobookFileOwnershipCheckOutcome.IdentityConflict
                    : AudiobookFileOwnershipCheckOutcome.IdentityUnavailable;
                return new AudiobookFileOwnershipCheckResult(
                    outcome,
                    unresolved,
                    unresolved.PathIdentityReason ?? "A legacy audiobook file identity requires operator resolution.");
            }

            var owner = await _db.AudiobookFiles
                .AsNoTracking()
                .Where(candidate => candidate.PathOwnershipKey == identity.OwnershipKey)
                .Where(candidate => !fileId.HasValue || candidate.Id != fileId.Value)
                .OrderBy(candidate => candidate.Id)
                .FirstOrDefaultAsync(ct);
            if (owner == null)
            {
                return new AudiobookFileOwnershipCheckResult(
                    AudiobookFileOwnershipCheckOutcome.Available);
            }

            return owner.AudiobookId == audiobookId
                ? new AudiobookFileOwnershipCheckResult(
                    AudiobookFileOwnershipCheckOutcome.AlreadyOwnedByAudiobook,
                    owner)
                : new AudiobookFileOwnershipCheckResult(
                    AudiobookFileOwnershipCheckOutcome.OwnedByOtherAudiobook,
                    owner);
        }

        public async Task UpdateAsync(AudiobookFile file, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(file);

            var entry = _db.Entry(file);
            if (entry.State == EntityState.Detached)
            {
                var existing = await _db.AudiobookFiles
                    .FirstOrDefaultAsync(candidate => candidate.Id == file.Id, ct);
                if (existing == null)
                {
                    return;
                }

                var existingEntry = _db.Entry(existing);
                var preservedAudiobookId = existing.AudiobookId;
                var preservedValues = PathIdentityPropertyNames.ToDictionary(
                    propertyName => propertyName,
                    propertyName => existingEntry.Property(propertyName).CurrentValue);
                existingEntry.CurrentValues.SetValues(file);
                existing.AudiobookId = preservedAudiobookId;
                foreach (var pair in preservedValues)
                {
                    existingEntry.Property(pair.Key).CurrentValue = pair.Value;
                }
            }
            else
            {
                var preservedAudiobookId = entry
                    .Property(nameof(AudiobookFile.AudiobookId))
                    .OriginalValue;
                file.AudiobookId = (int)preservedAudiobookId!;
                foreach (var propertyName in PathIdentityPropertyNames)
                {
                    entry.Property(propertyName).CurrentValue =
                        entry.Property(propertyName).OriginalValue;
                }
            }

            await _db.SaveChangesAsync(ct);
        }

        public async Task DeleteByAudiobookIdAsync(int audiobookId, CancellationToken ct = default)
        {
            var files = await _db.AudiobookFiles.Where(f => f.AudiobookId == audiobookId).ToListAsync(ct);
            _db.AudiobookFiles.RemoveRange(files);
            await _db.SaveChangesAsync(ct);
        }

        public async Task DeleteAsync(int id, CancellationToken ct = default)
        {
            var file = await _db.AudiobookFiles.FindAsync(new object[] { id }, ct);
            if (file != null)
            {
                _db.AudiobookFiles.Remove(file);
                await _db.SaveChangesAsync(ct);
            }
        }

        public async Task<List<string>> GetAllFilePathsAsync(
            FileSystemPathSemantics comparisonSemantics,
            CancellationToken ct = default)
        {
            var files = await _db.AudiobookFiles
                .AsNoTracking()
                .ToListAsync(ct);
            var audiobooks = await _db.Audiobooks
                .AsNoTracking()
                .ToListAsync(ct);
            return TrackedFilePathIndexBuilder.ResolvePaths(
                    files,
                    audiobooks,
                    comparisonSemantics)
                .ToList();
        }

        public async Task<List<AudiobookFile>> GetAllAsync(CancellationToken ct = default)
        {
            return await _db.AudiobookFiles
                .AsNoTracking()
                .ToListAsync(ct);
        }

        public Task<List<AudiobookFilePathReferenceSnapshot>> GetOtherPathReferenceSnapshotsAsync(
            int audiobookId,
            CancellationToken ct = default) =>
            _db.AudiobookFiles
                .AsNoTracking()
                .Where(file => file.AudiobookId != audiobookId)
                .Where(file => file.Path != null)
                .Select(file => new AudiobookFilePathReferenceSnapshot(
                    file.AudiobookId,
                    file.Path))
                .ToListAsync(ct);

        public async Task<List<AudiobookFormatSummary>> GetFormatSummariesAsync(CancellationToken ct = default)
        {
            var rows = await _db.AudiobookFiles
                .AsNoTracking()
                .GroupBy(f => new { f.AudiobookId, f.Format, f.Codec, f.Container })
                .Select(g => new
                {
                    g.Key.AudiobookId,
                    g.Key.Format,
                    g.Key.Codec,
                    g.Key.Container,
                    Bitrate = g.Max(f => f.Bitrate),
                    Path = g.Min(f => f.Path),
                })
                .ToListAsync(ct);

            return rows.Select(r => new AudiobookFormatSummary
            {
                AudiobookId = r.AudiobookId,
                Format = r.Format,
                Codec = r.Codec,
                Container = r.Container,
                Bitrate = r.Bitrate,
                Path = r.Path,
            }).ToList();
        }

        public async Task<Dictionary<int, int>> GetCountsByAudiobookIdAsync(CancellationToken ct = default)
        {
            return await _db.AudiobookFiles
                .AsNoTracking()
                .GroupBy(f => f.AudiobookId)
                .Select(g => new { AudiobookId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(r => r.AudiobookId, r => r.Count, ct);
        }

        private static readonly string[] PathIdentityPropertyNames =
        [
            nameof(AudiobookFile.Path),
            nameof(AudiobookFile.CanonicalPath),
            nameof(AudiobookFile.PathSyntax),
            nameof(AudiobookFile.PathCaseSensitivity),
            nameof(AudiobookFile.PathCaseSensitivityMode),
            nameof(AudiobookFile.PathIdentityBoundary),
            nameof(AudiobookFile.PathIdentityLookupKey),
            nameof(AudiobookFile.PathOwnershipKey),
            nameof(AudiobookFile.PathIdentityVersion),
            nameof(AudiobookFile.PathIdentityState),
            nameof(AudiobookFile.PathIdentityReason),
            nameof(AudiobookFile.PhysicalObjectIdentity),
            nameof(AudiobookFile.PhysicalIdentityVersion),
            nameof(AudiobookFile.PhysicalIdentityObservedAtUtc)
        ];

        private static AudiobookFilePathIdentity ToIdentity(AudiobookFile file) =>
            new(
                file.CanonicalPath!,
                file.PathSyntax!.Value,
                file.PathCaseSensitivity,
                file.PathCaseSensitivityMode,
                file.PathIdentityBoundary!,
                file.PathIdentityLookupKey!,
                file.PathOwnershipKey,
                file.PathIdentityVersion,
                file.PathIdentityState,
                file.PathIdentityReason);

        private static AudiobookFileClaimResult? ToClaimResult(
            AudiobookFileOwnershipCheckResult ownership) =>
            ownership.Outcome switch
            {
                AudiobookFileOwnershipCheckOutcome.Available => null,
                AudiobookFileOwnershipCheckOutcome.AlreadyOwnedByAudiobook => new AudiobookFileClaimResult(
                    AudiobookFileClaimOutcome.AlreadyOwnedByAudiobook,
                    ownership.ExistingFile,
                    ownership.Reason),
                AudiobookFileOwnershipCheckOutcome.OwnedByOtherAudiobook => new AudiobookFileClaimResult(
                    AudiobookFileClaimOutcome.OwnedByOtherAudiobook,
                    ownership.ExistingFile,
                    ownership.Reason),
                AudiobookFileOwnershipCheckOutcome.IdentityConflict => new AudiobookFileClaimResult(
                    AudiobookFileClaimOutcome.IdentityConflict,
                    ownership.ExistingFile,
                    ownership.Reason),
                AudiobookFileOwnershipCheckOutcome.IdentityUnavailable => new AudiobookFileClaimResult(
                    AudiobookFileClaimOutcome.IdentityUnavailable,
                    ownership.ExistingFile,
                    ownership.Reason),
                _ => throw new ArgumentOutOfRangeException(nameof(ownership))
            };
    }
}
