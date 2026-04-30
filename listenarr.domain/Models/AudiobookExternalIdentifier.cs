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
namespace Listenarr.Domain.Models
{
    public enum AudiobookExternalIdentifierType
    {
        Asin = 0,
        Isbn = 1,
        OpenLibraryId = 2
    }

    public enum AudiobookExternalIdentifierSource
    {
        Provider = 0,
        Imported = 1,
        Manual = 2
    }

    public class AudiobookExternalIdentifier
    {
        public int Id { get; set; }
        public int AudiobookId { get; set; }
        public AudiobookExternalIdentifierType Type { get; set; }
        public string ValueRaw { get; set; } = string.Empty;
        public string ValueNormalized { get; set; } = string.Empty;
        public string? Region { get; set; }
        public bool IsPrimary { get; set; }
        public AudiobookExternalIdentifierSource Source { get; set; } = AudiobookExternalIdentifierSource.Imported;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    public static class AudiobookIdentifierNormalizer
    {
        public static bool TryNormalize(
            AudiobookExternalIdentifierType type,
            string? rawValue,
            out string normalizedValue,
            out string? error)
        {
            normalizedValue = string.Empty;
            error = null;

            if (string.IsNullOrWhiteSpace(rawValue))
            {
                error = "Identifier value is required.";
                return false;
            }

            var trimmed = rawValue.Trim();
            switch (type)
            {
                case AudiobookExternalIdentifierType.Asin:
                    {
                        var candidate = new string(trimmed.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
                        if (candidate.Length != 10 || !candidate.All(char.IsLetterOrDigit))
                        {
                            error = "ASIN must be 10 alphanumeric characters.";
                            return false;
                        }
                        normalizedValue = candidate;
                        return true;
                    }
                case AudiobookExternalIdentifierType.Isbn:
                    {
                        var candidate = new string(trimmed.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
                        if (!IsValidIsbn(candidate))
                        {
                            error = "ISBN must be a valid ISBN-10 or ISBN-13.";
                            return false;
                        }
                        normalizedValue = candidate;
                        return true;
                    }
                case AudiobookExternalIdentifierType.OpenLibraryId:
                    {
                        var candidate = NormalizeOpenLibraryId(trimmed);
                        if (string.IsNullOrWhiteSpace(candidate))
                        {
                            error = "OpenLibrary ID is invalid.";
                            return false;
                        }
                        normalizedValue = candidate;
                        return true;
                    }
                default:
                    error = "Unsupported identifier type.";
                    return false;
            }
        }

        public static string? NormalizeRegion(string? region)
        {
            if (string.IsNullOrWhiteSpace(region)) return null;
            var candidate = new string(region.Trim().Where(char.IsLetter).ToArray()).ToLowerInvariant();
            return candidate.Length switch
            {
                0 => null,
                > 8 => candidate[..8],
                _ => candidate
            };
        }

        public static string NormalizeRawValueForStorage(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private static string? NormalizeOpenLibraryId(string value)
        {
            var v = value.Trim();
            if (Uri.TryCreate(v, UriKind.Absolute, out var abs))
            {
                v = abs.AbsolutePath;
            }

            v = v.Trim('/');
            var segments = v.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var candidate = segments.Length > 0 ? segments[^1] : v;
            if (string.IsNullOrWhiteSpace(candidate)) return null;
            return candidate.Trim().ToUpperInvariant();
        }

        private static bool IsValidIsbn(string value)
        {
            if (value.Length == 10)
            {
                return IsValidIsbn10(value);
            }

            if (value.Length == 13)
            {
                return IsValidIsbn13(value);
            }

            return false;
        }

        private static bool IsValidIsbn10(string value)
        {
            if (value.Length != 10) return false;
            for (var i = 0; i < 9; i++)
            {
                if (!char.IsDigit(value[i])) return false;
            }

            if (!(char.IsDigit(value[9]) || value[9] == 'X')) return false;

            var sum = 0;
            for (var i = 0; i < 9; i++)
            {
                sum += (10 - i) * (value[i] - '0');
            }

            sum += value[9] == 'X' ? 10 : value[9] - '0';
            return sum % 11 == 0;
        }

        private static bool IsValidIsbn13(string value)
        {
            if (value.Length != 13 || !value.All(char.IsDigit)) return false;

            var sum = 0;
            for (var i = 0; i < 12; i++)
            {
                var digit = value[i] - '0';
                sum += (i % 2 == 0) ? digit : digit * 3;
            }

            var expectedCheck = (10 - (sum % 10)) % 10;
            var actualCheck = value[12] - '0';
            return expectedCheck == actualCheck;
        }
    }
}
