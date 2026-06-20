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

namespace Listenarr.Api.Features.Library
{
    public sealed partial class LibraryMetadataRescanWorkflow
    {
        private const int MetadataRescanCooldownSeconds = 15;
        private const int MetadataRescanWindowMinutes = 10;
        private const int MetadataRescanMaxRequestsPerWindow = 5;
        private const int MetadataRescanMaxAsinLookupAttempts = 8;
        private const int MetadataRescanMaxIsbnConversionAttempts = 5;

        private readonly IAudiobookRepository _repo;
        private readonly IAudiobookMetadataService _metadataService;
        private readonly MetadataConverters _metadataConverters;
        private readonly IImageCacheService _imageCacheService;
        private readonly ILogger<LibraryMetadataRescanWorkflow> _logger;
        private readonly IMemoryCache? _memoryCache;
        private readonly IAsinLookupService? _asinLookupService;

        public LibraryMetadataRescanWorkflow(
            IAudiobookRepository repo,
            IAudiobookMetadataService metadataService,
            MetadataConverters metadataConverters,
            IImageCacheService imageCacheService,
            ILogger<LibraryMetadataRescanWorkflow> logger,
            IMemoryCache? memoryCache = null,
            IAsinLookupService? asinLookupService = null)
        {
            _repo = repo;
            _metadataService = metadataService;
            _metadataConverters = metadataConverters;
            _imageCacheService = imageCacheService;
            _logger = logger;
            _memoryCache = memoryCache;
            _asinLookupService = asinLookupService;
        }

