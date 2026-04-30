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
                var cacheKey = BuildAuthorLookupCacheKey(region, normalizedName, normalizedAsin);
                string? seededName = null;
                string? seededImage = null;
                string? seededDescription = null;
                string? seededCachedPath = null;
                var seededSimilarAuthors = new List<RelatedAuthorItem>();

                if (refresh)
                {
                    _cache.Remove(cacheKey);
                }
                else if (_cache.TryGetValue(cacheKey, out AuthorLookupCacheEntry? cachedEntry) && cachedEntry != null)
                {
                    cachedEntry.Asin ??= normalizedAsin;

                    // If previously marked NotFound, try to resolve an ASIN from the DB and check cache by ASIN
                    if (cachedEntry.NotFound)
                    {
                        var notFoundCacheProbe = await ProbeAuthorImageCacheAsync(normalizedName, region, cachedEntry.Asin);
                        if (!string.IsNullOrWhiteSpace(notFoundCacheProbe.CachedPath))
                        {
                            cachedEntry.Asin = notFoundCacheProbe.Asin ?? cachedEntry.Asin;
                            cachedEntry.CachedPath = notFoundCacheProbe.CachedPath;
                            cachedEntry.Name ??= normalizedName;
                            cachedEntry.NotFound = false;
                            _cache.Set(cacheKey, cachedEntry, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(12) });

                            return Ok(MapAuthorLookupResponse(cachedEntry, normalizedName));
                        }

                        return NotFound("Author not found");
                    }

                    string? cachedPath = cachedEntry.CachedPath;
                    if (!string.IsNullOrWhiteSpace(cachedEntry.Asin))
                    {
                        cachedPath = await ResolveCachedImagePathAsync(cachedEntry.Asin) ?? cachedPath;
                    }

                    cachedEntry.CachedPath = cachedPath;

                    if (HasCompleteAuthorLookupData(cachedEntry.CachedPath, cachedEntry.Description, cachedEntry.SimilarAuthors))
                    {
                        return Ok(MapAuthorLookupResponse(cachedEntry, normalizedName));
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

                var persistedEntry = await ResolvePersistedAuthorCacheAsync(normalizedName, region, normalizedAsin);
                if (persistedEntry != null)
                {
                    var persistedResponse = await MapPersistedAuthorLookupResponseAsync(persistedEntry, normalizedName);
                    if (!refresh &&
                        HasCompleteAuthorLookupData(persistedResponse.CachedPath, persistedResponse.Description, persistedResponse.SimilarAuthors))
                    {
                        CacheAuthorLookupResponse(cacheKey, persistedResponse);
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

                var cacheHint = await ProbeAuthorImageCacheAsync(normalizedName, region, normalizedAsin);
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
                    _cache.Set(cacheKey, new AuthorLookupCacheEntry
                    {
                        NotFound = true,
                        Name = normalizedName
                    }, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(6) });

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
                        cached = await ResolveCachedImagePathAsync(resolvedAsin);
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

                var similarAuthors = MapSimilarAuthors(
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

                await PersistAuthorLookupAsync(
                    persistedEntry,
                    normalizedName,
                    region,
                    result);

                CacheAuthorLookupResponse(cacheKey, result);
                CacheAuthorLookupResponse(BuildAuthorLookupCacheKey(region, normalizedName, result.Asin), result);

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
                    Books = catalog.Books.Select(MapAuthorCatalogBook).ToList(),
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
                else if (_cache.TryGetValue(cacheKey, out SeriesLookupCacheEntry? cachedEntry) && cachedEntry != null)
                {
                    cachedEntry.Asin ??= normalizedAsin;
                    return Ok(MapSeriesLookupResponse(cachedEntry, normalizedName));
                }

                var persistedEntry = await ResolvePersistedSeriesCacheAsync(normalizedName, region, normalizedAsin);
                if (!refresh && persistedEntry != null)
                {
                    var persistedResponse = await MapPersistedSeriesLookupResponseAsync(persistedEntry, normalizedName);
                    CacheSeriesLookupResponse(cacheKey, persistedResponse);
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
                    cachedPath = await ResolveCachedImagePathAsync(resolvedSeries.Asin);

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

                await PersistSeriesLookupAsync(
                    persistedEntry,
                    normalizedName,
                    region,
                    result,
                    catalog?.Books);

                CacheSeriesLookupResponse(cacheKey, result);

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
                    Books = catalog.Books.Select(MapSeriesCatalogBook).ToList(),
                    TotalBooks = catalog.TotalBooks
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error fetching series catalog for {Name}", name);
                return StatusCode(500, "Internal server error");
            }
        }

        private static string BuildAuthorCatalogBookKey(AudibleSearchResult book)
        {
            if (!string.IsNullOrWhiteSpace(book.Asin))
            {
                return $"asin:{NormalizeCatalogToken(book.Asin)}";
            }

            var title = NormalizeCatalogToken(book.Title);
            var authors = string.Join("|", (book.Authors ?? new List<AudibleAuthor>())
                .Select(a => NormalizeCatalogToken(a.Name))
                .Where(a => !string.IsNullOrWhiteSpace(a)));

            return $"title:{title}:authors:{authors}";
        }

        private static string NormalizeCatalogToken(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return new string(value.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        }

        private static AuthorCatalogBookItem MapAuthorCatalogBook(AudibleSearchResult book)
        {
            var primarySeries = book.Series?.FirstOrDefault();
            var runtime = book.LengthMinutes ?? book.RuntimeLengthMin ?? book.RuntimeMinutes;

            return new AuthorCatalogBookItem
            {
                Asin = book.Asin,
                Title = book.Title ?? "Unknown Title",
                Subtitle = book.Subtitle,
                Authors = (book.Authors ?? new List<AudibleAuthor>())
                    .Select(a => a.Name)
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Cast<string>()
                    .ToList(),
                ImageUrl = book.ImageUrl,
                Runtime = runtime,
                Language = book.Language,
                Publisher = book.Publisher,
                Narrators = (book.Narrators ?? new List<AudibleNarrator>())
                    .Select(n => n.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Cast<string>()
                    .ToList(),
                Genres = (book.Genres ?? new List<AudibleGenre>())
                    .Select(g => g.Name)
                    .Where(g => !string.IsNullOrWhiteSpace(g))
                    .Cast<string>()
                    .ToList(),
                Series = primarySeries?.Name,
                SeriesNumber = primarySeries?.Position,
                PublishedDate = book.ReleaseDate,
                Isbn = book.Isbn,
                Link = book.Link,
                MetadataSource = "Audible"
            };
        }

        private static SeriesCatalogBookItem MapSeriesCatalogBook(AudibleSearchResult book)
        {
            var primarySeries = book.Series?.FirstOrDefault();
            var runtime = book.LengthMinutes ?? book.RuntimeLengthMin ?? book.RuntimeMinutes;

            return new SeriesCatalogBookItem
            {
                Asin = book.Asin,
                Title = book.Title ?? "Unknown Title",
                Subtitle = book.Subtitle,
                Authors = (book.Authors ?? new List<AudibleAuthor>())
                    .Select(a => a.Name)
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Cast<string>()
                    .ToList(),
                ImageUrl = book.ImageUrl,
                Runtime = runtime,
                Language = book.Language,
                Publisher = book.Publisher,
                Narrators = (book.Narrators ?? new List<AudibleNarrator>())
                    .Select(n => n.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Cast<string>()
                    .ToList(),
                Genres = (book.Genres ?? new List<AudibleGenre>())
                    .Select(g => g.Name)
                    .Where(g => !string.IsNullOrWhiteSpace(g))
                    .Cast<string>()
                    .ToList(),
                Series = primarySeries?.Name,
                SeriesNumber = primarySeries?.Position,
                PublishedDate = book.ReleaseDate,
                Isbn = book.Isbn,
                Link = book.Link,
                MetadataSource = "Audible"
            };
        }

        private async Task<(string? Asin, string? CachedPath)> ProbeAuthorImageCacheAsync(string normalizedName, string region, string? hintedAsin)
        {
            var candidateAsins = new List<string>();

            if (!string.IsNullOrWhiteSpace(hintedAsin))
            {
                candidateAsins.Add(hintedAsin.Trim());
            }

            try
            {
                var cachedAuthor = await _audiobookRepository.GetCachedAuthorByNameAsync(normalizedName, region);
                if (!string.IsNullOrWhiteSpace(cachedAuthor?.AuthorAsin)
                    && !candidateAsins.Any(existing => string.Equals(existing, cachedAuthor.AuthorAsin, StringComparison.OrdinalIgnoreCase)))
                {
                    candidateAsins.Add(cachedAuthor.AuthorAsin);
                }

                var storedAuthorAsin = await _audiobookRepository.GetAuthorAsinByNameAsync(normalizedName);
                if (!string.IsNullOrWhiteSpace(storedAuthorAsin)
                    && !candidateAsins.Any(existing => string.Equals(existing, storedAuthorAsin, StringComparison.OrdinalIgnoreCase)))
                {
                    candidateAsins.Add(storedAuthorAsin);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to probe DB for cached author ASIN: {Author}", normalizedName);
            }

            foreach (var candidateAsin in candidateAsins)
            {
                var cachedPath = await ResolveCachedImagePathAsync(candidateAsin);
                if (!string.IsNullOrWhiteSpace(cachedPath))
                {
                    return (candidateAsin, cachedPath);
                }
            }

            return (candidateAsins.FirstOrDefault(), null);
        }

        private async Task<string?> ResolveCachedImagePathAsync(string? asin)
        {
            if (string.IsNullOrWhiteSpace(asin)) return null;

            try
            {
                var diskPath = await _imageCacheService.GetCachedImagePathAsync(asin);
                return string.IsNullOrWhiteSpace(diskPath)
                    ? null
                    : "/" + diskPath.TrimStart('/');
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to resolve cached author image path for ASIN {Asin}", asin);
                return null;
            }
        }

        private async Task<AuthorCacheEntry?> ResolvePersistedAuthorCacheAsync(string normalizedName, string region, string? normalizedAsin)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(normalizedAsin))
                {
                    var byAsin = await _audiobookRepository.GetCachedAuthorByAsinAsync(normalizedAsin, region);
                    if (byAsin != null)
                    {
                        return byAsin;
                    }
                }

                var byName = await _audiobookRepository.GetCachedAuthorByNameAsync(normalizedName, region);
                if (byName != null)
                {
                    return byName;
                }

                var storedAuthorAsin = await _audiobookRepository.GetAuthorAsinByNameAsync(normalizedName);
                if (!string.IsNullOrWhiteSpace(storedAuthorAsin))
                {
                    return await _audiobookRepository.GetCachedAuthorByAsinAsync(storedAuthorAsin, region);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to resolve persisted author cache for {Author}", normalizedName);
            }

            return null;
        }

        private async Task<AuthorLookupResponse> MapPersistedAuthorLookupResponseAsync(AuthorCacheEntry entry, string fallbackName)
        {
            var cachedPath = await ResolveCachedImagePathAsync(entry.AuthorAsin);
            if (string.IsNullOrWhiteSpace(cachedPath) &&
                !string.IsNullOrWhiteSpace(entry.AuthorAsin) &&
                !string.IsNullOrWhiteSpace(entry.ImageUrl))
            {
                try
                {
                    cachedPath = await _imageCacheService.MoveToAuthorLibraryStorageAsync(entry.AuthorAsin, entry.ImageUrl);
                    if (!string.IsNullOrWhiteSpace(cachedPath))
                    {
                        cachedPath = "/" + cachedPath.TrimStart('/');
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to backfill cached author image for ASIN {Asin}", entry.AuthorAsin);
                }
            }

            return new AuthorLookupResponse
            {
                Asin = entry.AuthorAsin,
                Name = string.IsNullOrWhiteSpace(entry.AuthorName) ? fallbackName : entry.AuthorName,
                Image = entry.ImageUrl,
                CachedPath = cachedPath,
                Description = entry.Description,
                SimilarAuthors = (entry.SimilarAuthors ?? new List<CachedRelatedAuthor>())
                    .Where(author => !string.IsNullOrWhiteSpace(author.Name))
                    .Select(author => new RelatedAuthorItem
                    {
                        Asin = author.Asin,
                        Name = author.Name
                    })
                    .ToList()
            };
        }

        private async Task PersistAuthorLookupAsync(
            AuthorCacheEntry? existingEntry,
            string normalizedName,
            string region,
            AuthorLookupResponse response)
        {
            if (string.IsNullOrWhiteSpace(response.Name))
            {
                return;
            }

            try
            {
                var entry = existingEntry ?? new AuthorCacheEntry();
                entry.AuthorName = response.Name;
                entry.AuthorNameNormalized = NormalizeAuthorCacheKey(normalizedName);
                entry.AuthorAsin = response.Asin;
                entry.Region = AudiobookIdentifierNormalizer.NormalizeRegion(region) ?? "us";
                entry.ImageUrl = response.Image;
                entry.Description = response.Description;
                entry.SimilarAuthors = response.SimilarAuthors
                    .Where(author => !string.IsNullOrWhiteSpace(author.Name))
                    .Select(author => new CachedRelatedAuthor
                    {
                        Asin = author.Asin,
                        Name = author.Name
                    })
                    .ToList();
                entry.LastFetchedAt = DateTime.UtcNow;

                await _audiobookRepository.UpsertCachedAuthorAsync(entry);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to persist author cache for {Author}", normalizedName);
            }
        }

        private void CacheAuthorLookupResponse(string cacheKey, AuthorLookupResponse response)
        {
            _cache.Set(cacheKey, new AuthorLookupCacheEntry
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

        private static string BuildAuthorLookupCacheKey(string region, string name, string? asin = null)
        {
            var normalizedRegion = AudiobookIdentifierNormalizer.NormalizeRegion(region) ?? "us";
            var normalizedName = NormalizeAuthorCacheKey(name);
            var normalizedAsin = string.IsNullOrWhiteSpace(asin) ? null : asin.Trim().ToUpperInvariant();

            return string.IsNullOrWhiteSpace(normalizedAsin)
                ? $"author-lookup:{normalizedRegion}:{normalizedName}"
                : $"author-lookup:{normalizedRegion}:{normalizedName}:{normalizedAsin}";
        }

        private async Task<SeriesCacheEntry?> ResolvePersistedSeriesCacheAsync(string normalizedName, string region, string? normalizedAsin)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(normalizedAsin))
                {
                    var byAsin = await _audiobookRepository.GetCachedSeriesByAsinAsync(normalizedAsin, region);
                    if (byAsin != null)
                    {
                        return byAsin;
                    }
                }

                return await _audiobookRepository.GetCachedSeriesByNameAsync(normalizedName, region);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to resolve persisted series cache for {Series}", normalizedName);
            }

            return null;
        }

        private async Task<SeriesLookupResponse> MapPersistedSeriesLookupResponseAsync(SeriesCacheEntry entry, string fallbackName)
        {
            var cachedPath = await ResolveCachedImagePathAsync(entry.SeriesAsin);
            if (string.IsNullOrWhiteSpace(cachedPath) &&
                !string.IsNullOrWhiteSpace(entry.SeriesAsin) &&
                !string.IsNullOrWhiteSpace(entry.ImageUrl))
            {
                try
                {
                    cachedPath = await _imageCacheService.MoveToSeriesLibraryStorageAsync(entry.SeriesAsin, entry.ImageUrl);
                    if (!string.IsNullOrWhiteSpace(cachedPath))
                    {
                        cachedPath = "/" + cachedPath.TrimStart('/');
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to backfill cached series image for ASIN {Asin}", entry.SeriesAsin);
                }
            }

            return new SeriesLookupResponse
            {
                Asin = entry.SeriesAsin,
                Name = string.IsNullOrWhiteSpace(entry.SeriesName) ? fallbackName : entry.SeriesName,
                Image = entry.ImageUrl,
                CachedPath = cachedPath,
                Description = entry.Description,
                TotalBooks = entry.CatalogBooks?.Count ?? 0
            };
        }

        private async Task PersistSeriesLookupAsync(
            SeriesCacheEntry? existingEntry,
            string normalizedName,
            string region,
            SeriesLookupResponse response,
            IEnumerable<AudibleSearchResult>? catalogBooks = null)
        {
            if (string.IsNullOrWhiteSpace(response.Name))
            {
                return;
            }

            try
            {
                var entry = existingEntry ?? new SeriesCacheEntry();
                entry.SeriesName = response.Name;
                entry.SeriesNameNormalized = NormalizeSeriesCacheKey(normalizedName);
                entry.SeriesAsin = response.Asin;
                entry.Region = AudiobookIdentifierNormalizer.NormalizeRegion(region) ?? "us";
                entry.ImageUrl = response.Image;
                entry.Description = response.Description;
                if (catalogBooks != null)
                {
                    entry.CatalogBooks = catalogBooks.Select(book => new CachedSeriesCatalogBook
                    {
                        Asin = book.Asin,
                        Title = book.Title ?? "Unknown Title",
                        Subtitle = book.Subtitle,
                        Authors = (book.Authors ?? new List<AudibleAuthor>())
                            .Select(author => author.Name)
                            .Where(author => !string.IsNullOrWhiteSpace(author))
                            .Cast<string>()
                            .ToList(),
                        ImageUrl = book.ImageUrl,
                        Runtime = book.LengthMinutes ?? book.RuntimeLengthMin ?? book.RuntimeMinutes,
                        Language = book.Language,
                        Publisher = book.Publisher,
                        Narrators = (book.Narrators ?? new List<AudibleNarrator>())
                            .Select(narrator => narrator.Name)
                            .Where(narrator => !string.IsNullOrWhiteSpace(narrator))
                            .Cast<string>()
                            .ToList(),
                        Genres = (book.Genres ?? new List<AudibleGenre>())
                            .Select(genre => genre.Name)
                            .Where(genre => !string.IsNullOrWhiteSpace(genre))
                            .Cast<string>()
                            .ToList(),
                        Series = book.Series?.FirstOrDefault()?.Name,
                        SeriesNumber = book.Series?.FirstOrDefault()?.Position,
                        PublishedDate = book.ReleaseDate,
                        Isbn = book.Isbn,
                        Link = book.Link,
                        MetadataSource = "Audible"
                    }).ToList();
                }
                entry.LastFetchedAt = DateTime.UtcNow;

                await _audiobookRepository.UpsertCachedSeriesAsync(entry);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to persist series cache for {Series}", normalizedName);
            }
        }

        private void CacheSeriesLookupResponse(string cacheKey, SeriesLookupResponse response)
        {
            _cache.Set(cacheKey, new SeriesLookupCacheEntry
            {
                Asin = response.Asin,
                Name = response.Name,
                Image = response.Image,
                CachedPath = response.CachedPath,
                Description = response.Description,
                TotalBooks = response.TotalBooks
            }, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(12) });
        }

        private static string NormalizeAuthorCacheKey(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var cleaned = new string(value
                .Where(character => char.IsLetterOrDigit(character) || char.IsWhiteSpace(character))
                .ToArray());
            var parts = cleaned.Split(
                new[] { ' ', '\t', '\n', '\r' },
                StringSplitOptions.RemoveEmptyEntries);

            return string.Join(' ', parts).ToLowerInvariant();
        }

        private static string NormalizeSeriesCacheKey(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var cleaned = new string(value
                .Where(character => char.IsLetterOrDigit(character) || char.IsWhiteSpace(character))
                .ToArray());
            var parts = cleaned.Split(
                new[] { ' ', '\t', '\n', '\r' },
                StringSplitOptions.RemoveEmptyEntries);

            return string.Join(' ', parts).ToLowerInvariant();
        }

        private static AuthorLookupResponse MapAuthorLookupResponse(AuthorLookupCacheEntry entry, string fallbackName)
        {
            return new AuthorLookupResponse
            {
                Asin = entry.Asin,
                Name = entry.Name ?? fallbackName,
                Image = entry.Image,
                CachedPath = entry.CachedPath,
                Description = entry.Description,
                SimilarAuthors = entry.SimilarAuthors ?? new List<RelatedAuthorItem>()
            };
        }

        private static SeriesLookupResponse MapSeriesLookupResponse(SeriesLookupCacheEntry entry, string fallbackName)
        {
            return new SeriesLookupResponse
            {
                Asin = entry.Asin,
                Name = entry.Name ?? fallbackName,
                Image = entry.Image,
                CachedPath = entry.CachedPath,
                Description = entry.Description,
                TotalBooks = entry.TotalBooks
            };
        }

        private static List<RelatedAuthorItem> MapSimilarAuthors(IEnumerable<AudnexusSimilarAuthor>? authors, string currentAuthorName)
        {
            if (authors == null)
            {
                return new List<RelatedAuthorItem>();
            }

            return authors
                .Where(author => !string.IsNullOrWhiteSpace(author.Name))
                .Where(author => !string.Equals(author.Name, currentAuthorName, StringComparison.OrdinalIgnoreCase))
                .GroupBy(author => author.Name!, StringComparer.OrdinalIgnoreCase)
                .Select(group => new RelatedAuthorItem
                {
                    Asin = group.First().Asin,
                    Name = group.First().Name ?? string.Empty
                })
                .ToList();
        }

        private static bool HasCompleteAuthorLookupData(
            string? cachedPath,
            string? description,
            IEnumerable<RelatedAuthorItem>? similarAuthors)
        {
            return !string.IsNullOrWhiteSpace(cachedPath) &&
                !string.IsNullOrWhiteSpace(description) &&
                (similarAuthors?.Any(author => !string.IsNullOrWhiteSpace(author.Name)) ?? false);
        }

        private sealed class AuthorLookupCacheEntry
        {
            public string? Asin { get; set; }
            public string? Name { get; set; }
            public string? Image { get; set; }
            public string? CachedPath { get; set; }
            public string? Description { get; set; }
            public List<RelatedAuthorItem>? SimilarAuthors { get; set; }
            public bool NotFound { get; set; }
        }

        private sealed class SeriesLookupCacheEntry
        {
            public string? Asin { get; set; }
            public string? Name { get; set; }
            public string? Image { get; set; }
            public string? CachedPath { get; set; }
            public string? Description { get; set; }
            public int TotalBooks { get; set; }
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

