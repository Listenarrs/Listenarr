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

namespace Listenarr.Api.Features.Images
{
    internal enum ImageIdentifierValidationFailure
    {
        None,
        Missing,
        Invalid
    }

    internal readonly record struct ImageIdentifierValidationResult(
        string Identifier,
        ImageIdentifierValidationFailure Failure)
    {
        public bool IsValid => Failure == ImageIdentifierValidationFailure.None;
    }

    internal static class ImageIdentifierRequestValidator
    {
        public static ImageIdentifierValidationResult ValidateGetImageIdentifier(string identifier)
        {
            if (string.IsNullOrEmpty(identifier))
            {
                return new ImageIdentifierValidationResult(identifier, ImageIdentifierValidationFailure.Missing);
            }

            // Strip any query parameters from the identifier (e.g., "B0CQZ5167B?access_token=..." -> "B0CQZ5167B")
            var queryIndex = identifier.IndexOf('?');
            if (queryIndex >= 0)
            {
                identifier = identifier.Substring(0, queryIndex);
            }

            // Validate identifier to prevent path traversal or overly long values.
            // Identifiers should be simple ASINs, numeric IDs or author names-disallow path separators.
            if (identifier.IndexOfAny(new char[] { '\\', '/', '\0' }) >= 0 || identifier.Length > 256)
            {
                return new ImageIdentifierValidationResult(identifier, ImageIdentifierValidationFailure.Invalid);
            }

            return new ImageIdentifierValidationResult(identifier, ImageIdentifierValidationFailure.None);
        }
    }
}
