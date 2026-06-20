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
    internal enum MetadataSeriesLookupStatus
    {
        Ok,
        BadRequest,
        NotFound,
        Error
    }

    internal sealed record MetadataSeriesLookupResult(
        MetadataSeriesLookupStatus Status,
        MetadataController.SeriesLookupResponse? Response,
        string? Message)
    {
        public static MetadataSeriesLookupResult Ok(MetadataController.SeriesLookupResponse response) =>
            new(MetadataSeriesLookupStatus.Ok, response, null);

        public static MetadataSeriesLookupResult BadRequest(string message) =>
            new(MetadataSeriesLookupStatus.BadRequest, null, message);

        public static MetadataSeriesLookupResult NotFound(string message) =>
            new(MetadataSeriesLookupStatus.NotFound, null, message);

        public static MetadataSeriesLookupResult Error(string message) =>
            new(MetadataSeriesLookupStatus.Error, null, message);
    }

    internal sealed class MetadataSeriesLookupWorkflow
    {
        private readonly AudibleService _audibleService;
        private readonly IImageCacheService _imageCacheService;
        private readonly ISeriesCatalogService _seriesCatalogService;
        private readonly IMemoryCache _cache;
        private readonly MetadataImageCacheWorkflow _imageCacheWorkflow;
        private readonly MetadataLookupCacheWorkflow _lookupCacheWorkflow;
        private readonly MetadataLookupResponseCache _lookupResponseCache;
        private readonly ILogger _logger;

        public MetadataSeriesLookupWorkflow(
            AudibleService audibleService,
            IImageCacheService imageCacheService,
            ISeriesCatalogService seriesCatalogService,
            IMemoryCache cache,
            MetadataImageCacheWorkflow imageCacheWorkflow,
            MetadataLookupCacheWorkflow lookupCacheWorkflow,
            MetadataLookupResponseCache lookupResponseCache,
            ILogger logger)
        {
            _audibleService = audibleService;
            _imageCacheService = imageCacheService;
            _seriesCatalogService = seriesCatalogService;
            _cache = cache;
            _imageCacheWorkflow = imageCacheWorkflow;
            _lookupCacheWorkflow = lookupCacheWorkflow;
            _lookupResponseCache = lookupResponseCache;
            _logger = logger;
        }

        public async Task<MetadataSeriesLookupResult> LookupAsync(
            string name,
            string region,
            string? asin,
            bool refresh)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name)) return MetadataSeriesLookupResult.BadRequest("Series name is required");

                var normalizedName = name.Trim();
                var normalizedAsin = string.IsNullOrWhiteSpace(asin) ? null : asin.Trim();
                var cacheKey = $"series-lookup:{region}:{normalizedName.ToLowerInvariant()}";

                if (refresh)
                {
                    _cache.Remove(cacheKey);
                }
                else if (_cache.TryGetValue(cacheKey, out MetadataSeriesLookupCacheEntry? cachedEntry) && cachedEntry != null)
                {
                    cachedEntry.Asin ??= normalizedAsin;
                    return MetadataSeriesLookupResult.Ok(_lookupResponseCache.MapSeriesLookupResponse(cachedEntry, normalizedName));
                }

                var persistedEntry = await _lookupCacheWorkflow.ResolvePersistedSeriesCacheAsync(normalizedName, region, normalizedAsin);
                if (!refresh && persistedEntry != null)
                {
                    var persistedResponse = await _lookupCacheWorkflow.MapPersistedSeriesLookupResponseAsync(persistedEntry, normalizedName);
                    _lookupResponseCache.CacheSeriesLookupResponse(cacheKey, persistedResponse);
                    return MetadataSeriesLookupResult.Ok(persistedResponse);
                }

                normalizedAsin ??= persistedEntry?.SeriesAsin;

                var resolvedSeries = !string.IsNullOrWhiteSpace(normalizedAsin)
                    ? await _audibleService.GetSeriesByAsinAsync(normalizedAsin, region)
                    : null;

                resolvedSeries ??= await _audibleService.LookupSeriesAsync(normalizedName, region);
                normalizedAsin ??= resolvedSeries?.Asin;

                if (resolvedSeries == null && !string.IsNullOrWhiteSpace(normalizedAsin))
                {
                    resolvedSeries = await _audibleService.GetSeriesByAsinAsync(normalizedAsin, region);
                }

                if (resolvedSeries == null)
                {
                    return MetadataSeriesLookupResult.NotFound("Series not found");
                }

                var resolvedSeriesName = string.IsNullOrWhiteSpace(resolvedSeries.Name)
                    ? normalizedName
                    : resolvedSeries.Name;

                var catalog = await _seriesCatalogService.GetCatalogAsync(
                    resolvedSeriesName,
                    region,
                    limit: 250,
                    language: null,
                    forceRefresh: refresh);

                var imageUrl =
                    resolvedSeries.Image ??
                    catalog?.Books.FirstOrDefault(book => !string.IsNullOrWhiteSpace(book.ImageUrl))?.ImageUrl ??
                    persistedEntry?.ImageUrl;

                string? cachedPath = null;
                if (!string.IsNullOrWhiteSpace(resolvedSeries.Asin))
                {
                    cachedPath = await _imageCacheWorkflow.ResolveCachedImagePathAsync(resolvedSeries.Asin);

                    if ((refresh || string.IsNullOrWhiteSpace(cachedPath)) && !string.IsNullOrWhiteSpace(imageUrl))
                    {
                        try
                        {
                            cachedPath = await _imageCacheService.MoveToSeriesLibraryStorageAsync(
                                resolvedSeries.Asin,
                                imageUrl,
                                forceRefresh: refresh);
                            if (!string.IsNullOrWhiteSpace(cachedPath))
                            {
                                cachedPath = "/" + cachedPath.TrimStart('/');
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogWarning(ex, "Failed to cache series image for {Series}", normalizedName);
                        }
                    }
                }

                var result = new MetadataController.SeriesLookupResponse
                {
                    Asin = resolvedSeries.Asin,
                    Name = resolvedSeriesName,
                    Image = imageUrl,
                    CachedPath = cachedPath,
                    Description = resolvedSeries.Description ?? persistedEntry?.Description,
                    TotalBooks = catalog?.TotalBooks ?? persistedEntry?.CatalogBooks?.Count ?? 0
                };

                await _lookupCacheWorkflow.PersistSeriesLookupAsync(
                    persistedEntry,
                    normalizedName,
                    region,
                    result,
                    catalog?.Books);

                _lookupResponseCache.CacheSeriesLookupResponse(cacheKey, result);

                return MetadataSeriesLookupResult.Ok(result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error looking up series: {Name}", name);
                return MetadataSeriesLookupResult.Error("Internal server error");
            }
        }
    }
}
