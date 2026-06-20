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

namespace Listenarr.Api.Features.Library
{
    public sealed class LibraryIdentifierWorkflow
    {
        private readonly IAudiobookRepository _repo;
        private readonly ILogger<LibraryIdentifierWorkflow> _logger;

        public LibraryIdentifierWorkflow(
            IAudiobookRepository repo,
            ILogger<LibraryIdentifierWorkflow> logger)
        {
            _repo = repo;
            _logger = logger;
        }

        public async Task<IActionResult> GetAsync(int id)
        {
            var audiobook = await _repo.GetByIdAsync(id);

            if (audiobook == null)
            {
                return new NotFoundObjectResult(new { message = "Audiobook not found" });
            }

            var identifiers = AudiobookIdentifierMapper.GetEffectiveIdentifiers(audiobook)
                .Select(AudiobookIdentifierMapper.ToIdentifierResponse)
                .ToList();

            return new OkObjectResult(new
            {
                audiobookId = audiobook.Id,
                identifiers
            });
        }

        public async Task<IActionResult> ReplaceAsync(int id, ReplaceAudiobookIdentifiersRequest? request)
        {
            var audiobook = await _repo.GetByIdAsync(id);

            if (audiobook == null)
            {
                return new NotFoundObjectResult(new { message = "Audiobook not found" });
            }

            var incoming = request?.Identifiers ?? new List<AudiobookIdentifierWriteItem>();
            if (incoming.Count > 50)
            {
                return new BadRequestObjectResult(new { message = "Too many identifiers. Maximum is 50." });
            }

            var normalizedResult = NormalizeIdentifiers(audiobook, incoming);
            if (normalizedResult.ValidationErrors.Count > 0)
            {
                return new BadRequestObjectResult(new { message = "Identifier validation failed.", errors = normalizedResult.ValidationErrors });
            }

            var normalized = normalizedResult.Identifiers;
            EnsurePrimaryIdentifiers(normalized);

            audiobook.ExternalIdentifiers = normalized;
            AudiobookIdentifierMapper.SyncLegacyFieldsFromIdentifiers(audiobook);

            await _repo.UpdateWithIdentifierReplaceAsync(audiobook, normalized);

            _logger.LogInformation(
                "Replaced identifiers for audiobook {AudiobookId} ({Title}). Count={Count}",
                audiobook.Id,
                audiobook.Title,
                normalized.Count);

            return new OkObjectResult(new
            {
                message = "Audiobook identifiers updated successfully",
                audiobook = new
                {
                    id = audiobook.Id,
                    asin = audiobook.Asin,
                    isbn = audiobook.Isbn,
                    openLibraryId = audiobook.OpenLibraryId
                },
                identifiers = AudiobookIdentifierMapper.OrderIdentifiers(audiobook.ExternalIdentifiers)
                    .Select(AudiobookIdentifierMapper.ToIdentifierResponse)
                    .ToList()
            });
        }

        private static (List<AudiobookExternalIdentifier> Identifiers, List<object> ValidationErrors) NormalizeIdentifiers(
            Audiobook audiobook,
            List<AudiobookIdentifierWriteItem> incoming)
        {
            var validationErrors = new List<object>();
            var normalized = new List<AudiobookExternalIdentifier>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var primaryCountByType = new Dictionary<AudiobookExternalIdentifierType, int>();
            var now = DateTime.UtcNow;
            var existingServerOwnedSourceKeys = new HashSet<string>(
                (audiobook.ExternalIdentifiers ?? new List<AudiobookExternalIdentifier>())
                    .Where(identifier =>
                        identifier.Source != AudiobookExternalIdentifierSource.Manual &&
                        !string.IsNullOrWhiteSpace(identifier.ValueNormalized))
                    .Select(AudiobookIdentifierMapper.FullSourceKey),
                StringComparer.OrdinalIgnoreCase);

            for (var index = 0; index < incoming.Count; index++)
            {
                var item = incoming[index];
                if (!Enum.IsDefined(typeof(AudiobookExternalIdentifierType), item.Type))
                {
                    validationErrors.Add(new { index, field = "type", error = "Unsupported identifier type." });
                    continue;
                }

                if (!AudiobookIdentifierNormalizer.TryNormalize(item.Type, item.Value, out var normalizedValue, out var error))
                {
                    validationErrors.Add(new { index, field = "value", error = error ?? "Invalid identifier value." });
                    continue;
                }

                var normalizedRegion = item.Type == AudiobookExternalIdentifierType.Asin
                    ? AudiobookIdentifierNormalizer.NormalizeRegion(item.Region)
                    : null;

                var key = $"{item.Type}|{normalizedValue}|{normalizedRegion ?? string.Empty}";
                if (!seen.Add(key))
                {
                    validationErrors.Add(new { index, field = "value", error = "Duplicate identifier." });
                    continue;
                }

                if (item.IsPrimary)
                {
                    primaryCountByType.TryGetValue(item.Type, out var count);
                    primaryCountByType[item.Type] = count + 1;
                }

                normalized.Add(new AudiobookExternalIdentifier
                {
                    AudiobookId = audiobook.Id,
                    Type = item.Type,
                    ValueRaw = AudiobookIdentifierNormalizer.NormalizeRawValueForStorage(item.Value),
                    ValueNormalized = normalizedValue,
                    Region = normalizedRegion,
                    IsPrimary = item.IsPrimary,
                    Source = ResolveWriteSource(item, normalizedValue, normalizedRegion, existingServerOwnedSourceKeys),
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }

            foreach (var kvp in primaryCountByType.Where(kvp => kvp.Value > 1))
            {
                validationErrors.Add(new
                {
                    field = "isPrimary",
                    type = kvp.Key,
                    error = $"Only one primary identifier is allowed for type {kvp.Key}."
                });
            }

            return (normalized, validationErrors);
        }

        private static AudiobookExternalIdentifierSource ResolveWriteSource(
            AudiobookIdentifierWriteItem item,
            string normalizedValue,
            string? normalizedRegion,
            HashSet<string> existingServerOwnedSourceKeys)
        {
            var source = item.Source ?? AudiobookExternalIdentifierSource.Manual;
            if (!Enum.IsDefined(typeof(AudiobookExternalIdentifierSource), source))
            {
                return AudiobookExternalIdentifierSource.Manual;
            }

            if (source == AudiobookExternalIdentifierSource.Manual)
            {
                return source;
            }

            var requestedKey = AudiobookIdentifierMapper.FullSourceKey(item.Type, normalizedValue, normalizedRegion, source);
            return existingServerOwnedSourceKeys.Contains(requestedKey)
                ? source
                : AudiobookExternalIdentifierSource.Manual;
        }

        private static void EnsurePrimaryIdentifiers(List<AudiobookExternalIdentifier> normalized)
        {
            var asins = normalized.Where(identifier => identifier.Type == AudiobookExternalIdentifierType.Asin).ToList();
            if (asins.Count > 0 && !asins.Any(identifier => identifier.IsPrimary))
            {
                asins[0].IsPrimary = true;
            }

            var olids = normalized.Where(identifier => identifier.Type == AudiobookExternalIdentifierType.OpenLibraryId).ToList();
            if (olids.Count == 1)
            {
                olids[0].IsPrimary = true;
            }
        }
    }
}
