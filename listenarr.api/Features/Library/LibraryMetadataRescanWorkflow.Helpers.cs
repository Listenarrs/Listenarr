/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
namespace Listenarr.Api.Features.Library
{
    public sealed partial class LibraryMetadataRescanWorkflow
    {
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
