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
using Listenarr.Domain.Common;

namespace Listenarr.Api.Features.Images
{
    internal static class ImageIdentifierHelper
    {
        public static bool LooksLikeAsin(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = value.Trim();
            if (v.Length != 10) return false;
            return v.All(char.IsLetterOrDigit);
        }

        public static bool LooksLikeIsbn(string value)
        {
            var v = NormalizeIsbn(value);
            if (string.IsNullOrWhiteSpace(v)) return false;
            if (v.Length == 10)
            {
                for (var i = 0; i < 9; i++)
                {
                    if (!char.IsDigit(v[i])) return false;
                }
                return char.IsDigit(v[9]) || v[9] == 'X';
            }

            if (v.Length == 13)
            {
                return v.All(char.IsDigit);
            }

            return false;
        }

        public static string NormalizeIsbn(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            return new string(value.Where(ch => char.IsLetterOrDigit(ch)).ToArray()).ToUpperInvariant();
        }

        public static string? NormalizeOpenLibraryId(string? value)
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
            return candidate.Trim();
        }

        public static string? NormalizeHttpImageUrl(string? value)
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

        public static bool IsRecoverableImageLookupException(Exception ex)
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

        public static string ResolvePathWithOptionalBase(string? basePath, string candidatePath)
        {
            return FileUtils.CombineWithOptionalBase(basePath, candidatePath.Trim());
        }
    }
}
