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

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Domain.Common;
using Listenarr.Domain.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace Listenarr.Api.Features.Library
{
    public sealed class LibraryMetadataRescanWorkflow
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

        private static IEnumerable<string> EnumerateMetadataRescanRegions(string? preferredRegion)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var ordered = new List<string>();
            void AddOrdered(string? region)
            {
                var normalized = AudiobookIdentifierNormalizer.NormalizeRegion(region);
                if (string.IsNullOrWhiteSpace(normalized)) return;
                if (seen.Add(normalized)) ordered.Add(normalized);
            }

            AddOrdered(preferredRegion);
            AddOrdered("us");
            AddOrdered("uk");

            if (ordered.Count == 0)
            {
                ordered.Add("us");
            }

            return ordered;
        }

        private static bool TryExtractMetadataLookupResult(
            object? rawResult,
            out AudibleBookResponse? metadata,
            out string? source)
        {
            metadata = null;
            source = null;
            if (rawResult == null) return false;

            if (rawResult is AudibleBookResponse direct)
            {
                metadata = direct;
                return true;
            }

            var type = rawResult.GetType();
            var metadataProp = type.GetProperty("metadata", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (metadataProp != null)
            {
                var metadataValue = metadataProp.GetValue(rawResult);
                if (metadataValue is AudibleBookResponse audible)
                {
                    metadata = audible;
                }
                else if (metadataValue is JsonElement metadataElement && metadataElement.ValueKind == JsonValueKind.Object)
                {
                    try
                    {
                        metadata = metadataElement.Deserialize<AudibleBookResponse>();
                    }
                    catch (JsonException)
                    {
                        metadata = null;
                    }
                    catch (NotSupportedException)
                    {
                        metadata = null;
                    }
                }
            }

            var sourceProp = type.GetProperty("source", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (sourceProp != null)
            {
                source = sourceProp.GetValue(rawResult)?.ToString();
            }

            return metadata != null;
        }

        private static bool ApplyMetadataRescanPatch(Audiobook audiobook, AudibleBookMetadata metadata)
        {
            var legacyIdentifierFieldsTouched = false;

            if (!string.IsNullOrWhiteSpace(metadata.Title)) audiobook.Title = metadata.Title;
            if (!string.IsNullOrWhiteSpace(metadata.Subtitle)) audiobook.Subtitle = metadata.Subtitle;
            if (!string.IsNullOrWhiteSpace(metadata.PublishYear)) audiobook.PublishYear = metadata.PublishYear;
            if (!string.IsNullOrWhiteSpace(metadata.PublishedDate)) audiobook.PublishedDate = metadata.PublishedDate;
            if (!string.IsNullOrWhiteSpace(metadata.Description)) audiobook.Description = metadata.Description;
            if (!string.IsNullOrWhiteSpace(metadata.Publisher)) audiobook.Publisher = metadata.Publisher;
            if (!string.IsNullOrWhiteSpace(metadata.Language)) audiobook.Language = metadata.Language;
            if (metadata.Runtime.HasValue && metadata.Runtime.Value > 0) audiobook.Runtime = metadata.Runtime;
            if (!string.IsNullOrWhiteSpace(metadata.Version)) audiobook.Version = metadata.Version;

            if ((metadata.SeriesMemberships != null && metadata.SeriesMemberships.Any()) ||
                !string.IsNullOrWhiteSpace(metadata.Series) ||
                !string.IsNullOrWhiteSpace(metadata.SeriesNumber))
            {
                // Preserve the user's manually-chosen primary series across a rescan rather than
                // reverting to the metadata provider's default (see issue #658).
                AudiobookSeriesMembershipHelper.ApplyToAudiobookPreservingPrimary(
                    audiobook,
                    metadata.SeriesMemberships,
                    metadata.Series,
                    metadata.SeriesNumber);
            }

            var authors = NormalizeMetadataStringList(
                (metadata.Authors != null && metadata.Authors.Any())
                    ? metadata.Authors
                    : (!string.IsNullOrWhiteSpace(metadata.Author) ? new List<string> { metadata.Author! } : null));
            if (authors.Count > 0) audiobook.Authors = authors;

            var narrators = NormalizeMetadataStringList(
                (metadata.Narrators != null && metadata.Narrators.Any())
                    ? metadata.Narrators
                    : (!string.IsNullOrWhiteSpace(metadata.Narrator) ? new List<string> { metadata.Narrator! } : null));
            if (narrators.Count > 0) audiobook.Narrators = narrators;

            var genres = NormalizeMetadataStringList(metadata.Genres);
            if (genres.Count > 0) audiobook.Genres = genres;

            var isbns = NormalizeMetadataStringList(metadata.Isbn);
            if (isbns.Count > 0)
            {
                audiobook.Isbn = isbns;
                legacyIdentifierFieldsTouched = true;
            }

            if (!string.IsNullOrWhiteSpace(metadata.Asin))
            {
                audiobook.Asin = metadata.Asin;
                legacyIdentifierFieldsTouched = true;
            }

            if (!string.IsNullOrWhiteSpace(metadata.OpenLibraryId))
            {
                audiobook.OpenLibraryId = metadata.OpenLibraryId;
                legacyIdentifierFieldsTouched = true;
            }

            return legacyIdentifierFieldsTouched;
        }

        private async Task<string?> MoveMetadataImageToLibraryStorageAsync(Audiobook audiobook, string imageUrl)
        {
            if (string.IsNullOrWhiteSpace(imageUrl)) return null;

            try
            {
                var imageKey = !string.IsNullOrWhiteSpace(audiobook.Asin)
                    ? audiobook.Asin!
                    : (audiobook.Isbn != null && audiobook.Isbn.Any(i => !string.IsNullOrWhiteSpace(i))
                        ? "img-" + ComputeShortHash(audiobook.Isbn.First(i => !string.IsNullOrWhiteSpace(i)))
                        : "img-" + ComputeShortHash($"{audiobook.Title}|{audiobook.Authors?.FirstOrDefault()}"));

                var libraryImagePath = await _imageCacheService.MoveToLibraryStorageAsync(imageKey, imageUrl);
                if (string.IsNullOrWhiteSpace(libraryImagePath))
                {
                    return null;
                }

                return "/" + libraryImagePath.TrimStart('/');
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Failed to move rescanned metadata image for audiobook {AudiobookId}", audiobook.Id);
                return null;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Failed to move rescanned metadata image for audiobook {AudiobookId}", audiobook.Id);
                return null;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Failed to move rescanned metadata image for audiobook {AudiobookId}", audiobook.Id);
                return null;
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogWarning(ex, "Failed to move rescanned metadata image for audiobook {AudiobookId}", audiobook.Id);
                return null;
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Failed to move rescanned metadata image for audiobook {AudiobookId}", audiobook.Id);
                return null;
            }
            catch (UriFormatException ex)
            {
                _logger.LogWarning(ex, "Failed to move rescanned metadata image for audiobook {AudiobookId}", audiobook.Id);
                return null;
            }
        }

        private static List<string> NormalizeMetadataStringList(IEnumerable<string>? values)
        {
            if (values == null) return new List<string>();

            return values
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string? FirstNonEmpty(params string?[] values)
        {
            var first = values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            return first?.Trim();
        }

        private static bool TryConsumeMetadataRescanQuota(
            IMemoryCache cache,
            HttpContext? httpContext,
            int audiobookId,
            out string message,
            out int retryAfterSeconds)
        {
            message = string.Empty;
            retryAfterSeconds = 0;

            var actorKey = BuildMetadataRescanActorKey(httpContext);
            var cacheKey = $"metadata-rescan-rate:{audiobookId}:{actorKey}";
            var now = DateTime.UtcNow;

            if (!cache.TryGetValue(cacheKey, out MetadataRescanRateLimitState? state) || state == null)
            {
                state = new MetadataRescanRateLimitState
                {
                    WindowStartUtc = now,
                    Count = 0,
                    LastAttemptUtc = null
                };
            }

            if (state.LastAttemptUtc.HasValue)
            {
                var cooldownRemaining = TimeSpan.FromSeconds(MetadataRescanCooldownSeconds) - (now - state.LastAttemptUtc.Value);
                if (cooldownRemaining > TimeSpan.Zero)
                {
                    retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(cooldownRemaining.TotalSeconds));
                    message = $"Rescan cooldown active. Please wait {retryAfterSeconds} seconds before rescanning this audiobook again.";
                    return false;
                }
            }

            if ((now - state.WindowStartUtc) >= TimeSpan.FromMinutes(MetadataRescanWindowMinutes))
            {
                state.WindowStartUtc = now;
                state.Count = 0;
            }

            if (state.Count >= MetadataRescanMaxRequestsPerWindow)
            {
                var windowEndsAt = state.WindowStartUtc.AddMinutes(MetadataRescanWindowMinutes);
                var remaining = windowEndsAt - now;
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
                message = $"Metadata rescan rate limit reached for this audiobook. Try again in {retryAfterSeconds} seconds.";
                return false;
            }

            state.Count++;
            state.LastAttemptUtc = now;

            cache.Set(
                cacheKey,
                state,
                new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(MetadataRescanWindowMinutes + 5)
                });

            return true;
        }

        private static string BuildMetadataRescanActorKey(HttpContext? httpContext)
        {
            var user = httpContext?.User;
            var userId =
                user?.FindFirst("sub")?.Value ??
                user?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ??
                user?.Identity?.Name;

            var remoteIp = httpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";

            var actorDescriptor = !string.IsNullOrWhiteSpace(userId)
                ? $"user:{userId}|ip:{remoteIp}"
                : $"ip:{remoteIp}";

            return ComputeShortHash(actorDescriptor);
        }

        private static string ComputeShortHash(string? input)
        {
            if (string.IsNullOrEmpty(input))
                return Guid.NewGuid().ToString("N").Substring(0, 12);

            var bytes = Encoding.UTF8.GetBytes(input);
            var hash = SHA1.HashData(bytes);
            return BitConverter.ToString(hash).Replace("-", "").Substring(0, 16).ToLowerInvariant();
        }

        private sealed class MetadataRescanRateLimitState
        {
            public DateTime WindowStartUtc { get; set; }
            public int Count { get; set; }
            public DateTime? LastAttemptUtc { get; set; }
        }
    }
}
