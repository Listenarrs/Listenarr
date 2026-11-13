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
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories
{
    public partial class AudiobookRepository
    {
        public async Task<List<Audiobook>> GetMetadataBackfillCandidatesAsync(
            int max,
            DateTime retryBeforeUtc,
            CancellationToken ct = default)
        {
            if (max <= 0)
            {
                return new List<Audiobook>();
            }

            // Only the string columns are queryable here; the JSON list columns
            // (narrators, genres) are checked in memory by the fill step, which
            // runs anyway once one of these triggers the lookup.
            return await _db.Audiobooks
                .AsNoTracking()
                .Include(a => a.ExternalIdentifiers)
                .Where(a => a.Files!.Any())
                .Where(a => a.MetadataBackfillAttemptedAt == null || a.MetadataBackfillAttemptedAt < retryBeforeUtc)
                .Where(a => (a.Asin != null && a.Asin != string.Empty)
                    || a.ExternalIdentifiers!.Any(i =>
                        i.Type == AudiobookExternalIdentifierType.Asin
                        || i.Type == AudiobookExternalIdentifierType.Isbn))
                .Where(a => a.Description == null || a.Description == string.Empty
                    || a.ImageUrl == null || a.ImageUrl == string.Empty
                    || a.Publisher == null || a.Publisher == string.Empty
                    || a.Language == null || a.Language == string.Empty
                    || a.PublishedDate == null || a.PublishedDate == string.Empty)
                .OrderBy(a => a.MetadataBackfillAttemptedAt == null ? 0 : 1)
                .ThenBy(a => a.MetadataBackfillAttemptedAt)
                .ThenBy(a => a.Id)
                .Take(max)
                .ToListAsync(ct);
        }

        public async Task SetMetadataBackfillAttemptedAtAsync(
            int audiobookId,
            DateTime attemptedAtUtc,
            CancellationToken ct = default)
        {
            // Same detached-stub targeted write as SetLastSearchTimeAsync: the
            // worker holds a sweep-start snapshot and must not re-save it over
            // concurrent edits, and the stub path also runs on the InMemory
            // test provider (ExecuteUpdate does not).
            var tracked = _db.Audiobooks.Local.FirstOrDefault(a => a.Id == audiobookId);
            if (tracked != null)
            {
                _db.Entry(tracked).State = EntityState.Detached;
            }

            var stub = new Audiobook { Id = audiobookId, MetadataBackfillAttemptedAt = attemptedAtUtc };
            var entry = _db.Entry(stub);
            entry.Property(a => a.MetadataBackfillAttemptedAt).IsModified = true;

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            finally
            {
                entry.State = EntityState.Detached;
            }
        }
    }
}
