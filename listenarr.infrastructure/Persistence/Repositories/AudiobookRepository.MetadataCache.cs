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
        // Both cache tables carry a unique index on (NameNormalized, Region), and both upserts
        // below resolve an existing row with a read and then write in a separate statement. Two
        // callers that miss the read at the same time therefore both insert, and whichever one
        // reaches the index second gets a UNIQUE violation. Concurrent Audnexus author lookups
        // are enough to produce it: the losing write is dropped and that author's cache entry
        // goes unwritten for the cycle.
        //
        // Retrying closes the gap, because the second pass reads the row the winner committed
        // and takes the update path. Normally one retry does it; the bound is what guarantees
        // termination if some repeat we have not thought of keeps the read missing.
        //
        // INSERT ... ON CONFLICT DO UPDATE is not a drop-in here. It takes a single conflict
        // target, which can only be the unique (NameNormalized, Region) index, but the
        // resolution below is ASIN-first against an index that is deliberately not unique.
        // Finding the row that holds an ASIN whose name differs is not expressible as a conflict
        // clause on the name index, so the SQL form would quietly change what the method does.
        // It would also restate the field merge rules, JSON columns and value converters
        // included, somewhere they have to be kept in step by hand.
        //
        // BEGIN IMMEDIATE would make the read and the write atomic rather than detecting the
        // loss afterwards, and it would hold across processes. It is not used because EF's
        // BeginTransactionAsync issues a deferred BEGIN, so it means dropping to the raw
        // connection, and a later refactor that restores the deferred form turns the symptom
        // into SQLITE_BUSY without anything failing loudly. It also serializes every cache
        // upsert to fix a collision that is rare by construction.
        //
        // Two limits on the retry below, both deliberate.
        //
        // A blank normalized name is never retried. The key being written is blank too, and
        // re-reading it would mean resolving on the empty string, which merges authors whose
        // names differ only in punctuation onto one row. There is no correct row to find, so
        // that case keeps the behaviour it has today.
        //
        // SaveChangesAsync saves the whole scoped context rather than this entity alone, and
        // the translated exception carries no table or column, so a violation raised by another
        // pending entity in the same scope would be read as this one's. No caller shares a
        // scope that way today, and the bound limits the cost if one ever does.
        private const int CacheUpsertAttempts = 3;

        public async Task<AuthorCacheEntry> UpsertCachedAuthorAsync(AuthorCacheEntry authorCacheEntry)
        {
            ArgumentNullException.ThrowIfNull(authorCacheEntry);

            var normalizedName = NormalizeAuthorName(authorCacheEntry.AuthorName);
            var normalizedRegion = AudiobookIdentifierNormalizer.NormalizeRegion(authorCacheEntry.Region) ?? "us";
            var normalizedAsin = NormalizeAsin(authorCacheEntry.AuthorAsin);

            for (var attempt = 1; ; attempt++)
            {
                AuthorCacheEntry? existing = null;

                if (!string.IsNullOrWhiteSpace(normalizedAsin))
                {
                    existing = await _db.AuthorCacheEntries.FirstOrDefaultAsync(entry =>
                        entry.AuthorAsin != null &&
                        entry.AuthorAsin.ToUpper() == normalizedAsin &&
                        entry.Region == normalizedRegion);
                }

                if (existing == null && !string.IsNullOrWhiteSpace(normalizedName))
                {
                    existing = await _db.AuthorCacheEntries.FirstOrDefaultAsync(entry =>
                        entry.AuthorNameNormalized == normalizedName &&
                        entry.Region == normalizedRegion);
                }

                var now = DateTime.UtcNow;
                var inserting = existing == null;
                if (existing == null)
                {
                    existing = new AuthorCacheEntry
                    {
                        CreatedAt = now
                    };

                    _db.AuthorCacheEntries.Add(existing);
                }

                existing.AuthorName = string.IsNullOrWhiteSpace(authorCacheEntry.AuthorName)
                    ? (string.IsNullOrWhiteSpace(existing.AuthorName) ? normalizedName : existing.AuthorName)
                    : authorCacheEntry.AuthorName.Trim();
                existing.AuthorNameNormalized = string.IsNullOrWhiteSpace(normalizedName)
                    ? NormalizeAuthorName(existing.AuthorName)
                    : normalizedName;
                existing.AuthorAsin = string.IsNullOrWhiteSpace(normalizedAsin)
                    ? existing.AuthorAsin
                    : normalizedAsin;
                existing.Region = normalizedRegion;
                existing.ImageUrl = authorCacheEntry.ImageUrl ?? existing.ImageUrl;
                existing.Description = authorCacheEntry.Description ?? existing.Description;

                if (authorCacheEntry.SimilarAuthors != null)
                {
                    existing.SimilarAuthors = authorCacheEntry.SimilarAuthors;
                }

                if (authorCacheEntry.CatalogBooks != null)
                {
                    existing.CatalogBooks = authorCacheEntry.CatalogBooks;
                }

                existing.LastFetchedAt = authorCacheEntry.LastFetchedAt ?? existing.LastFetchedAt ?? now;
                existing.UpdatedAt = now;

                try
                {
                    await _db.SaveChangesAsync();
                    return existing;
                }
                // Only an insert that lost a race is retried, and only when the key it wrote can
                // be looked up again. A violation on the update path is the by-ASIN lookup having
                // resolved a row that cannot take the incoming name; the deterministic form of
                // that would resolve the same row every pass and spin, so it surfaces instead.
                catch (UniqueConstraintViolationException) when (
                    inserting
                    && !string.IsNullOrWhiteSpace(normalizedName)
                    && attempt < CacheUpsertAttempts)
                {
                    _db.Entry(existing).State = EntityState.Detached;
                }
            }
        }

        public async Task<SeriesCacheEntry> UpsertCachedSeriesAsync(SeriesCacheEntry seriesCacheEntry)
        {
            ArgumentNullException.ThrowIfNull(seriesCacheEntry);

            var normalizedName = NormalizeSeriesName(seriesCacheEntry.SeriesName);
            var normalizedRegion = AudiobookIdentifierNormalizer.NormalizeRegion(seriesCacheEntry.Region) ?? "us";
            var normalizedAsin = NormalizeAsin(seriesCacheEntry.SeriesAsin);

            for (var attempt = 1; ; attempt++)
            {
                SeriesCacheEntry? existing = null;

                if (!string.IsNullOrWhiteSpace(normalizedAsin))
                {
                    existing = await _db.SeriesCacheEntries.FirstOrDefaultAsync(entry =>
                        entry.SeriesAsin != null &&
                        entry.SeriesAsin.ToUpper() == normalizedAsin &&
                        entry.Region == normalizedRegion);
                }

                if (existing == null && !string.IsNullOrWhiteSpace(normalizedName))
                {
                    existing = await _db.SeriesCacheEntries.FirstOrDefaultAsync(entry =>
                        entry.SeriesNameNormalized == normalizedName &&
                        entry.Region == normalizedRegion);
                }

                var now = DateTime.UtcNow;
                var inserting = existing == null;
                if (existing == null)
                {
                    existing = new SeriesCacheEntry
                    {
                        CreatedAt = now
                    };

                    _db.SeriesCacheEntries.Add(existing);
                }

                existing.SeriesName = string.IsNullOrWhiteSpace(seriesCacheEntry.SeriesName)
                    ? (string.IsNullOrWhiteSpace(existing.SeriesName) ? normalizedName : existing.SeriesName)
                    : seriesCacheEntry.SeriesName.Trim();
                existing.SeriesNameNormalized = string.IsNullOrWhiteSpace(normalizedName)
                    ? NormalizeSeriesName(existing.SeriesName)
                    : normalizedName;
                existing.SeriesAsin = string.IsNullOrWhiteSpace(normalizedAsin)
                    ? existing.SeriesAsin
                    : normalizedAsin;
                existing.Region = normalizedRegion;
                existing.ImageUrl = seriesCacheEntry.ImageUrl ?? existing.ImageUrl;
                existing.Description = seriesCacheEntry.Description ?? existing.Description;

                if (seriesCacheEntry.CatalogBooks != null)
                {
                    existing.CatalogBooks = seriesCacheEntry.CatalogBooks;
                }

                existing.LastFetchedAt = seriesCacheEntry.LastFetchedAt ?? existing.LastFetchedAt ?? now;
                existing.UpdatedAt = now;

                try
                {
                    await _db.SaveChangesAsync();
                    return existing;
                }
                catch (UniqueConstraintViolationException) when (
                    inserting
                    && !string.IsNullOrWhiteSpace(normalizedName)
                    && attempt < CacheUpsertAttempts)
                {
                    _db.Entry(existing).State = EntityState.Detached;
                }
            }
        }
    }
}
