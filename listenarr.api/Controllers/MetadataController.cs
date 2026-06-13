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
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Application.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace Listenarr.Api.Controllers
{
    [ApiController]
    [Route("api/v{version:apiVersion}/metadata")]
    [Tags("Metadata")]
    public class MetadataController : ControllerBase
    {
        private readonly IAudiobookMetadataService _metadataService;
        private readonly ILogger<MetadataController> _logger;
        private readonly AudibleService _audibleService;
        private readonly IAudnexusService _audnexusService;
        private readonly IImageCacheService _imageCacheService;
        private readonly IMemoryCache _cache;
        private readonly IAudiobookRepository _audiobookRepository;
        private readonly IAsinLookupService _asinLookupService;
        private readonly IAuthorCatalogService _authorCatalogService;
        private readonly ISeriesCatalogService _seriesCatalogService;
        private readonly MetadataImageCacheWorkflow _imageCacheWorkflow;
        private readonly MetadataLookupCacheWorkflow _lookupCacheWorkflow;
        private readonly MetadataLookupResponseCache _lookupResponseCache;

        public MetadataController(
            IAudiobookMetadataService metadataService,
            AudibleService audibleService,
            IAudnexusService audnexusService,
            IImageCacheService imageCacheService,
            IMemoryCache cache,
            IAudiobookRepository audiobookRepository,
            IAsinLookupService asinLookupService,
            IAuthorCatalogService authorCatalogService,
            ISeriesCatalogService seriesCatalogService,
            ILogger<MetadataController> logger)
        {
            _metadataService = metadataService;
            _audibleService = audibleService;
            _audnexusService = audnexusService;
            _imageCacheService = imageCacheService;
            _cache = cache;
            _audiobookRepository = audiobookRepository;
            _asinLookupService = asinLookupService;
            _authorCatalogService = authorCatalogService;
            _seriesCatalogService = seriesCatalogService;
            _logger = logger;
            _imageCacheWorkflow = new MetadataImageCacheWorkflow(_audiobookRepository, _imageCacheService, _logger);
            _lookupCacheWorkflow = new MetadataLookupCacheWorkflow(_audiobookRepository, _imageCacheService, _imageCacheWorkflow, _logger);
            _lookupResponseCache = new MetadataLookupResponseCache(_cache);
        }

        /// <summary>
        /// Get audiobook metadata from configured metadata sources by ASIN.
        /// </summary>
        [HttpGet("{asin}")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<object>> GetMetadata(
            string asin,
            [FromQuery] string region = "us",
            [FromQuery] bool cache = true)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(asin))
                {
                    return BadRequest("ASIN is required");
                }

                var result = await _metadataService.GetMetadataAsync(asin, region, cache);
                if (result == null)
                {
                    return NotFound($"No metadata found for ASIN: {asin}");
                }

                return Ok(result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error fetching metadata for ASIN: {Asin}", asin);
                return StatusCode(500, $"Error fetching metadata: {ex.Message}");
            }
        }

        /// <summary>
        /// Get audiobook metadata from the Audible-backed catalog provider by ASIN.
        /// </summary>
        [HttpGet("audible/{asin}")]
        [ProducesResponseType(typeof(AudibleBookResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<AudibleBookResponse>> GetAudibleMetadata(
            string asin,
            [FromQuery] string region = "us",
            [FromQuery] bool cache = true)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(asin))
                {
                    return BadRequest("ASIN parameter is required");
                }

                var result = await _metadataService.GetAudibleMetadataAsync(asin, region, cache);
                if (result == null)
                {
                    return NotFound($"No metadata found for ASIN: {asin}");
                }

                return Ok(result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error fetching Audible metadata for ASIN: {Asin}", asin);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Resolve an ASIN from an ISBN value.
        /// </summary>
        [HttpGet("asin-from-isbn/{isbn}")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetAsinFromIsbn(string isbn, CancellationToken ct)
        {
            var result = await _asinLookupService.GetAsinFromIsbnAsync(isbn, ct);
            if (!result.Success)
            {
                return NotFound(new { success = false, error = result.Error ?? "ASIN not found" });
            }

            return Ok(new { success = true, asin = result.Asin });
        }

        /// <summary>
        /// Lookup an author by name via Audible, prefer cached portraits, and enrich with biography and similar authors.
        /// </summary>
        [HttpGet("author")]
        [ProducesResponseType(typeof(AuthorLookupResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public Task<ActionResult<AuthorLookupResponse>> LookupAuthor(
            [FromQuery] string name,
            [FromQuery] string region = "us",
            [FromQuery] string? asin = null)
        {
            return LookupAuthorCore(name, region, asin, refresh: false);
        }

        /// <summary>
        /// Refresh an author lookup by name via Audible, bypassing cached data.
        /// </summary>
        [HttpPost("author/refresh")]
        [ProducesResponseType(typeof(AuthorLookupResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public Task<ActionResult<AuthorLookupResponse>> RefreshAuthor([FromBody] AuthorLookupRefreshRequest? request)
        {
            return LookupAuthorCore(
                request?.Name ?? string.Empty,
                request?.Region ?? "us",
                request?.Asin,
                refresh: true);
        }

        private async Task<ActionResult<AuthorLookupResponse>> LookupAuthorCore(
            string name,
            string region,
            string? asin,
            bool refresh)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name)) return BadRequest("Author name is required");

                var normalizedName = name.Trim();
                var normalizedAsin = string.IsNullOrWhiteSpace(asin) ? null : asin.Trim();
                var cacheKey = MetadataCacheKeys.BuildAuthorLookupCacheKey(region, normalizedName, normalizedAsin);
                string? seededName = null;
                string? seededImage = null;
                string? seededDescription = null;
                string? seededCachedPath = null;
                var seededSimilarAuthors = new List<RelatedAuthorItem>();

                if (refresh)
                {
                    _cache.Remove(cacheKey);
                }
                else if (_cache.TryGetValue(cacheKey, out MetadataAuthorLookupCacheEntry? cachedEntry) && cachedEntry != null)
                {
                    cachedEntry.Asin ??= normalizedAsin;

                    // If previously marked NotFound, try to resolve an ASIN from the DB and check cache by ASIN
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

                            return Ok(_lookupResponseCache.MapAuthorLookupResponse(cachedEntry, normalizedName));
                        }

                        return NotFound("Author not found");
                    }

                    string? cachedPath = cachedEntry.CachedPath;
                    if (!string.IsNullOrWhiteSpace(cachedEntry.Asin))
                    {
                        cachedPath = await _imageCacheWorkflow.ResolveCachedImagePathAsync(cachedEntry.Asin) ?? cachedPath;
                    }

                    cachedEntry.CachedPath = cachedPath;

                    if (MetadataResponseMapper.HasCompleteAuthorLookupData(cachedEntry.CachedPath, cachedEntry.Description, cachedEntry.SimilarAuthors))
                    {
                        return Ok(_lookupResponseCache.MapAuthorLookupResponse(cachedEntry, normalizedName));
                    }

                    normalizedAsin ??= cachedEntry.Asin;
                    seededName = cachedEntry.Name;
                    seededImage = cachedEntry.Image;
                    seededDescription = cachedEntry.Description;
                    seededCachedPath = cachedPath;
                    seededSimilarAuthors = cachedEntry.SimilarAuthors?
                        .Where(author => !string.IsNullOrWhiteSpace(author.Name))
                        .ToList() ?? new List<RelatedAuthorItem>();
                }

                var persistedEntry = await _lookupCacheWorkflow.ResolvePersistedAuthorCacheAsync(normalizedName, region, normalizedAsin);
                if (persistedEntry != null)
                {
                    var persistedResponse = await _lookupCacheWorkflow.MapPersistedAuthorLookupResponseAsync(persistedEntry, normalizedName);
                    if (!refresh &&
                        MetadataResponseMapper.HasCompleteAuthorLookupData(persistedResponse.CachedPath, persistedResponse.Description, persistedResponse.SimilarAuthors))
                    {
                        _lookupResponseCache.CacheAuthorLookupResponse(cacheKey, persistedResponse);
                        return Ok(persistedResponse);
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
                    // Audible returned nothing — try Audnexus as fallback
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

                    return NotFound("Author not found");
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

                        // Attempt to ensure author image is cached under authors storage.
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

                var result = new AuthorLookupResponse
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

                return Ok(result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error looking up author: {Name}", name);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Fetch the full catalog for an author using Audible's author/books flow.
        /// </summary>
        [HttpGet("author/books")]
        [ProducesResponseType(typeof(AuthorCatalogResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public Task<ActionResult<AuthorCatalogResponse>> GetAuthorBooks(
            [FromQuery] string name,
            [FromQuery] string region = "us",
            [FromQuery] int limit = 250)
        {
            return GetAuthorBooksCore(name, region, limit, refresh: false);
        }

        /// <summary>
        /// Refresh the full catalog for an author using Audible's author/books flow.
        /// </summary>
        [HttpPost("author/books/refresh")]
        [ProducesResponseType(typeof(AuthorCatalogResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public Task<ActionResult<AuthorCatalogResponse>> RefreshAuthorBooks([FromBody] CatalogRefreshRequest? request)
        {
            return GetAuthorBooksCore(
                request?.Name ?? string.Empty,
                request?.Region ?? "us",
                request?.Limit ?? 250,
                refresh: true);
        }

        private async Task<ActionResult<AuthorCatalogResponse>> GetAuthorBooksCore(
            string name,
            string region,
            int limit,
            bool refresh)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name)) return BadRequest("Author name is required");

                var normalizedName = name.Trim();
                var catalog = await _authorCatalogService.GetCatalogAsync(
                    normalizedName,
                    region,
                    limit,
                    language: null,
                    forceRefresh: refresh);

                if (catalog == null || string.IsNullOrWhiteSpace(catalog.Author.Asin))
                {
                    return NotFound("Author not found");
                }

                return Ok(new AuthorCatalogResponse
                {
                    Author = new AuthorCatalogAuthorInfo
                    {
                        Asin = catalog.Author.Asin,
                        Name = string.IsNullOrWhiteSpace(catalog.Author.Name) ? normalizedName : catalog.Author.Name,
                        Image = catalog.Author.Image
                    },
                    Books = catalog.Books.Select(MetadataResponseMapper.MapAuthorCatalogBook).ToList(),
                    TotalBooks = catalog.TotalBooks
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error fetching author catalog for {Name}", name);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Lookup a series by name via Audible, preferring cached series metadata and images.
        /// </summary>
        [HttpGet("series")]
        [ProducesResponseType(typeof(SeriesLookupResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public Task<ActionResult<SeriesLookupResponse>> LookupSeries(
            [FromQuery] string name,
            [FromQuery] string region = "us",
            [FromQuery] string? asin = null)
        {
            return LookupSeriesCore(name, region, asin, refresh: false);
        }

        /// <summary>
        /// Refresh a series lookup by name via Audible, bypassing cached data.
        /// </summary>
        [HttpPost("series/refresh")]
        [ProducesResponseType(typeof(SeriesLookupResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public Task<ActionResult<SeriesLookupResponse>> RefreshSeries([FromBody] SeriesLookupRefreshRequest? request)
        {
            return LookupSeriesCore(
                request?.Name ?? string.Empty,
                request?.Region ?? "us",
                request?.Asin,
                refresh: true);
        }

        private async Task<ActionResult<SeriesLookupResponse>> LookupSeriesCore(
            string name,
            string region,
            string? asin,
            bool refresh)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name)) return BadRequest("Series name is required");

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
                    return Ok(_lookupResponseCache.MapSeriesLookupResponse(cachedEntry, normalizedName));
                }

                var persistedEntry = await _lookupCacheWorkflow.ResolvePersistedSeriesCacheAsync(normalizedName, region, normalizedAsin);
                if (!refresh && persistedEntry != null)
                {
                    var persistedResponse = await _lookupCacheWorkflow.MapPersistedSeriesLookupResponseAsync(persistedEntry, normalizedName);
                    _lookupResponseCache.CacheSeriesLookupResponse(cacheKey, persistedResponse);
                    return Ok(persistedResponse);
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
                    return NotFound("Series not found");
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

                var result = new SeriesLookupResponse
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

                return Ok(result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error looking up series: {Name}", name);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Fetch the full catalog for a series using Audible's series/books flow.
        /// </summary>
        [HttpGet("series/books")]
        [ProducesResponseType(typeof(SeriesCatalogResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public Task<ActionResult<SeriesCatalogResponse>> GetSeriesBooks(
            [FromQuery] string name,
            [FromQuery] string region = "us",
            [FromQuery] int limit = 250)
        {
            return GetSeriesBooksCore(name, region, limit, refresh: false);
        }

        /// <summary>
        /// Refresh the full catalog for a series using Audible's series/books flow.
        /// </summary>
        [HttpPost("series/books/refresh")]
        [ProducesResponseType(typeof(SeriesCatalogResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public Task<ActionResult<SeriesCatalogResponse>> RefreshSeriesBooks([FromBody] CatalogRefreshRequest? request)
        {
            return GetSeriesBooksCore(
                request?.Name ?? string.Empty,
                request?.Region ?? "us",
                request?.Limit ?? 250,
                refresh: true);
        }

        private async Task<ActionResult<SeriesCatalogResponse>> GetSeriesBooksCore(
            string name,
            string region,
            int limit,
            bool refresh)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name)) return BadRequest("Series name is required");

                var normalizedName = name.Trim();
                var catalog = await _seriesCatalogService.GetCatalogAsync(
                    normalizedName,
                    region,
                    limit,
                    language: null,
                    forceRefresh: refresh);

                if (catalog == null || string.IsNullOrWhiteSpace(catalog.Series.Asin))
                {
                    return NotFound("Series not found");
                }

                return Ok(new SeriesCatalogResponse
                {
                    Series = new SeriesCatalogInfo
                    {
                        Asin = catalog.Series.Asin,
                        Name = string.IsNullOrWhiteSpace(catalog.Series.Name) ? normalizedName : catalog.Series.Name,
                        Image = catalog.Series.Image,
                        Description = catalog.Series.Description
                    },
                    Books = catalog.Books.Select(MetadataResponseMapper.MapSeriesCatalogBook).ToList(),
                    TotalBooks = catalog.TotalBooks
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error fetching series catalog for {Name}", name);
                return StatusCode(500, "Internal server error");
            }
        }

        public sealed class AuthorLookupResponse
        {
            public string? Asin { get; set; }
            public string Name { get; set; } = string.Empty;
            public string? Image { get; set; }
            public string? CachedPath { get; set; }
            public string? Description { get; set; }
            public List<RelatedAuthorItem> SimilarAuthors { get; set; } = new();
        }

        public sealed class AuthorLookupRefreshRequest
        {
            public string Name { get; set; } = string.Empty;
            public string Region { get; set; } = "us";
            public string? Asin { get; set; }
        }

        public sealed class RelatedAuthorItem
        {
            public string? Asin { get; set; }
            public string Name { get; set; } = string.Empty;
        }

        public sealed class SeriesLookupResponse
        {
            public string? Asin { get; set; }
            public string Name { get; set; } = string.Empty;
            public string? Image { get; set; }
            public string? CachedPath { get; set; }
            public string? Description { get; set; }
            public int TotalBooks { get; set; }
        }

        public sealed class SeriesLookupRefreshRequest
        {
            public string Name { get; set; } = string.Empty;
            public string Region { get; set; } = "us";
            public string? Asin { get; set; }
        }

        public sealed class AuthorCatalogResponse
        {
            public AuthorCatalogAuthorInfo Author { get; set; } = new();
            public List<AuthorCatalogBookItem> Books { get; set; } = new();
            public int TotalBooks { get; set; }
        }

        public sealed class CatalogRefreshRequest
        {
            public string Name { get; set; } = string.Empty;
            public string Region { get; set; } = "us";
            public int Limit { get; set; } = 250;
        }

        public sealed class AuthorCatalogAuthorInfo
        {
            public string? Asin { get; set; }
            public string Name { get; set; } = string.Empty;
            public string? Image { get; set; }
        }

        public sealed class AuthorCatalogBookItem
        {
            public string? Asin { get; set; }
            public string Title { get; set; } = string.Empty;
            public string? Subtitle { get; set; }
            public List<string> Authors { get; set; } = new();
            public string? ImageUrl { get; set; }
            public int? Runtime { get; set; }
            public string? Language { get; set; }
            public string? Publisher { get; set; }
            public List<string> Narrators { get; set; } = new();
            public List<string> Genres { get; set; } = new();
            public string? Series { get; set; }
            public string? SeriesNumber { get; set; }
            public string? PublishedDate { get; set; }
            public string? Isbn { get; set; }
            public string? Link { get; set; }
            public string? MetadataSource { get; set; }
        }

        public sealed class SeriesCatalogResponse
        {
            public SeriesCatalogInfo Series { get; set; } = new();
            public List<SeriesCatalogBookItem> Books { get; set; } = new();
            public int TotalBooks { get; set; }
        }

        public sealed class SeriesCatalogInfo
        {
            public string? Asin { get; set; }
            public string Name { get; set; } = string.Empty;
            public string? Image { get; set; }
            public string? Description { get; set; }
        }

        public sealed class SeriesCatalogBookItem
        {
            public string? Asin { get; set; }
            public string Title { get; set; } = string.Empty;
            public string? Subtitle { get; set; }
            public List<string> Authors { get; set; } = new();
            public string? ImageUrl { get; set; }
            public int? Runtime { get; set; }
            public string? Language { get; set; }
            public string? Publisher { get; set; }
            public List<string> Narrators { get; set; } = new();
            public List<string> Genres { get; set; } = new();
            public string? Series { get; set; }
            public string? SeriesNumber { get; set; }
            public string? PublishedDate { get; set; }
            public string? Isbn { get; set; }
            public string? Link { get; set; }
            public string? MetadataSource { get; set; }
        }
    }
}
