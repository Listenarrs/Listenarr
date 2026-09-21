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
        public async Task<AuthorCacheEntry> UpsertCachedAuthorAsync(AuthorCacheEntry authorCacheEntry)
        {
            ArgumentNullException.ThrowIfNull(authorCacheEntry);

            var normalizedName = NormalizeAuthorName(authorCacheEntry.AuthorName);
            var normalizedRegion = AudiobookIdentifierNormalizer.NormalizeRegion(authorCacheEntry.Region) ?? "us";
            var normalizedAsin = NormalizeAsin(authorCacheEntry.AuthorAsin);

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

            await _db.SaveChangesAsync();
            return existing;
        }

        public async Task<SeriesCacheEntry> UpsertCachedSeriesAsync(SeriesCacheEntry seriesCacheEntry)
        {
            ArgumentNullException.ThrowIfNull(seriesCacheEntry);

            var normalizedName = NormalizeSeriesName(seriesCacheEntry.SeriesName);
            var normalizedRegion = AudiobookIdentifierNormalizer.NormalizeRegion(seriesCacheEntry.Region) ?? "us";
            var normalizedAsin = NormalizeAsin(seriesCacheEntry.SeriesAsin);

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

            await _db.SaveChangesAsync();
            return existing;
        }
    }
}