        public async Task<IActionResult> RescanAsync(int id, HttpContext httpContext)
        {
            var audiobook = await _repo.GetByIdAsync(id);

            if (audiobook == null)
            {
                return new NotFoundObjectResult(new { message = "Audiobook not found" });
            }

            if (_memoryCache != null &&
                !TryConsumeMetadataRescanQuota(_memoryCache, httpContext, audiobook.Id, out var rateLimitMessage, out var retryAfterSeconds))
            {
                try
                {
                    httpContext.Response.Headers["Retry-After"] = retryAfterSeconds.ToString();
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogDebug(ex, "Failed to set Retry-After header for metadata rescan rate-limit response");
                }

                return new ObjectResult(new
                {
                    message = rateLimitMessage,
                    retryAfterSeconds
                })
                {
                    StatusCode = StatusCodes.Status429TooManyRequests
                };
            }

            var effectiveIdentifiers = AudiobookIdentifierMapper.GetEffectiveIdentifiers(audiobook);
            var asinIdentifiers = effectiveIdentifiers
                .Where(i => i.Type == AudiobookExternalIdentifierType.Asin)
                .OrderByDescending(i => i.IsPrimary)
                .ThenBy(i => i.Source)
                .ThenBy(i => i.ValueNormalized)
                .ToList();

            var isbnIdentifiers = effectiveIdentifiers
                .Where(i => i.Type == AudiobookExternalIdentifierType.Isbn)
                .OrderByDescending(i => i.IsPrimary)
                .ThenBy(i => i.Source)
                .ThenBy(i => i.ValueNormalized)
                .ToList();

            if (!asinIdentifiers.Any() && !isbnIdentifiers.Any())
            {
                return new BadRequestObjectResult(new { message = "No ASIN or ISBN identifiers are available for metadata rescan." });
            }

            var triedAsinKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var triedAsinDebug = new List<object>();
            var triedIsbnDebug = new List<string>();
            var asinLookupAttempts = 0;
            var isbnConversionAttempts = 0;
            var asinLookupAttemptCapHit = false;
            var isbnConversionAttemptCapHit = false;

            AudibleBookResponse? providerMetadata = null;
            string? providerSource = null;
            string? resolvedAsin = null;
            string? resolvedRegion = null;

            async Task<bool> TryMetadataLookupByAsinAsync(string asin, string? preferredRegion, string via)
            {
                if (!AudiobookIdentifierNormalizer.TryNormalize(
                        AudiobookExternalIdentifierType.Asin,
                        asin,
                        out var normalizedAsin,
                        out _))
                {
                    return false;
                }

                foreach (var region in EnumerateMetadataRescanRegions(preferredRegion))
                {
                    var regionValue = string.IsNullOrWhiteSpace(region) ? "us" : region!;
                    var key = $"{normalizedAsin}|{regionValue}";
                    if (!triedAsinKeys.Add(key))
                    {
                        continue;
                    }

                    triedAsinDebug.Add(new { asin = normalizedAsin, region = regionValue, via });

                    if (asinLookupAttempts >= MetadataRescanMaxAsinLookupAttempts)
                    {
                        asinLookupAttemptCapHit = true;
                        return false;
                    }

                    asinLookupAttempts++;

                    object? rawResult;
                    try
                    {
                        rawResult = await _metadataService.GetMetadataAsync(normalizedAsin, regionValue, cache: false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(
                            ex,
                            "Metadata rescan lookup failed for audiobook {AudiobookId} ({Title}) ASIN {Asin} region {Region}",
                            audiobook.Id,
                            audiobook.Title,
                            normalizedAsin,
                            regionValue);
                        continue;
                    }

                    if (!TryExtractMetadataLookupResult(rawResult, out var extractedMetadata, out var extractedSource) ||
                        extractedMetadata == null)
                    {
                        continue;
                    }

                    providerMetadata = extractedMetadata;
                    providerSource = extractedSource;
                    resolvedAsin = string.IsNullOrWhiteSpace(extractedMetadata.Asin) ? normalizedAsin : extractedMetadata.Asin;
                    resolvedRegion = regionValue;
                    return true;
                }

                return false;
            }

            foreach (var asinIdentifier in asinIdentifiers)
            {
                var asinValue = FirstNonEmpty(asinIdentifier.ValueRaw, asinIdentifier.ValueNormalized);
                if (string.IsNullOrWhiteSpace(asinValue)) continue;

                if (await TryMetadataLookupByAsinAsync(asinValue, asinIdentifier.Region, "asin"))
                {
                    break;
                }

                if (asinLookupAttemptCapHit)
                {
                    break;
                }
            }

            if (providerMetadata == null)
            {
                if (_asinLookupService == null)
                {
                    _logger.LogWarning("IAsinLookupService not available for ISBN fallback during metadata rescan of audiobook {AudiobookId}", audiobook.Id);
                }

                foreach (var isbnIdentifier in isbnIdentifiers)
                {
                    var isbnValue = FirstNonEmpty(isbnIdentifier.ValueNormalized, isbnIdentifier.ValueRaw);
                    if (string.IsNullOrWhiteSpace(isbnValue)) continue;

                    if (!triedIsbnDebug.Contains(isbnValue, StringComparer.OrdinalIgnoreCase))
                    {
                        triedIsbnDebug.Add(isbnValue);
                    }

                    try
                    {
                        if (isbnConversionAttempts >= MetadataRescanMaxIsbnConversionAttempts)
                        {
                            isbnConversionAttemptCapHit = true;
                            break;
                        }

                        if (_asinLookupService == null)
                        {
                            continue;
                        }

                        isbnConversionAttempts++;
                        var (success, asinFromIsbn, _) = await _asinLookupService.GetAsinFromIsbnAsync(isbnValue);
                        if (!success || string.IsNullOrWhiteSpace(asinFromIsbn))
                        {
                            continue;
                        }

                        if (await TryMetadataLookupByAsinAsync(asinFromIsbn, null, "isbn"))
                        {
                            break;
                        }

                        if (asinLookupAttemptCapHit)
                        {
                            break;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(
                            ex,
                            "Metadata rescan ASIN conversion failed for audiobook {AudiobookId} ISBN {Isbn}",
                            audiobook.Id,
                            isbnValue);
                    }
                }
            }

            if (providerMetadata == null || string.IsNullOrWhiteSpace(resolvedAsin))
            {
                _logger.LogDebug(
                    "Metadata rescan found no metadata for audiobook {AudiobookId}. TriedAsins={TriedAsins}; TriedIsbns={TriedIsbns}; AsinLookups={AsinLookups}/{AsinCap}; IsbnConversions={IsbnConversions}/{IsbnCap}; Capped={Capped}",
                    audiobook.Id,
                    triedAsinDebug,
                    triedIsbnDebug,
                    asinLookupAttempts,
                    MetadataRescanMaxAsinLookupAttempts,
                    isbnConversionAttempts,
                    MetadataRescanMaxIsbnConversionAttempts,
                    asinLookupAttemptCapHit || isbnConversionAttemptCapHit);

                return new NotFoundObjectResult(new
                {
                    message = "No metadata found using the available identifiers."
                });
            }

            var convertedMetadata = _metadataConverters.ConvertAudibleToMetadata(
                providerMetadata,
                resolvedAsin,
                string.IsNullOrWhiteSpace(providerSource) ? "Audible" : providerSource!);

            var legacyIdentifierFieldsTouched = ApplyMetadataRescanPatch(audiobook, convertedMetadata);

            if (!string.IsNullOrWhiteSpace(convertedMetadata.ImageUrl))
            {
                audiobook.ImageUrl = await MoveMetadataImageToLibraryStorageAsync(audiobook, convertedMetadata.ImageUrl)
                    ?? convertedMetadata.ImageUrl;
            }

            if (legacyIdentifierFieldsTouched)
            {
                AudiobookIdentifierMapper.SyncImportedIdentifiersFromLegacyFields(audiobook);
            }

            await _repo.UpdateAsync(audiobook);

            _logger.LogInformation(
                "Metadata rescan updated audiobook {AudiobookId} ({Title}) using {Source} ASIN {Asin} region {Region}",
                audiobook.Id,
                audiobook.Title,
                providerSource ?? "unknown",
                resolvedAsin,
                resolvedRegion ?? "us");

            return new OkObjectResult(new
            {
                message = "Metadata rescanned successfully",
                audiobookId = audiobook.Id,
                source = providerSource,
                asin = resolvedAsin,
                region = resolvedRegion
            });
        }

    }
}
