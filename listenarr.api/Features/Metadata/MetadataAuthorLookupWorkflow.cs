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
    internal enum MetadataAuthorLookupStatus
    {
        Ok,
        BadRequest,
        NotFound,
        Error
    }

    internal sealed record MetadataAuthorLookupResult(
        MetadataAuthorLookupStatus Status,
        MetadataController.AuthorLookupResponse? Response,
        string? Message)
    {
        public static MetadataAuthorLookupResult Ok(MetadataController.AuthorLookupResponse response) =>
            new(MetadataAuthorLookupStatus.Ok, response, null);

        public static MetadataAuthorLookupResult BadRequest(string message) =>
            new(MetadataAuthorLookupStatus.BadRequest, null, message);

        public static MetadataAuthorLookupResult NotFound(string message) =>
            new(MetadataAuthorLookupStatus.NotFound, null, message);

        public static MetadataAuthorLookupResult Error(string message) =>
            new(MetadataAuthorLookupStatus.Error, null, message);
    }

    internal sealed class MetadataAuthorLookupWorkflow
    {
        private readonly AudibleService _audibleService;
        private readonly IAudnexusService _audnexusService;
        private readonly IImageCacheService _imageCacheService;
        private readonly IMemoryCache _cache;
        private readonly MetadataImageCacheWorkflow _imageCacheWorkflow;
        private readonly MetadataLookupCacheWorkflow _lookupCacheWorkflow;
        private readonly MetadataLookupResponseCache _lookupResponseCache;
        private readonly ILogger _logger;

        public MetadataAuthorLookupWorkflow(
            AudibleService audibleService,
            IAudnexusService audnexusService,
            IImageCacheService imageCacheService,
            IMemoryCache cache,
            MetadataImageCacheWorkflow imageCacheWorkflow,
            MetadataLookupCacheWorkflow lookupCacheWorkflow,
            MetadataLookupResponseCache lookupResponseCache,
            ILogger logger)
        {
            _audibleService = audibleService;
            _audnexusService = audnexusService;
            _imageCacheService = imageCacheService;
            _cache = cache;
            _imageCacheWorkflow = imageCacheWorkflow;
            _lookupCacheWorkflow = lookupCacheWorkflow;
            _lookupResponseCache = lookupResponseCache;
            _logger = logger;
        }

        public async Task<MetadataAuthorLookupResult> LookupAsync(
            string name,
            string region,
            string? asin,
            bool refresh)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name)) return MetadataAuthorLookupResult.BadRequest("Author name is required");

                var normalizedName = name.Trim();
                var normalizedAsin = string.IsNullOrWhiteSpace(asin) ? null : asin.Trim();
                var cacheKey = MetadataCacheKeys.BuildAuthorLookupCacheKey(region, normalizedName, normalizedAsin);
                string? seededName = null;
                string? seededImage = null;
                string? seededDescription = null;
                string? seededCachedPath = null;
                var seededSimilarAuthors = new List<MetadataController.RelatedAuthorItem>();

                if (refresh)
                {
                    _cache.Remove(cacheKey);
                }
                else if (_cache.TryGetValue(cacheKey, out MetadataAuthorLookupCacheEntry? cachedEntry) && cachedEntry != null)
                {
                    cachedEntry.Asin ??= normalizedAsin;

                    if (cachedEntry.NotFound)
                    {
                        var notFoundCacheProbe = await _imageCacheWorkflow.ProbeAuthorImageCacheAsync(normalizedName, region, cachedEntry.Asin);
                        if (!string.IsNullOrWhiteSpace(notFoundCacheProbe.CachedPath))
                        {
                            cachedEntry.Asin = notFoundCacheProbe.Asin ?? cachedEntry.Asin;
                            cachedEntry.CachedPath = notFoundCacheProbe.CachedPath;
                            cachedEntry.Name ??= normalizedName;
                            cachedEntry.NotFound = false;
                            _cache.Set(cacheKey, cachedEntry, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(12) });

                            return MetadataAuthorLookupResult.Ok(_lookupResponseCache.MapAuthorLookupResponse(cachedEntry, normalizedName));
                        }

                        return MetadataAuthorLookupResult.NotFound("Author not found");
                    }

                    string? cachedPath = cachedEntry.CachedPath;
                    if (!string.IsNullOrWhiteSpace(cachedEntry.Asin))
                    {
                        cachedPath = await _imageCacheWorkflow.ResolveCachedImagePathAsync(cachedEntry.Asin) ?? cachedPath;
                    }

                    cachedEntry.CachedPath = cachedPath;

                    if (MetadataResponseMapper.HasCompleteAuthorLookupData(cachedEntry.CachedPath, cachedEntry.Description, cachedEntry.SimilarAuthors))
                    {
                        return MetadataAuthorLookupResult.Ok(_lookupResponseCache.MapAuthorLookupResponse(cachedEntry, normalizedName));
                    }

                    normalizedAsin ??= cachedEntry.Asin;
                    seededName = cachedEntry.Name;
                    seededImage = cachedEntry.Image;
                    seededDescription = cachedEntry.Description;
                    seededCachedPath = cachedPath;
                    seededSimilarAuthors = cachedEntry.SimilarAuthors?
                        .Where(author => !string.IsNullOrWhiteSpace(author.Name))
                        .ToList() ?? new List<MetadataController.RelatedAuthorItem>();
                }

                var persistedEntry = await _lookupCacheWorkflow.ResolvePersistedAuthorCacheAsync(normalizedName, region, normalizedAsin);
                if (persistedEntry != null)
                {
                    var persistedResponse = await _lookupCacheWorkflow.MapPersistedAuthorLookupResponseAsync(persistedEntry, normalizedName);
                    if (!refresh &&
                        MetadataResponseMapper.HasCompleteAuthorLookupData(persistedResponse.CachedPath, persistedResponse.Description, persistedResponse.SimilarAuthors))
                    {
                        _lookupResponseCache.CacheAuthorLookupResponse(cacheKey, persistedResponse);
                        return MetadataAuthorLookupResult.Ok(persistedResponse);
                    }

                    normalizedAsin ??= persistedResponse.Asin;
                    seededName ??= persistedResponse.Name;
                    seededImage ??= persistedResponse.Image;
                    seededDescription ??= persistedResponse.Description;
                    seededCachedPath ??= persistedResponse.CachedPath;
                    if (seededSimilarAuthors.Count == 0 && persistedResponse.SimilarAuthors.Count > 0)
                    {
                        seededSimilarAuthors = persistedResponse.SimilarAuthors
                            .Where(author => !string.IsNullOrWhiteSpace(author.Name))
                            .ToList();
                    }
                }

                var cacheHint = await _imageCacheWorkflow.ProbeAuthorImageCacheAsync(normalizedName, region, normalizedAsin);
                var resolvedAsin = normalizedAsin ?? cacheHint.Asin;
                var cached = seededCachedPath ?? cacheHint.CachedPath;
                var needsDescription = refresh || string.IsNullOrWhiteSpace(seededDescription);
                var needsSimilarAuthors = refresh || seededSimilarAuthors.Count == 0;
                var needsCachedImage = refresh || string.IsNullOrWhiteSpace(cached);
                var needsAuthorDetails = string.IsNullOrWhiteSpace(resolvedAsin) ||
                    string.IsNullOrWhiteSpace(seededName) ||
                    string.IsNullOrWhiteSpace(seededImage) ||
                    needsDescription ||
                    needsCachedImage ||
                    refresh;

                AuthorLookupItem? info = null;
                AuthorLookupItem? authorDetails = null;
                string? resolvedName = seededName;
                string? resolvedImage = seededImage;
                string? resolvedDescription = seededDescription;

                if (!string.IsNullOrWhiteSpace(resolvedAsin) && needsAuthorDetails)
                {
                    authorDetails = await _audibleService.GetAuthorByAsinAsync(resolvedAsin, region);
                }

                if (authorDetails == null && needsAuthorDetails)
                {
                    info = await _audibleService.LookupAuthorAsync(normalizedName, region);
                }

                resolvedAsin ??= authorDetails?.Asin ?? info?.Asin;

                if (authorDetails == null && !string.IsNullOrWhiteSpace(resolvedAsin) && needsAuthorDetails)
                {
                    authorDetails = await _audibleService.GetAuthorByAsinAsync(resolvedAsin, region);
                }

                resolvedName ??= authorDetails?.Name ?? info?.Name;

                var audibleImage = authorDetails?.Image ?? info?.Image;
                if (!string.IsNullOrWhiteSpace(audibleImage) &&
                    (string.IsNullOrWhiteSpace(resolvedImage) || needsCachedImage))
                {
                    resolvedImage = audibleImage;
                }

                var audibleDescription = authorDetails?.Description ?? info?.Description;
                if (!string.IsNullOrWhiteSpace(audibleDescription))
                {
                    resolvedDescription = audibleDescription;
                }

                AudnexusAuthorSearchResult? audnexusSearchAuthor = null;
                AudnexusAuthorResponse? audnexusAuthor = null;
                var shouldQueryAudnexus =
                    refresh ||
                    string.IsNullOrWhiteSpace(resolvedAsin) ||
                    string.IsNullOrWhiteSpace(resolvedName) ||
                    string.IsNullOrWhiteSpace(resolvedDescription) ||
                    string.IsNullOrWhiteSpace(resolvedImage) ||
                    needsSimilarAuthors ||
                    (needsCachedImage && string.IsNullOrWhiteSpace(audibleImage));

                if (!string.IsNullOrWhiteSpace(resolvedAsin) && shouldQueryAudnexus)
                {
                    try
                    {
                        audnexusAuthor = await _audnexusService.GetAuthorAsync(resolvedAsin, region, update: false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Audnexus author details fallback failed for '{Author}'", normalizedName);
                    }
                }

                if (shouldQueryAudnexus && (authorDetails == null || audnexusAuthor == null || string.IsNullOrWhiteSpace(resolvedDescription)))
                {
                    try
                    {
                        var audnexResults = await _audnexusService.SearchAuthorsAsync(normalizedName, region);
                        audnexusSearchAuthor = audnexResults?.FirstOrDefault(a =>
                            !string.IsNullOrWhiteSpace(a.Name) &&
                            a.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase))
                            ?? audnexResults?.FirstOrDefault(a =>
                                !string.IsNullOrWhiteSpace(a.Asin) &&
                                string.Equals(a.Asin, resolvedAsin, StringComparison.OrdinalIgnoreCase))
                            ?? audnexResults?.FirstOrDefault();

                        if (audnexusSearchAuthor != null)
                        {
                            resolvedAsin ??= audnexusSearchAuthor.Asin;
                            resolvedName ??= audnexusSearchAuthor.Name;
                            resolvedImage ??= audnexusSearchAuthor.Image;
                            resolvedDescription ??= audnexusSearchAuthor.Description;

                            if (audnexusAuthor == null && !string.IsNullOrWhiteSpace(audnexusSearchAuthor.Asin))
                            {
                                audnexusAuthor = await _audnexusService.GetAuthorAsync(audnexusSearchAuthor.Asin, region, update: false);
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Audnexus author fallback failed for '{Author}'", normalizedName);
                    }
                }

                if (audnexusAuthor != null)
                {
                    resolvedAsin ??= audnexusAuthor.Asin;
                    resolvedName ??= audnexusAuthor.Name;
                    resolvedDescription ??= audnexusAuthor.Description;
                }

                var audnexusImage = audnexusAuthor?.Image ?? audnexusSearchAuthor?.Image;
                if (!string.IsNullOrWhiteSpace(audnexusImage) &&
                    (string.IsNullOrWhiteSpace(resolvedImage) ||
                        (needsCachedImage && string.IsNullOrWhiteSpace(audibleImage))))
                {
                    resolvedImage = audnexusImage;
                }

                var hasResolvedAuthorIdentity =
                    !string.IsNullOrWhiteSpace(resolvedAsin) ||
                    !string.IsNullOrWhiteSpace(authorDetails?.Name) ||
                    !string.IsNullOrWhiteSpace(info?.Name) ||
                    !string.IsNullOrWhiteSpace(audnexusAuthor?.Name) ||
                    !string.IsNullOrWhiteSpace(audnexusSearchAuthor?.Name);

                if (!hasResolvedAuthorIdentity)
                {
                    _lookupResponseCache.CacheAuthorNotFound(cacheKey, normalizedName);
                    return MetadataAuthorLookupResult.NotFound("Author not found");
                }

                resolvedName ??=
                    authorDetails?.Name ??
                    info?.Name ??
                    audnexusAuthor?.Name ??
                    audnexusSearchAuthor?.Name ??
                    normalizedName;

                try
                {
                    if (!refresh &&
                        string.IsNullOrWhiteSpace(cached) &&
                        !string.IsNullOrWhiteSpace(resolvedAsin))
                    {
                        cached = await _imageCacheWorkflow.ResolveCachedImagePathAsync(resolvedAsin);
                    }

                    if ((refresh || string.IsNullOrWhiteSpace(cached)) &&
                        !string.IsNullOrWhiteSpace(resolvedAsin))
                    {
                        var preferredImageForCaching =
                            authorDetails?.Image ??
                            info?.Image ??
                            audnexusAuthor?.Image ??
                            audnexusSearchAuthor?.Image ??
                            resolvedImage;

                        cached = await _imageCacheService.MoveToAuthorLibraryStorageAsync(
                            resolvedAsin,
                            preferredImageForCaching,
                            forceRefresh: refresh);
                        if (!string.IsNullOrWhiteSpace(cached)) cached = "/" + cached.TrimStart('/');
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to cache author image for {Author}", name);
                }

                var similarAuthors = MetadataResponseMapper.MapSimilarAuthors(
                    audnexusAuthor?.Similar ?? audnexusSearchAuthor?.Similar,
                    normalizedName);
                if (similarAuthors.Count == 0 && seededSimilarAuthors.Count > 0)
                {
                    similarAuthors = seededSimilarAuthors;
                }

                var result = new MetadataController.AuthorLookupResponse
                {
                    Asin = resolvedAsin,
                    Name = resolvedName,
                    Image = resolvedImage,
                    CachedPath = cached,
                    Description = resolvedDescription,
                    SimilarAuthors = similarAuthors
                };

                await _lookupCacheWorkflow.PersistAuthorLookupAsync(
                    persistedEntry,
                    normalizedName,
                    region,
                    result);

                _lookupResponseCache.CacheAuthorLookupResponse(cacheKey, result);
                _lookupResponseCache.CacheAuthorLookupResponse(MetadataCacheKeys.BuildAuthorLookupCacheKey(region, normalizedName, result.Asin), result);

                return MetadataAuthorLookupResult.Ok(result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error looking up author: {Name}", name);
                return MetadataAuthorLookupResult.Error("Internal server error");
            }
        }
    }
}
