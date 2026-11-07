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
 */
using System.Text;

namespace Listenarr.Domain.Common
{
    /// <summary>
    /// Narrator name matching that ignores case, punctuation, spacing and accents, so a required
    /// narrator entered as "R C Bray" still matches releases and tags spelled "R.C. Bray",
    /// "R. C. Bray" or "RC Bray".
    /// </summary>
    public static class NarratorNameMatcher
    {
        /// <summary>
        /// Whether <paramref name="text"/> contains <paramref name="narrator"/> once both are
        /// compacted. A blank narrator never matches.
        /// </summary>
        public static bool Contains(string? text, string? narrator)
        {
            var needle = Compact(narrator);
            return needle.Length > 0 && Compact(text).Contains(needle, StringComparison.Ordinal);
        }

        /// <summary>
        /// Lower-cases and keeps only letters and digits, dropping accents via FormD decomposition.
        /// </summary>
        public static string Compact(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(value.Length);
            foreach (var character in value.Normalize(NormalizationForm.FormD))
            {
                if (char.IsLetterOrDigit(character))
                {
                    builder.Append(char.ToLowerInvariant(character));
                }
            }

            return builder.ToString();
        }
    }
}
