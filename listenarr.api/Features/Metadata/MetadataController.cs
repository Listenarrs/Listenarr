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

namespace Listenarr.Api.Features.Metadata
{
    [ApiController]
    [Route("api/v{version:apiVersion}/metadata")]
    [Tags("Metadata")]
    public partial class MetadataController : ControllerBase
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
        private readonly MetadataAuthorLookupWorkflow _authorLookupWorkflow;
        private readonly MetadataSeriesLookupWorkflow _seriesLookupWorkflow;

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
            _authorLookupWorkflow = new MetadataAuthorLookupWorkflow(
                _audibleService,
                _audnexusService,
                _imageCacheService,
                _cache,
                _imageCacheWorkflow,
                _lookupCacheWorkflow,
                _lookupResponseCache,
                _logger);
            _seriesLookupWorkflow = new MetadataSeriesLookupWorkflow(
                _audibleService,
                _imageCacheService,
                _seriesCatalogService,
                _cache,
                _imageCacheWorkflow,
                _lookupCacheWorkflow,
                _lookupResponseCache,
                _logger);
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
            var result = await _authorLookupWorkflow.LookupAsync(name, region, asin, refresh);
            return result.Status switch
            {
                MetadataAuthorLookupStatus.Ok => Ok(result.Response!),
                MetadataAuthorLookupStatus.BadRequest => BadRequest(result.Message),
                MetadataAuthorLookupStatus.NotFound => NotFound(result.Message),
                _ => StatusCode(500, result.Message)
            };
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
            var result = await _seriesLookupWorkflow.LookupAsync(name, region, asin, refresh);
            return result.Status switch
            {
                MetadataSeriesLookupStatus.Ok => Ok(result.Response!),
                MetadataSeriesLookupStatus.BadRequest => BadRequest(result.Message),
                MetadataSeriesLookupStatus.NotFound => NotFound(result.Message),
                _ => StatusCode(500, result.Message)
            };
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

    }
}
