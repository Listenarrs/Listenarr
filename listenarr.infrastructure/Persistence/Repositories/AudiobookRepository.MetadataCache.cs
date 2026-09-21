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
        // Retrying is what closes the gap, because the second pass reads the row the winner just
        // committed and takes the update path. The alternative, an INSERT ... ON CONFLICT DO
        // UPDATE, would have to restate the field-by-field merge rules below in SQL, including
        // the JSON columns that EF serializes through value converters, so the merge would then
        // live in two places that have to agree.
        //
        // One retry is enough once a winner exists. The bound is here so that a row deleted
        // between two attempts cannot spin.
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
                // Only an insert that lost a race is retried. A violation on the update path means
                // the row this resolved to cannot hold the incoming key, which re-reading will not
                // change, so that one still surfaces to the caller.
                catch (UniqueConstraintViolationException) when (inserting && attempt < CacheUpsertAttempts)
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
                catch (UniqueConstraintViolationException) when (inserting && attempt < CacheUpsertAttempts)
                {
                    _db.Entry(existing).State = EntityState.Detached;
                }
            }
        }
    }
}
