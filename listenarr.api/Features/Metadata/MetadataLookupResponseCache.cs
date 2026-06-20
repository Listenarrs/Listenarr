/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Microsoft.Extensions.Caching.Memory;

namespace Listenarr.Api.Features.Metadata
{
    internal sealed class MetadataLookupResponseCache
    {
        private readonly IMemoryCache _cache;

        public MetadataLookupResponseCache(IMemoryCache cache)
        {
            _cache = cache;
        }

        public void CacheAuthorLookupResponse(string cacheKey, MetadataController.AuthorLookupResponse response)
        {
            _cache.Set(cacheKey, new MetadataAuthorLookupCacheEntry
            {
                Asin = response.Asin,
                Name = response.Name,
                Image = response.Image,
                CachedPath = response.CachedPath,
                Description = response.Description,
                SimilarAuthors = response.SimilarAuthors,
                NotFound = false
            }, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(12) });
        }

        public void CacheAuthorNotFound(string cacheKey, string normalizedName)
        {
            _cache.Set(cacheKey, new MetadataAuthorLookupCacheEntry
            {
                NotFound = true,
                Name = normalizedName
            }, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(6) });
        }

        public void CacheSeriesLookupResponse(string cacheKey, MetadataController.SeriesLookupResponse response)
        {
            _cache.Set(cacheKey, new MetadataSeriesLookupCacheEntry
            {
                Asin = response.Asin,
                Name = response.Name,
                Image = response.Image,
                CachedPath = response.CachedPath,
                Description = response.Description,
                TotalBooks = response.TotalBooks
            }, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(12) });
        }

        public MetadataController.AuthorLookupResponse MapAuthorLookupResponse(MetadataAuthorLookupCacheEntry entry, string fallbackName)
        {
            return new MetadataController.AuthorLookupResponse
            {
                Asin = entry.Asin,
                Name = entry.Name ?? fallbackName,
                Image = entry.Image,
                CachedPath = entry.CachedPath,
                Description = entry.Description,
                SimilarAuthors = entry.SimilarAuthors ?? new List<MetadataController.RelatedAuthorItem>()
            };
        }

        public MetadataController.SeriesLookupResponse MapSeriesLookupResponse(MetadataSeriesLookupCacheEntry entry, string fallbackName)
        {
            return new MetadataController.SeriesLookupResponse
            {
                Asin = entry.Asin,
                Name = entry.Name ?? fallbackName,
                Image = entry.Image,
                CachedPath = entry.CachedPath,
                Description = entry.Description,
                TotalBooks = entry.TotalBooks
            };
        }
    }

    internal sealed class MetadataAuthorLookupCacheEntry
    {
        public string? Asin { get; set; }
        public string? Name { get; set; }
        public string? Image { get; set; }
        public string? CachedPath { get; set; }
        public string? Description { get; set; }
        public List<MetadataController.RelatedAuthorItem>? SimilarAuthors { get; set; }
        public bool NotFound { get; set; }
    }

    internal sealed class MetadataSeriesLookupCacheEntry
    {
        public string? Asin { get; set; }
        public string? Name { get; set; }
        public string? Image { get; set; }
        public string? CachedPath { get; set; }
        public string? Description { get; set; }
        public int TotalBooks { get; set; }
    }
}
