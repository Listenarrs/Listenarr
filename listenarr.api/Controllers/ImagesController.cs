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

namespace Listenarr.Api.Controllers
{
    [ApiController]
    [Route("api/v{version:apiVersion}/images")]
    [Tags("Images")]
    public class ImagesController : ControllerBase
    {
        private readonly IImageCacheService _imageCacheService;
        private readonly IAudiobookMetadataService _audiobookMetadataService;
        private readonly AudibleService _audibleService;
        private readonly IAudnexusService _audnexusService;
        private readonly IAudiobookRepository _audiobookRepository;
        private readonly IOpenLibraryService? _openLibraryService;
        private readonly ILogger<ImagesController> _logger;
        private readonly IWebHostEnvironment _environment;
        private readonly string _effectiveContentRootPath;

        [ActivatorUtilitiesConstructor]
        public ImagesController(
            IImageCacheService imageCacheService,
            IAudiobookMetadataService audiobookMetadataService,
            AudibleService audibleService,
            IAudnexusService audnexusService,
            IAudiobookRepository audiobookRepository,
            ILogger<ImagesController> logger,
            IWebHostEnvironment environment)
            : this(
                imageCacheService,
                audiobookMetadataService,
                audibleService,
                audnexusService,
                audiobookRepository,
                openLibraryService: null,
                logger,
                environment)
        {
        }

        public ImagesController(
            IImageCacheService imageCacheService,
            IAudiobookMetadataService audiobookMetadataService,
            AudibleService audibleService,
            IAudnexusService audnexusService,
            IAudiobookRepository audiobookRepository,
            IOpenLibraryService? openLibraryService,
            ILogger<ImagesController> logger,
            IWebHostEnvironment environment)
        {
            _imageCacheService = imageCacheService;
            _audiobookMetadataService = audiobookMetadataService;
            _audibleService = audibleService;
            _audnexusService = audnexusService;
            _audiobookRepository = audiobookRepository;
            _openLibraryService = openLibraryService;
            _logger = logger;
            _environment = environment;
            _effectiveContentRootPath = ResolveEffectiveContentRoot(environment.ContentRootPath);
        }

