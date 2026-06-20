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


namespace Listenarr.Application.Audiobooks.Identifiers;

public static class AudiobookIdentifierMapper
{
    public static AudiobookIdentifierResponseItem ToIdentifierResponse(AudiobookExternalIdentifier identifier)
    {
        return new AudiobookIdentifierResponseItem
        {
            Id = identifier.Id,
            Type = identifier.Type,
            Value = string.IsNullOrWhiteSpace(identifier.ValueRaw) ? identifier.ValueNormalized : identifier.ValueRaw,
            ValueNormalized = identifier.ValueNormalized,
            Region = identifier.Region,
            IsPrimary = identifier.IsPrimary,
            Source = identifier.Source,
            CreatedAt = identifier.CreatedAt,
            UpdatedAt = identifier.UpdatedAt
        };
    }

    public static List<AudiobookExternalIdentifier> OrderIdentifiers(IEnumerable<AudiobookExternalIdentifier>? identifiers)
    {
        return (identifiers ?? Enumerable.Empty<AudiobookExternalIdentifier>())
            .OrderBy(i => i.Type)
            .ThenByDescending(i => i.IsPrimary)
            .ThenBy(i => i.Source)
            .ThenBy(i => i.ValueNormalized)
            .ToList();
    }

    public static List<AudiobookExternalIdentifier> BuildLegacyBackfillIdentifiers(
        Audiobook audiobook,
        AudiobookExternalIdentifierSource source)
    {
        var now = DateTime.UtcNow;
        var result = new List<AudiobookExternalIdentifier>();

        if (!string.IsNullOrWhiteSpace(audiobook.Asin) &&
            AudiobookIdentifierNormalizer.TryNormalize(AudiobookExternalIdentifierType.Asin, audiobook.Asin, out var normalizedAsin, out _))
        {
            result.Add(new AudiobookExternalIdentifier
            {
                Type = AudiobookExternalIdentifierType.Asin,
                ValueRaw = AudiobookIdentifierNormalizer.NormalizeRawValueForStorage(audiobook.Asin),
                ValueNormalized = normalizedAsin,
                Region = null,
                IsPrimary = true,
                Source = source,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        var seenIsbns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var isbn in audiobook.Isbn ?? new List<string>())
        {
            if (!AudiobookIdentifierNormalizer.TryNormalize(AudiobookExternalIdentifierType.Isbn, isbn, out var normalizedIsbn, out _))
            {
                continue;
            }

            if (!seenIsbns.Add(normalizedIsbn)) continue;

            result.Add(new AudiobookExternalIdentifier
            {
                Type = AudiobookExternalIdentifierType.Isbn,
                ValueRaw = AudiobookIdentifierNormalizer.NormalizeRawValueForStorage(isbn),
                ValueNormalized = normalizedIsbn,
                Region = null,
                IsPrimary = false,
                Source = source,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        if (!string.IsNullOrWhiteSpace(audiobook.OpenLibraryId) &&
            AudiobookIdentifierNormalizer.TryNormalize(AudiobookExternalIdentifierType.OpenLibraryId, audiobook.OpenLibraryId, out var normalizedOlid, out _))
        {
            result.Add(new AudiobookExternalIdentifier
            {
                Type = AudiobookExternalIdentifierType.OpenLibraryId,
                ValueRaw = AudiobookIdentifierNormalizer.NormalizeRawValueForStorage(audiobook.OpenLibraryId),
                ValueNormalized = normalizedOlid,
                Region = null,
                IsPrimary = true,
                Source = source,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        return result;
    }

    public static string TypeValueKey(AudiobookExternalIdentifier item)
    {
        return $"{item.Type}|{item.ValueNormalized}";
    }

    public static string FullKey(AudiobookExternalIdentifier item)
    {
        return $"{item.Type}|{item.ValueNormalized}|{item.Region ?? string.Empty}";
    }

    public static string FullSourceKey(AudiobookExternalIdentifier item)
    {
        return FullSourceKey(item.Type, item.ValueNormalized, item.Region, item.Source);
    }

    public static string FullSourceKey(
        AudiobookExternalIdentifierType type,
        string? valueNormalized,
        string? region,
        AudiobookExternalIdentifierSource source)
    {
        return $"{type}|{valueNormalized ?? string.Empty}|{region ?? string.Empty}|{source}";
    }

    public static List<AudiobookExternalIdentifier> GetEffectiveIdentifiers(Audiobook audiobook)
    {
        var merged = new List<AudiobookExternalIdentifier>();
        var seenFull = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTypeValue = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfNew(AudiobookExternalIdentifier item)
        {
            if (string.IsNullOrWhiteSpace(item.ValueNormalized)) return;

            var typeValueKey = TypeValueKey(item);
            if (item.Source == AudiobookExternalIdentifierSource.Imported && seenTypeValue.Contains(typeValueKey))
            {
                return;
            }

            var fullKey = FullKey(item);
            if (!seenFull.Add(fullKey)) return;
            merged.Add(item);
            seenTypeValue.Add(typeValueKey);
        }

        foreach (var existing in (audiobook.ExternalIdentifiers ?? new List<AudiobookExternalIdentifier>())
            .OrderBy(i => i.Type)
            .ThenByDescending(i => i.IsPrimary)
            .ThenBy(i => i.Source == AudiobookExternalIdentifierSource.Imported ? 1 : 0)
            .ThenBy(i => i.Source)
            .ThenBy(i => i.ValueNormalized))
        {
            AddIfNew(existing);
        }

        foreach (var legacy in BuildLegacyBackfillIdentifiers(audiobook, AudiobookExternalIdentifierSource.Imported))
        {
            AddIfNew(legacy);
        }

        return OrderIdentifiers(merged);
    }

    public static void SyncLegacyFieldsFromIdentifiers(Audiobook audiobook)
    {
        var identifiers = OrderIdentifiers(audiobook.ExternalIdentifiers);

        var primaryAsin = identifiers
            .Where(i => i.Type == AudiobookExternalIdentifierType.Asin)
            .OrderByDescending(i => i.IsPrimary)
            .ThenBy(i => i.Source)
            .FirstOrDefault();
        audiobook.Asin = primaryAsin?.ValueNormalized;

        audiobook.Isbn = identifiers
            .Where(i => i.Type == AudiobookExternalIdentifierType.Isbn)
            .Select(i => i.ValueNormalized)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var primaryOlid = identifiers
            .Where(i => i.Type == AudiobookExternalIdentifierType.OpenLibraryId)
            .OrderByDescending(i => i.IsPrimary)
            .ThenBy(i => i.Source)
            .FirstOrDefault();
        audiobook.OpenLibraryId = primaryOlid?.ValueNormalized;
    }

    public static void SyncImportedIdentifiersFromLegacyFields(Audiobook audiobook)
    {
        audiobook.ExternalIdentifiers ??= new List<AudiobookExternalIdentifier>();

        audiobook.ExternalIdentifiers = audiobook.ExternalIdentifiers
            .Where(i => i.Source != AudiobookExternalIdentifierSource.Imported)
            .ToList();

        var existingTypeValueKeys = new HashSet<string>(
            audiobook.ExternalIdentifiers
                .Where(i => !string.IsNullOrWhiteSpace(i.ValueNormalized))
                .Select(TypeValueKey),
            StringComparer.OrdinalIgnoreCase);
        var seenImportedFullKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var imported = BuildLegacyBackfillIdentifiers(audiobook, AudiobookExternalIdentifierSource.Imported);
        foreach (var item in imported.Where(item =>
                     !string.IsNullOrWhiteSpace(item.ValueNormalized) &&
                     !existingTypeValueKeys.Contains(TypeValueKey(item)) &&
                     seenImportedFullKeys.Add(FullKey(item))))
        {
            audiobook.ExternalIdentifiers.Add(item);
        }
    }
}