        /// <summary>
        /// Get a cached cover image by identifier (ASIN, numeric ID, or author name). Optionally download on demand via a URL query parameter.
        /// </summary>
        /// <param name="identifier">Image identifier (typically an ASIN or database ID).</param>
        /// <returns>The image file with appropriate content type and caching headers.</returns>
        [HttpGet("{identifier}")]
        public async Task<IActionResult> GetImage(string identifier)
        {
            if (string.IsNullOrEmpty(identifier))
            {
                return BadRequest("Identifier is required");
            }

            // Strip any query parameters from the identifier (e.g., "B0CQZ5167B?access_token=..." -> "B0CQZ5167B")
            var queryIndex = identifier.IndexOf('?');
            if (queryIndex >= 0)
            {
                identifier = identifier.Substring(0, queryIndex);
            }

            // Validate identifier to prevent path traversal or overly long values.
            // Identifiers should be simple ASINs, numeric IDs or author names—disallow path separators.
            if (identifier.IndexOfAny(new char[] { '\\', '/', '\0' }) >= 0 || identifier.Length > 256)
            {
                _logger.LogWarning("Rejected invalid identifier: {Identifier}", LogRedaction.SanitizeText(identifier));
                return BadRequest("Invalid identifier");
            }

            // Check for url parameter to download on demand
            var url = Request.Query["url"].ToString();
            if (!string.IsNullOrWhiteSpace(url) && (url.StartsWith("http://") || url.StartsWith("https://")))
            {
                // Try to download and cache the image
                try
                {
                    var downloaded = await _imageCacheService.DownloadAndCacheImageAsync(url, identifier);
                    if (!string.IsNullOrWhiteSpace(downloaded))
                    {
                        _logger.LogInformation("Downloaded image on demand for identifier: {Identifier}", LogRedaction.SanitizeText(identifier));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to download image on demand for identifier: {Identifier}", LogRedaction.SanitizeText(identifier));
                }
            }

            try
            {
                // Get the cached image path (checks library first, then temp)
                var relativePath = await _imageCacheService.GetCachedImagePathAsync(identifier);
                _logger.LogInformation("ImagesController DEBUG: returned relativePath='{RelativePath}' for identifier {Identifier}", LogRedaction.SanitizeFilePath(relativePath), LogRedaction.SanitizeText(identifier));
                bool movedAttempted = false;

                // Shortcut: if the returned relative path clearly points to a temp cache
                // layout, attempt to move it into library storage immediately. This
                // handles cases where path normalization/validation later could
                // interfere with detection (unit tests expect the move to be invoked).
                if (!string.IsNullOrWhiteSpace(relativePath))
                {
                    try
                    {
                        var preNormalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                        if (preNormalized.IndexOf(Path.Join("cache", "images", "temp"), StringComparison.OrdinalIgnoreCase) >= 0 ||
                            preNormalized.IndexOf(Path.Join("config", "cache", "images", "temp"), StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var book = await _audiobookRepository.GetByAsinAsync(identifier);
                            if (book != null)
                            {
                                movedAttempted = true;
                                var moved = await _imageCacheService.MoveToLibraryStorageAsync(identifier, null);
                                if (!string.IsNullOrWhiteSpace(moved))
                                {
                                    relativePath = moved;
                                }
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Pre-validation move attempt failed for identifier {Identifier}", LogRedaction.SanitizeText(identifier));
                    }
                }

                // Track whether we currently have a valid image path. Recompute after
                // validation/moves because `relativePath` may be nullified or replaced.

                // Sanitize/validate the returned relative path to ensure it points inside
                // known image directories. Treat any unexpected location as not-found.
                if (!string.IsNullOrWhiteSpace(relativePath))
                {
                    // Defend against services returning absolute paths unexpectedly
                    if (Path.IsPathRooted(relativePath))
                    {
                        _logger.LogWarning("Image service returned rooted path for identifier {Identifier}: {Path}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(relativePath));
                        relativePath = null;
                    }
                    else
                    {
                        _logger.LogDebug("ImagesController: initial relativePath for {Identifier}: {RelativePath}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(relativePath));
                        try
                        {
                            var candidateFull = Path.GetFullPath(ResolvePathWithOptionalBase(_effectiveContentRootPath, relativePath));
                            var imagesRoot = Path.GetFullPath(CombineRelativePath(_effectiveContentRootPath, "cache", "images"));
                            var imagesRootConfig = Path.GetFullPath(CombineRelativePath(_effectiveContentRootPath, "config", "cache", "images"));
                            var wwwroot = Path.GetFullPath(CombineRelativePath(_effectiveContentRootPath, "wwwroot"));

                            // Use Path.GetRelativePath to reliably determine whether candidateFull
                            // is inside one of the allowed roots. This works across separator styles.
                            bool insideImagesRoot = !Path.GetRelativePath(imagesRoot, candidateFull).StartsWith("..", StringComparison.Ordinal);
                            bool insideImagesRootConfig = !Path.GetRelativePath(imagesRootConfig, candidateFull).StartsWith("..", StringComparison.Ordinal);
                            bool insideWwwroot = !Path.GetRelativePath(wwwroot, candidateFull).StartsWith("..", StringComparison.Ordinal);

                            if (!insideImagesRoot && !insideImagesRootConfig && !insideWwwroot)
                            {
                                _logger.LogWarning("Resolved image path outside permitted directories for identifier {Identifier}: {Path}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(candidateFull));
                                relativePath = null;
                            }
                            else
                            {
                                try
                                {
                                    // Defend against symlink/reparse-point escapes
                                    if (System.IO.File.Exists(candidateFull))
                                    {
                                        var attrs = System.IO.File.GetAttributes(candidateFull);
                                        if ((attrs & System.IO.FileAttributes.ReparsePoint) != 0)
                                        {
                                            _logger.LogWarning("Rejected reparse-point (symlink) image path for identifier {Identifier}: {Path}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(candidateFull));
                                            relativePath = null;
                                        }
                                    }
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                {
                                    _logger.LogWarning(ex, "Failed to inspect candidate image attributes for identifier {Identifier}", LogRedaction.SanitizeText(identifier));
                                    relativePath = null;
                                }
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogWarning(ex, "Failed to validate image path for identifier {Identifier}", LogRedaction.SanitizeText(identifier));
                            relativePath = null;
                        }
                    }
                }

                // If we found a temp cached image but the identifier corresponds to an audiobook in the library,
                // attempt to move it into permanent library storage so library images don't live in /temp.
                if (!string.IsNullOrWhiteSpace(relativePath))
                {
                    var normalizedRelative = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                    _logger.LogDebug("ImagesController: normalizedRelative for {Identifier}: {Normalized}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(normalizedRelative));
                    if (!movedAttempted && normalizedRelative.Contains(Path.Join("cache", "images", "temp")))
                    {
                        try
                        {
                            var book = await _audiobookRepository.GetByAsinAsync(identifier);
                            if (book != null)
                            {
                                _logger.LogInformation("Found temp cached image for library audiobook {Identifier}, attempting move to library storage", LogRedaction.SanitizeText(identifier));
                                var moved = await _imageCacheService.MoveToLibraryStorageAsync(identifier, null);
                                if (!string.IsNullOrWhiteSpace(moved))
                                {
                                    // Prefer the moved library path when serving the image
                                    // Validate moved path as well
                                    try
                                    {
                                        var movedFull = Path.GetFullPath(ResolvePathWithOptionalBase(_effectiveContentRootPath, moved));
                                        var imagesRoot = Path.GetFullPath(CombineRelativePath(_effectiveContentRootPath, "cache", "images"));
                                        var imagesRootConfig = Path.GetFullPath(CombineRelativePath(_effectiveContentRootPath, "config", "cache", "images"));
                                        var wwwroot = Path.GetFullPath(CombineRelativePath(_effectiveContentRootPath, "wwwroot"));

                                        if (movedFull.StartsWith(imagesRoot, StringComparison.OrdinalIgnoreCase) || movedFull.StartsWith(imagesRootConfig, StringComparison.OrdinalIgnoreCase) || movedFull.StartsWith(wwwroot, StringComparison.OrdinalIgnoreCase))
                                        {
                                            try
                                            {
                                                if (System.IO.File.Exists(movedFull))
                                                {
                                                    var matt = System.IO.File.GetAttributes(movedFull);
                                                    if ((matt & System.IO.FileAttributes.ReparsePoint) != 0)
                                                    {
                                                        _logger.LogWarning("Rejected moved reparse-point (symlink) image path for identifier {Identifier}: {Path}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(movedFull));
                                                    }
                                                    else
                                                    {
                                                        relativePath = moved;
                                                    }
                                                }
                                                else
                                                {
                                                    // If file doesn't yet exist, conservatively reject the moved path
                                                    _logger.LogWarning("Moved image file does not exist for identifier {Identifier}: {Path}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(movedFull));
                                                }
                                            }
                                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                            {
                                                _logger.LogWarning(ex, "Failed to inspect moved image attributes for identifier {Identifier}", LogRedaction.SanitizeText(identifier));
                                            }
                                        }
                                        else
                                        {
                                            _logger.LogWarning("Moved image path outside permitted directories for identifier {Identifier}: {Path}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(movedFull));
                                        }
                                    }
                                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                    {
                                        _logger.LogWarning(ex, "Failed to validate moved image path for identifier {Identifier}", LogRedaction.SanitizeText(identifier));
                                    }
                                }
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogWarning(ex, "Failed to move temp image to library for {Identifier}", LogRedaction.SanitizeText(identifier));
                        }
                    }

                }

                var hasValidImagePath = !string.IsNullOrWhiteSpace(relativePath);
                var hasRequestedImageUrl = !string.IsNullOrWhiteSpace(url);
                if (!hasValidImagePath && !hasRequestedImageUrl)
                {
                    _logger.LogWarning("Image not found for identifier: {Identifier}", LogRedaction.SanitizeText(identifier));

                    // Cache is missing and caller did not provide a URL. Try metadata providers:
                    // ASIN => Audible, then Audnexus; ISBN => OpenLibrary cover URL.
                    try
                    {
                        var region = Request.Query["region"].ToString();
                        if (string.IsNullOrWhiteSpace(region)) region = "us";

                        string? candidateUrl = null;
                        string? candidateIsbn = null;
                        string? localOpenLibraryId = null;
                        string? localTitle = null;
                        string? localAuthor = null;
                        var localIsbnCandidates = new List<string>();
                        var localOpenLibraryIds = new List<string>();
                        var localAsinCandidates = new List<string>();
                        var candidateUrls = new List<string>();
                        var candidateUrlSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        void AddCandidateUrl(string? url, string source)
                        {
                            var normalized = NormalizeHttpImageUrl(url);
                            if (string.IsNullOrWhiteSpace(normalized)) return;
                            if (candidateUrlSet.Add(normalized))
                            {
                                candidateUrls.Add(normalized);
                                _logger.LogDebug("Queued image candidate for {Identifier} from {Source}: {Url}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(source), LogRedaction.SanitizeText(normalized));
                            }
                            if (string.IsNullOrWhiteSpace(candidateUrl))
                            {
                                candidateUrl = normalized;
                            }
                        }

                        // Seed OpenLibrary fallback inputs from the local library record when
                        // this identifier is an ASIN. This helps when provider metadata is
                        // missing/stale but the book already has ISBN/OLID persisted.
                        try
                        {
                            if (LooksLikeAsin(identifier))
                            {
                                var localBook = await _audiobookRepository.GetByAsinAsync(identifier);
                                if (localBook != null)
                                {
                                    localTitle = localBook.Title;
                                    localAuthor = localBook.Authors?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));

                                    // Collect identifiers from the new typed identifier model first.
                                    foreach (var extId in (localBook.ExternalIdentifiers ?? Enumerable.Empty<AudiobookExternalIdentifier>())
                                        .Where(extId => !string.IsNullOrWhiteSpace(extId.ValueNormalized)))
                                    {
                                        switch (extId.Type)
                                        {
                                            case AudiobookExternalIdentifierType.Asin:
                                                if (LooksLikeAsin(extId.ValueNormalized) &&
                                                    !localAsinCandidates.Contains(extId.ValueNormalized, StringComparer.OrdinalIgnoreCase))
                                                {
                                                    localAsinCandidates.Add(extId.ValueNormalized);
                                                }
                                                break;
                                            case AudiobookExternalIdentifierType.Isbn:
                                                if (LooksLikeIsbn(extId.ValueNormalized) &&
                                                    !localIsbnCandidates.Contains(extId.ValueNormalized, StringComparer.OrdinalIgnoreCase))
                                                {
                                                    localIsbnCandidates.Add(extId.ValueNormalized);
                                                }
                                                break;
                                            case AudiobookExternalIdentifierType.OpenLibraryId:
                                                {
                                                    var normalizedOlid = NormalizeOpenLibraryId(extId.ValueNormalized);
                                                    if (!string.IsNullOrWhiteSpace(normalizedOlid) &&
                                                        !localOpenLibraryIds.Contains(normalizedOlid, StringComparer.OrdinalIgnoreCase))
                                                    {
                                                        localOpenLibraryIds.Add(normalizedOlid);
                                                    }
                                                }
                                                break;
                                        }
                                    }

                                    var localIsbn = localBook.Isbn?
                                        .Select(NormalizeIsbn)
                                        .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v) && LooksLikeIsbn(v));
                                    if (!string.IsNullOrWhiteSpace(localIsbn))
                                    {
                                        if (!localIsbnCandidates.Contains(localIsbn, StringComparer.OrdinalIgnoreCase))
                                        {
                                            localIsbnCandidates.Add(localIsbn);
                                        }
                                        candidateIsbn ??= localIsbn;
                                        _logger.LogDebug("Seeded candidate ISBN {Isbn} from local library record for {Identifier}", LogRedaction.SanitizeText(candidateIsbn), LogRedaction.SanitizeText(identifier));
                                    }

                                    if (!string.IsNullOrWhiteSpace(localBook.OpenLibraryId))
                                    {
                                        var normalizedLocalOlid = NormalizeOpenLibraryId(localBook.OpenLibraryId);
                                        if (!string.IsNullOrWhiteSpace(normalizedLocalOlid))
                                        {
                                            if (!localOpenLibraryIds.Contains(normalizedLocalOlid, StringComparer.OrdinalIgnoreCase))
                                            {
                                                localOpenLibraryIds.Add(normalizedLocalOlid);
                                            }
                                            localOpenLibraryId ??= normalizedLocalOlid;
                                        }
                                    }

                                    if (LooksLikeAsin(localBook.Asin ?? string.Empty))
                                    {
                                        var normalizedLocalAsin = (localBook.Asin ?? string.Empty).Trim().ToUpperInvariant();
                                        if (!localAsinCandidates.Contains(normalizedLocalAsin, StringComparer.OrdinalIgnoreCase))
                                        {
                                            localAsinCandidates.Add(normalizedLocalAsin);
                                        }
                                    }
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex) when (IsRecoverableImageLookupException(ex))
                        {
                            _logger.LogDebug(ex, "Failed to seed image fallback metadata from local library record for {Identifier}", LogRedaction.SanitizeText(identifier));
                        }

                        // If the requested identifier key has no cached image, reuse an existing
                        // cached image from any alternate stored identifier (e.g., old primary ASIN).
                        if (string.IsNullOrWhiteSpace(relativePath))
                        {
                            var cacheAliasCandidates = localAsinCandidates
                                .Concat(localIsbnCandidates)
                                .Concat(localOpenLibraryIds)
                                .Where(v => !string.IsNullOrWhiteSpace(v) && !string.Equals(v, identifier, StringComparison.OrdinalIgnoreCase))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();

                            foreach (var aliasIdentifier in cacheAliasCandidates)
                            {
                                try
                                {
                                    var aliasPath = await _imageCacheService.GetCachedImagePathAsync(aliasIdentifier);
                                    if (!string.IsNullOrWhiteSpace(aliasPath))
                                    {
                                        relativePath = aliasPath;
                                        _logger.LogInformation(
                                            "Reused cached image for identifier {Identifier} via alternate identifier {AliasIdentifier}: {Path}",
                                            LogRedaction.SanitizeText(identifier),
                                            LogRedaction.SanitizeText(aliasIdentifier),
                                            LogRedaction.SanitizeText(relativePath));
                                        break;
                                    }
                                }
                                catch (OperationCanceledException)
                                {
                                    throw;
                                }
                                catch (Exception ex) when (IsRecoverableImageLookupException(ex))
                                {
                                    _logger.LogDebug(ex, "Failed probing alternate cached image identifier {AliasIdentifier} for {Identifier}", LogRedaction.SanitizeText(aliasIdentifier), LogRedaction.SanitizeText(identifier));
                                }
                            }
                        }

                        if (string.IsNullOrWhiteSpace(relativePath))
                        {
                            var audible = await _audiobookMetadataService.GetAudibleMetadataAsync(identifier, region, cache: true);

                            if (audible != null)
                            {
                                AddCandidateUrl(audible.ImageUrl, "Audible");
                                if (!string.IsNullOrWhiteSpace(audible.Isbn))
                                {
                                    candidateIsbn = NormalizeIsbn(audible.Isbn);
                                }
                            }

                            // Try Audnexus for ASINs as an additional candidate source even when
                            // Audible returned an image (Audible images can be placeholders or stale).
                            if (LooksLikeAsin(identifier))
                            {
                                try
                                {
                                    var audnexus = await _audnexusService.GetBookMetadataAsync(identifier, region, seedAuthors: true, update: false);
                                    if (audnexus != null)
                                    {
                                        AddCandidateUrl(audnexus.Image, "AudnexusBook");
                                        if (string.IsNullOrWhiteSpace(candidateIsbn) && !string.IsNullOrWhiteSpace(audnexus.Isbn))
                                        {
                                            candidateIsbn = NormalizeIsbn(audnexus.Isbn);
                                        }
                                    }
                                }
                                catch (OperationCanceledException)
                                {
                                    throw;
                                }
                                catch (Exception ex) when (IsRecoverableImageLookupException(ex))
                                {
                                    _logger.LogDebug(ex, "Audnexus ASIN lookup failed for {Identifier}", LogRedaction.SanitizeText(identifier));
                                }
                            }

                            // Try alternate stored ASIN identifiers for this audiobook when the requested
                            // ASIN is region-limited or missing from providers.
                            if (LooksLikeAsin(identifier) && localAsinCandidates.Count > 0)
                            {
                                foreach (var altAsin in localAsinCandidates
                                    .Where(a => !string.Equals(a, identifier, StringComparison.OrdinalIgnoreCase))
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .Take(3))
                                {
                                    try
                                    {
                                        var altAudible = await _audiobookMetadataService.GetAudibleMetadataAsync(altAsin, region, cache: true);
                                        if (altAudible != null)
                                        {
                                            AddCandidateUrl(altAudible.ImageUrl, "AudibleAltAsin");
                                            if (string.IsNullOrWhiteSpace(candidateIsbn) && !string.IsNullOrWhiteSpace(altAudible.Isbn))
                                            {
                                                candidateIsbn = NormalizeIsbn(altAudible.Isbn);
                                            }
                                        }
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        throw;
                                    }
                                    catch (Exception ex) when (IsRecoverableImageLookupException(ex))
                                    {
                                        _logger.LogDebug(ex, "Audible alternate ASIN lookup failed for {Identifier} via {AltAsin}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(altAsin));
                                    }

                                    try
                                    {
                                        var altAudnexus = await _audnexusService.GetBookMetadataAsync(altAsin, region, seedAuthors: true, update: false);
                                        if (altAudnexus != null)
                                        {
                                            AddCandidateUrl(altAudnexus.Image, "AudnexusBookAltAsin");
                                            if (string.IsNullOrWhiteSpace(candidateIsbn) && !string.IsNullOrWhiteSpace(altAudnexus.Isbn))
                                            {
                                                candidateIsbn = NormalizeIsbn(altAudnexus.Isbn);
                                            }
                                        }
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        throw;
                                    }
                                    catch (Exception ex) when (IsRecoverableImageLookupException(ex))
                                    {
                                        _logger.LogDebug(ex, "Audnexus alternate ASIN lookup failed for {Identifier} via {AltAsin}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(altAsin));
                                    }
                                }
                            }

                            // Build an OpenLibrary ISBN candidate when we have an ISBN (identifier or metadata/local record).
                            if (string.IsNullOrWhiteSpace(candidateIsbn) && LooksLikeIsbn(identifier))
                            {
                                candidateIsbn = NormalizeIsbn(identifier);
                            }
                            if (!string.IsNullOrWhiteSpace(candidateIsbn))
                            {
                                var olIsbnCandidate = $"https://covers.openlibrary.org/b/isbn/{Uri.EscapeDataString(candidateIsbn)}-L.jpg";
                                AddCandidateUrl(olIsbnCandidate, "OpenLibraryIsbn");
                                if (candidateUrls.Count == 1)
                                {
                                    _logger.LogInformation("Using OpenLibrary ISBN cover candidate for {Identifier}: ISBN={Isbn}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(candidateIsbn));
                                }
                            }

                            foreach (var localIsbnCandidate in localIsbnCandidates)
                            {
                                AddCandidateUrl(
                                    $"https://covers.openlibrary.org/b/isbn/{Uri.EscapeDataString(localIsbnCandidate)}-L.jpg",
                                    "OpenLibraryIsbnLocalIdentifier");
                            }

                            // Legacy fallback path through configured source envelope for compatibility.
                            if (string.IsNullOrWhiteSpace(candidateUrl) || string.IsNullOrWhiteSpace(candidateIsbn))
                            {
                                _logger.LogDebug("No image found in audible, attempting fallback GetMetadataAsync for {Identifier}", LogRedaction.SanitizeText(identifier));
                                try
                                {
                                    var metadataEnvelope = await _audiobookMetadataService.GetMetadataAsync(identifier, region, cache: true);
                                    if (metadataEnvelope != null)
                                    {
                                        try
                                        {
                                            // If the service returned an AudibleBookResponse directly
                                            if (metadataEnvelope is AudibleBookResponse directMeta)
                                            {
                                                AddCandidateUrl(directMeta.ImageUrl, "MetadataEnvelopeDirect");
                                            }
                                            else
                                            {
                                                // Try dynamic access
                                                dynamic env = metadataEnvelope;
                                                object? mdObj = env.metadata;

                                                // If it's already the Audible type, use it
                                                if (mdObj is AudibleBookResponse mdMeta)
                                                {
                                                    AddCandidateUrl(mdMeta.ImageUrl, "MetadataEnvelopeAudible");
                                                }
                                                else if (mdObj != null)
                                                {
                                                    // Try reflection for common property names
                                                    var t = mdObj.GetType();
                                                    var prop = t.GetProperty("ImageUrl") ?? t.GetProperty("Image") ?? t.GetProperty("image") ?? t.GetProperty("imageUrl");
                                                    if (prop != null)
                                                    {
                                                        var v = prop.GetValue(mdObj)?.ToString();
                                                        AddCandidateUrl(v, "MetadataEnvelopeReflection");
                                                    }

                                                    if (string.IsNullOrWhiteSpace(candidateIsbn))
                                                    {
                                                        var isbnProp = t.GetProperty("Isbn") ?? t.GetProperty("ISBN") ?? t.GetProperty("isbn");
                                                        var isbnVal = isbnProp?.GetValue(mdObj)?.ToString();
                                                        if (!string.IsNullOrWhiteSpace(isbnVal))
                                                        {
                                                            candidateIsbn = NormalizeIsbn(isbnVal);
                                                        }
                                                    }
                                                }
                                            }

                                            if (!string.IsNullOrWhiteSpace(candidateUrl))
                                            {
                                                _logger.LogInformation("Found image URL in fallback metadata source for identifier {Identifier}: {Url}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(candidateUrl));
                                            }
                                            else
                                            {
                                                _logger.LogDebug("Fallback metadata returned no image URL for {Identifier}", LogRedaction.SanitizeText(identifier));
                                            }
                                        }
                                        catch (Exception ex) when (IsRecoverableImageLookupException(ex))
                                        {
                                            _logger.LogDebug(ex, "Failed to parse fallback metadata envelope for {Identifier}", LogRedaction.SanitizeText(identifier));
                                        }
                                    }
                                    else
                                    {
                                        _logger.LogDebug("GetMetadataAsync returned null for {Identifier}", LogRedaction.SanitizeText(identifier));
                                    }
                                }
                                catch (OperationCanceledException)
                                {
                                    throw;
                                }
                                catch (Exception ex) when (IsRecoverableImageLookupException(ex))
                                {
                                    _logger.LogDebug(ex, "Fallback metadata lookup failed for {Identifier}", LogRedaction.SanitizeText(identifier));
                                }
                            }

                            // If metadata envelope yielded ISBN, queue OpenLibrary cover as a fallback candidate.
                            if (!string.IsNullOrWhiteSpace(candidateIsbn))
                            {
                                AddCandidateUrl($"https://covers.openlibrary.org/b/isbn/{Uri.EscapeDataString(candidateIsbn)}-L.jpg", "OpenLibraryIsbnPostMetadata");
                            }

                            // Final OpenLibrary fallback via persisted OLID (if available and ISBN path
                            // wasn't usable).
                            if (!string.IsNullOrWhiteSpace(localOpenLibraryId))
                            {
                                AddCandidateUrl($"https://covers.openlibrary.org/b/olid/{Uri.EscapeDataString(localOpenLibraryId)}-L.jpg", "OpenLibraryOlid");
                            }
                            foreach (var localOlid in localOpenLibraryIds)
                            {
                                AddCandidateUrl($"https://covers.openlibrary.org/b/olid/{Uri.EscapeDataString(localOlid)}-L.jpg", "OpenLibraryOlidLocalIdentifier");
                            }

                            // Final ISBN discovery fallback for ASIN requests: use local title/author to
                            // search OpenLibrary when providers/local metadata do not include ISBN/OLID.
                            if (string.IsNullOrWhiteSpace(candidateIsbn) &&
                                _openLibraryService != null &&
                                LooksLikeAsin(identifier) &&
                                !string.IsNullOrWhiteSpace(localTitle))
                            {
                                try
                                {
                                    var titleIsbns = await _openLibraryService.GetIsbnsForTitleAsync(localTitle!, localAuthor);
                                    var normalizedTitleIsbns = titleIsbns
                                        .Select(NormalizeIsbn)
                                        .Where(v => !string.IsNullOrWhiteSpace(v) && LooksLikeIsbn(v))
                                        .Distinct(StringComparer.OrdinalIgnoreCase)
                                        .Take(5)
                                        .ToList();

                                    if (normalizedTitleIsbns.Count > 0)
                                    {
                                        _logger.LogInformation(
                                            "Derived {Count} OpenLibrary ISBN candidate(s) from local title/author for {Identifier}: Title={Title}, Author={Author}",
                                            normalizedTitleIsbns.Count,
                                            LogRedaction.SanitizeText(identifier),
                                            LogRedaction.SanitizeText(localTitle),
                                            LogRedaction.SanitizeText(localAuthor));

                                        foreach (var titleIsbn in normalizedTitleIsbns)
                                        {
                                            AddCandidateUrl(
                                                $"https://covers.openlibrary.org/b/isbn/{Uri.EscapeDataString(titleIsbn)}-L.jpg",
                                                "OpenLibraryTitleAuthorSearch");
                                        }
                                    }
                                }
                                catch (OperationCanceledException)
                                {
                                    throw;
                                }
                                catch (Exception ex) when (IsRecoverableImageLookupException(ex))
                                {
                                    _logger.LogDebug(ex, "OpenLibrary title/author ISBN fallback failed for {Identifier}", LogRedaction.SanitizeText(identifier));
                                }
                            }

                            // If no image found from book metadata, attempt author lookups (treating identifier as author name/asin)
                            if (string.IsNullOrWhiteSpace(candidateUrl))
                            {
                                try
                                {
                                    // First: try to find a stored author ASIN in the DB and serve its cached image if available
                                    try
                                    {
                                        if (!string.IsNullOrWhiteSpace(identifier))
                                        {
                                            var authorAsin = await _audiobookRepository.GetAuthorAsinByNameAsync(identifier);
                                            if (!string.IsNullOrWhiteSpace(authorAsin))
                                            {
                                                var diskPath = await _imageCacheService.GetCachedImagePathAsync(authorAsin);
                                                if (!string.IsNullOrWhiteSpace(diskPath))
                                                {
                                                    // Use cached author image by ASIN (prefer authors storage path)
                                                    relativePath = "/" + diskPath.TrimStart('/');
                                                    _logger.LogInformation("Found cached author image for identifier {Identifier} via stored ASIN {Asin}: {Path}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(authorAsin), LogRedaction.SanitizeText(relativePath));
                                                }
                                            }
                                        }
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        throw;
                                    }
                                    catch (Exception ex) when (IsRecoverableImageLookupException(ex))
                                    {
                                        _logger.LogDebug(ex, "Failed to lookup stored author ASIN for identifier {Identifier}", LogRedaction.SanitizeText(identifier));
                                    }

                                    // If we didn't find a cached author image via stored ASIN, fallback to Audible lookup by name
                                    if (string.IsNullOrWhiteSpace(relativePath))
                                    {
                                        var authorLookup = await _audibleService.LookupAuthorAsync(identifier, region);
                                        if (authorLookup != null && !string.IsNullOrWhiteSpace(authorLookup.Image) && (authorLookup.Image.StartsWith("http://") || authorLookup.Image.StartsWith("https://")))
                                        {
                                            AddCandidateUrl(authorLookup.Image, "AudibleAuthor");
                                            _logger.LogInformation("Found author image from Audible for identifier {Identifier}: {Url}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(candidateUrl));
                                        }
                                    }
                                }
                                catch (OperationCanceledException)
                                {
                                    throw;
                                }
                                catch (Exception ex) when (IsRecoverableImageLookupException(ex))
                                {
                                    _logger.LogDebug(ex, "Audible author lookup failed for {Identifier}", LogRedaction.SanitizeText(identifier));
                                }

                                // 2) Audnexus author search fallback
                                if (string.IsNullOrWhiteSpace(candidateUrl))
                                {
                                    try
                                    {
                                        // If identifier looks like an ASIN, prefer GetAuthorAsync to fetch the author directly
                                        if (identifier != null && identifier.Length >= 10 && (identifier.StartsWith("B", StringComparison.OrdinalIgnoreCase) || identifier.All(char.IsLetterOrDigit)))
                                        {
                                            try
                                            {
                                                var authorResp = await _audnexusService.GetAuthorAsync(identifier, region, update: false);
                                                if (authorResp != null && !string.IsNullOrWhiteSpace(authorResp.Image) && (authorResp.Image.StartsWith("http://") || authorResp.Image.StartsWith("https://")))
                                                {
                                                    AddCandidateUrl(authorResp.Image, "AudnexusAuthorByAsin");
                                                    _logger.LogInformation("Found author image from Audnexus (by ASIN) for identifier {Identifier}: {Url}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(candidateUrl));
                                                }
                                            }
                                            catch (OperationCanceledException)
                                            {
                                                throw;
                                            }
                                            catch (Exception ex) when (IsRecoverableImageLookupException(ex))
                                            {
                                                _logger.LogDebug(ex, "Audnexus GetAuthorAsync failed for ASIN {Identifier}", LogRedaction.SanitizeText(identifier));
                                            }
                                        }

                                        // If still not found, fallback to searching by name
                                        if (string.IsNullOrWhiteSpace(candidateUrl))
                                        {
                                            // Try to find stored author ASIN in database (match by author name) and prefer direct GET
                                            try
                                            {
                                                if (!string.IsNullOrWhiteSpace(identifier))
                                                {
                                                    var authorAsin = await _audiobookRepository.GetAuthorAsinByNameAsync(identifier);
                                                    if (!string.IsNullOrWhiteSpace(authorAsin))
                                                    {
                                                        try
                                                        {
                                                            var authorResp = await _audnexusService.GetAuthorAsync(authorAsin, region, update: false);
                                                            if (authorResp != null && !string.IsNullOrWhiteSpace(authorResp.Image) && (authorResp.Image.StartsWith("http://") || authorResp.Image.StartsWith("https://")))
                                                            {
                                                                AddCandidateUrl(authorResp.Image, "AudnexusAuthorByStoredAsin");
                                                                _logger.LogInformation("Found author image from Audnexus by stored ASIN {Asin} for identifier {Identifier}: {Url}", LogRedaction.SanitizeText(authorAsin), LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(candidateUrl));
                                                            }
                                                        }
                                                        catch (OperationCanceledException)
                                                        {
                                                            throw;
                                                        }
                                                        catch (Exception ex) when (IsRecoverableImageLookupException(ex))
                                                        {
                                                            _logger.LogDebug(ex, "Audnexus GetAuthorAsync failed for ASIN {Asin}", LogRedaction.SanitizeText(authorAsin));
                                                        }
                                                    }
                                                }
                                            }
                                            catch (OperationCanceledException)
                                            {
                                                throw;
                                            }
                                            catch (Exception ex) when (IsRecoverableImageLookupException(ex))
                                            {
                                                _logger.LogDebug(ex, "Failed to lookup author ASINs in database for identifier {Identifier}", LogRedaction.SanitizeText(identifier));
                                            }

                                            // If still not found, fallback to searching by name
                                            if (string.IsNullOrWhiteSpace(candidateUrl))
                                            {
                                                var authors = await _audnexusService.SearchAuthorsAsync(identifier!, region);
                                                var first = authors?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Image));
                                                if (first != null && !string.IsNullOrWhiteSpace(first.Image) && (first.Image.StartsWith("http://") || first.Image.StartsWith("https://")))
                                                {
                                                    AddCandidateUrl(first.Image, "AudnexusAuthorSearch");
                                                    _logger.LogInformation("Found author image from Audnexus (search) for identifier {Identifier}: {Url}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(candidateUrl));
                                                }
                                            }
                                        }
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        throw;
                                    }
                                    catch (Exception ex) when (IsRecoverableImageLookupException(ex))
                                    {
                                        _logger.LogDebug(ex, "Audnexus author search failed for {Identifier}", LogRedaction.SanitizeText(identifier));
                                    }
                                }
                            }

                            if (candidateUrls.Count > 0)
                            {
                                foreach (var urlCandidate in candidateUrls)
                                {
                                    _logger.LogInformation("Attempting metadata-driven image download for identifier {Identifier} from {Url}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(urlCandidate));
                                    try
                                    {
                                        _logger.LogDebug("Calling DownloadAndCacheImageAsync for {Identifier} from {Url}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(urlCandidate));
                                        var downloaded = await _imageCacheService.DownloadAndCacheImageAsync(urlCandidate, identifier!);
                                        if (!string.IsNullOrWhiteSpace(downloaded))
                                        {
                                            _logger.LogInformation("Downloaded metadata image for identifier: {Identifier}", LogRedaction.SanitizeText(identifier));
                                            // Re-check cache
                                            relativePath = await _imageCacheService.GetCachedImagePathAsync(identifier!);
                                            if (!string.IsNullOrWhiteSpace(relativePath))
                                            {
                                                break;
                                            }
                                        }
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        throw;
                                    }
                                    catch (Exception ex) when (IsRecoverableImageLookupException(ex))
                                    {
                                        _logger.LogWarning(ex, "Failed to download metadata-driven image for {Identifier} from {Url}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(urlCandidate));
                                    }
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (IsRecoverableImageLookupException(ex))
                    {
                        _logger.LogDebug(ex, "Metadata-driven image download failed for {Identifier}", LogRedaction.SanitizeText(identifier));
                    }

                    if (relativePath == null)
                    {
                        return CreatePlaceholderResult(
                            logContext: "missing identifier",
                            logValue: identifier,
                            notFoundMessage: "Image not found");
                    }
                }


                // Defensive: If relativePath is null, return NotFound
                if (relativePath == null)
                {
                    _logger.LogWarning("Image service returned null relativePath for identifier {Identifier}", LogRedaction.SanitizeText(identifier));
                    Response.Headers["Cache-Control"] = "public, max-age=300";
                    return NotFound(new { message = "Image not found" });
                }

                // Build the full file path
                var fullPath = ResolvePathWithOptionalBase(_effectiveContentRootPath, relativePath);

                if (!System.IO.File.Exists(fullPath))
                {
                    _logger.LogWarning("Image file does not exist at path: {Path}", LogRedaction.SanitizeFilePath(fullPath));
                    return CreatePlaceholderResult(
                        logContext: "missing file",
                        logValue: fullPath,
                        notFoundMessage: "Image file not found");
                }

                // Determine content type based on file extension
                var extension = Path.GetExtension(fullPath).ToLowerInvariant();
                var contentType = extension switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".png" => "image/png",
                    ".gif" => "image/gif",
                    ".webp" => "image/webp",
                    ".svg" => "image/svg+xml",
                    _ => "application/octet-stream"
                };

                _logger.LogInformation("Serving cached image for identifier: {Identifier}, path: {Path}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(relativePath));

                // Return the image with caching headers
                return PhysicalFile(fullPath, contentType, enableRangeProcessing: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error retrieving image for identifier: {Identifier}", LogRedaction.SanitizeText(identifier));
                return StatusCode(500, new { message = "Error retrieving image" });
            }
        }

        private static bool LooksLikeAsin(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = value.Trim();
            if (v.Length != 10) return false;
            return v.All(char.IsLetterOrDigit);
        }

        private static bool LooksLikeIsbn(string value)
        {
            var v = NormalizeIsbn(value);
            if (string.IsNullOrWhiteSpace(v)) return false;
            if (v.Length == 10)
            {
                // ISBN-10 is 9 digits plus a digit or X checksum.
                for (var i = 0; i < 9; i++)
                {
                    if (!char.IsDigit(v[i])) return false;
                }
                return char.IsDigit(v[9]) || v[9] == 'X';
            }

            if (v.Length == 13)
            {
                // ISBN-13 is digits only (typically 978/979 prefix).
                return v.All(char.IsDigit);
            }

            return false;
        }

        private static string NormalizeIsbn(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return new string(value.Where(ch => char.IsLetterOrDigit(ch)).ToArray()).ToUpperInvariant();
        }

        private static string? NormalizeOpenLibraryId(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var v = value.Trim();
            if (Uri.TryCreate(v, UriKind.Absolute, out var abs))
            {
                v = abs.AbsolutePath;
            }

            v = v.Trim('/');
            var segments = v.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var candidate = segments.Length > 0 ? segments[^1] : v;
            if (string.IsNullOrWhiteSpace(candidate)) return null;
            // Covers API expects the bare OLID (e.g. OL12345M)
            return candidate.Trim();
        }

        private static string? NormalizeHttpImageUrl(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var trimmed = value.Trim();
            if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }
            return null;
        }

        private static bool IsRecoverableImageLookupException(Exception ex)
        {
            return ex is System.IO.IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or ArgumentException
                or FormatException
                or UriFormatException
                or System.Net.Http.HttpRequestException
                or System.Text.Json.JsonException;
        }

        private static string ResolvePathWithOptionalBase(string? basePath, string candidatePath)
        {
            var normalizedPath = candidatePath.Trim();

            if (string.IsNullOrEmpty(normalizedPath))
            {
                return normalizedPath;
            }

            if (Path.IsPathRooted(normalizedPath) || string.IsNullOrWhiteSpace(basePath))
            {
                return normalizedPath;
            }

            var relativePath = normalizedPath.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (Path.IsPathRooted(relativePath))
            {
                return relativePath;
            }

            var normalizedBasePath = basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.IsNullOrEmpty(normalizedBasePath)
                ? relativePath
                : normalizedBasePath + Path.DirectorySeparatorChar + relativePath;
        }

        private static string CombineRelativePath(string basePath, params string[] segments)
        {
            if (string.IsNullOrWhiteSpace(basePath))
            {
                throw new ArgumentException("Base path is required.", nameof(basePath));
            }

            var combined = basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var segment in segments)
            {
                if (string.IsNullOrWhiteSpace(segment))
                {
                    continue;
                }

                var relativeSegment = segment.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (Path.IsPathRooted(relativeSegment))
                {
                    throw new ArgumentException("Path segments must be relative.", nameof(segments));
                }

                combined = string.IsNullOrEmpty(combined)
                    ? relativeSegment
                    : combined + Path.DirectorySeparatorChar + relativeSegment;
            }

            return combined;
        }

        private IActionResult CreatePlaceholderResult(string logContext, string? logValue, string notFoundMessage)
        {
            try
            {
                var placeholderPath = ResolvePlaceholderPath();
                if (!string.IsNullOrWhiteSpace(placeholderPath))
                {
                    _logger.LogInformation("Serving placeholder image for {LogContext}: {LogValue}", LogRedaction.SanitizeText(logContext), LogRedaction.SanitizeText(logValue));
                    Response.Headers["Cache-Control"] = "public, max-age=300";
                    return PhysicalFile(placeholderPath, "image/svg+xml");
                }
            }
            catch (Exception ex) when (IsRecoverableImageLookupException(ex))
            {
                _logger.LogDebug(ex, "Failed to resolve placeholder for {LogContext}: {LogValue}", LogRedaction.SanitizeText(logContext), LogRedaction.SanitizeText(logValue));
            }

            // Fall back to the shared placeholder route before returning JSON 404. This
            // keeps image consumers rendering an actual placeholder even when the local
            // file cannot be resolved from the current content root.
            if (!string.Equals(HttpContext?.Request?.Path.Value, "/placeholder.svg", StringComparison.OrdinalIgnoreCase))
            {
                Response.Headers["Cache-Control"] = "public, max-age=300";
                return Redirect("/placeholder.svg");
            }

            Response.Headers["Cache-Control"] = "public, max-age=300";
            return NotFound(new { message = notFoundMessage });
        }

        private string? ResolvePlaceholderPath()
        {
            foreach (var candidate in EnumeratePlaceholderCandidates())
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                try
                {
                    var fullPath = Path.GetFullPath(candidate);
                    if (System.IO.File.Exists(fullPath))
                    {
                        return fullPath;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogDebug(ex, "Failed probing placeholder candidate path {Path}", candidate);
                }
            }

            return null;
        }

        private string ResolveEffectiveContentRoot(string? contentRootPath)
        {
            var fallbackRoot = string.IsNullOrWhiteSpace(contentRootPath)
                ? AppContext.BaseDirectory
                : contentRootPath;

            var resolvedRoot = TryResolveListenarrApiRoot(fallbackRoot);
            if (!string.IsNullOrWhiteSpace(resolvedRoot) &&
                !string.Equals(resolvedRoot, fallbackRoot, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "Resolved image controller content root to repo path: {ResolvedRoot}",
                    LogRedaction.SanitizeText(resolvedRoot));
                return resolvedRoot;
            }

            return fallbackRoot;
        }

        private static string? TryResolveListenarrApiRoot(string? startingPath)
        {
            if (string.IsNullOrWhiteSpace(startingPath))
            {
                return null;
            }

            try
            {
                var dir = new DirectoryInfo(Path.GetFullPath(startingPath));
                const int maxDepth = 8;
                var depth = 0;

                while (dir != null && depth++ < maxDepth)
                {
                    if (LooksLikeListenarrApiRoot(dir.FullName))
                    {
                        return dir.FullName;
                    }

                    var nestedApiRoot = CombineRelativePath(dir.FullName, "listenarr.api");
                    if (LooksLikeListenarrApiRoot(nestedApiRoot))
                    {
                        return nestedApiRoot;
                    }

                    dir = dir.Parent;
                }
            }
            catch (Exception ex) when (
                ex is IOException or
                UnauthorizedAccessException or
                ArgumentException or
                System.Security.SecurityException or
                NotSupportedException or
                PathTooLongException)
            {
                return null;
            }

            return null;
        }

        private static bool LooksLikeListenarrApiRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return false;
            }

            var hasConfigDirectory = Directory.Exists(CombineRelativePath(path, "config"));
            var hasProjectMarkers =
                System.IO.File.Exists(CombineRelativePath(path, "listenarr.api.csproj")) ||
                Directory.Exists(CombineRelativePath(path, "wwwroot"));

            return hasConfigDirectory && hasProjectMarkers;
        }

        private IEnumerable<string> EnumeratePlaceholderCandidates()
        {
            var baseDirectories = new[]
            {
                _effectiveContentRootPath,
                AppContext.BaseDirectory,
                Directory.GetCurrentDirectory()
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path =>
            {
                try
                {
                    return Path.GetFullPath(path);
                }
                catch (Exception ex) when (
                    ex is ArgumentException or
                    ArgumentNullException or
                    PathTooLongException or
                    NotSupportedException or
                    System.Security.SecurityException)
                {
                    return path;
                }
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

            foreach (var baseDirectory in baseDirectories)
            {
                DirectoryInfo? current = null;
                try
                {
                    current = new DirectoryInfo(baseDirectory);
                }
                catch (Exception ex) when (
                    ex is ArgumentException or
                    ArgumentNullException or
                    PathTooLongException or
                    NotSupportedException or
                    System.Security.SecurityException)
                {
                    current = null;
                }

                var depth = 0;
                while (current != null && depth++ < 8)
                {
                    yield return CombineRelativePath(current.FullName, "wwwroot", "placeholder.svg");
                    yield return CombineRelativePath(current.FullName, "fe", "public", "placeholder.svg");
                    yield return CombineRelativePath(current.FullName, "listenarr.api", "wwwroot", "placeholder.svg");

                    current = current.Parent;
                }
            }
        }

        /// <summary>
        /// Delete a cached cover image by identifier.
        /// </summary>
        /// <param name="identifier">Image identifier to delete.</param>
        [HttpDelete("{identifier}")]
        public async Task<IActionResult> DeleteImage(string identifier)
        {
            if (string.IsNullOrEmpty(identifier))
            {
                return BadRequest("Identifier is required");
            }

            try
            {
                var relativePath = await _imageCacheService.GetCachedImagePathAsync(identifier);

                if (relativePath == null)
                {
                    return NotFound(new { message = "Image not found" });
                }

                var fullPath = ResolvePathWithOptionalBase(_effectiveContentRootPath, relativePath);

                if (System.IO.File.Exists(fullPath))
                {
                    System.IO.File.Delete(fullPath);
                    _logger.LogInformation("Deleted cached image for identifier: {Identifier}", LogRedaction.SanitizeText(identifier));
                    return Ok(new { message = "Image deleted successfully" });
                }

                return NotFound(new { message = "Image file not found" });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error deleting image for identifier: {Identifier}", LogRedaction.SanitizeText(identifier));
                return StatusCode(500, new { message = "Error deleting image" });
            }
        }
    }
}



