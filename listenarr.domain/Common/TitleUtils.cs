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
using System.Text.RegularExpressions;

namespace Listenarr.Domain.Common
{
    public class TitleUtils
    {
        public static string NormalizeTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return string.Empty;

            // Remove ALL bracketed content [anything] - more robust than specific patterns
            var result = Regex.Replace(title, @"\[.*?\]", "", RegexOptions.IgnoreCase);

            // Remove ALL parentheses content (anything) - handles unknown quality/group indicators
            result = Regex.Replace(result, @"\(.*?\)", "", RegexOptions.IgnoreCase);

            // Remove curly braces content {anything}
            result = Regex.Replace(result, @"\{.*?\}", "", RegexOptions.IgnoreCase);

            // Remove common separators and replace with spaces
            result = Regex.Replace(result, @"[\-_\.]+", " ", RegexOptions.IgnoreCase);

            // Remove common quality/format indicators that might not be in brackets
            result = Regex.Replace(result, @"\b(mp3|m4a|m4b|flac|aac|ogg|opus|320|256|128|v0|v2|audiobook|unabridged|abridged)\b", "", RegexOptions.IgnoreCase);

            // Normalize multiple spaces to single spaces
            result = Regex.Replace(result, @"\s+", " ");

            // Remove trailing/leading spaces, dashes, etc.
            result = result.Trim(' ', '-', '.', ',');

            return result;
        }

        public static bool AreTitlesSimilar(string titleA, string titleB)
        {
            var normalizedA = NormalizeTitle(titleA);
            var normalizedB = NormalizeTitle(titleB);

            if (string.IsNullOrWhiteSpace(normalizedA) || string.IsNullOrWhiteSpace(normalizedB))
            {
                return false;
            }

            if (string.Equals(normalizedA, normalizedB, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var shorter = normalizedA.Length <= normalizedB.Length ? normalizedA : normalizedB;
            var longer = normalizedA.Length <= normalizedB.Length ? normalizedB : normalizedA;
            var shorterTokens = shorter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var longerTokens = longer.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (shorterTokens.Length == 0 || longerTokens.Length == 0)
            {
                return false;
            }

            if (shorterTokens.Length == 1)
            {
                var token = shorterTokens[0];
                return token.Length >= 6 &&
                       longerTokens.Contains(token, StringComparer.OrdinalIgnoreCase);
            }

            if (longer.Contains(shorter, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return TokensAppearInOrder(shorterTokens, longerTokens);
        }

        /// <summary>
        /// Check if two titles are similar (normalized comparison)
        /// </summary>
        // FIXME: Keep only one ?
        public static bool AreTitlesSimilarWithLevenstein(string title1, string title2)
        {
            if (string.IsNullOrWhiteSpace(title1) || string.IsNullOrWhiteSpace(title2))
                return false;

            var norm1 = TitleUtils.NormalizeTitle(title1);
            var norm2 = TitleUtils.NormalizeTitle(title2);

            // Exact match after normalization
            if (string.Equals(norm1, norm2, StringComparison.OrdinalIgnoreCase))
                return true;

            // Fuzzy match with Levenshtein distance (25% threshold)
            var distance = StringUtils.LevenshteinDistance(norm1, norm2);
            var maxLength = Math.Max(norm1.Length, norm2.Length);
            var similarity = 1.0 - (double)distance / maxLength;

            return similarity >= 0.75; // 75% similarity threshold
        }

        private static bool TokensAppearInOrder(string[] shorterTokens, string[] longerTokens)
        {
            if (shorterTokens == null || longerTokens == null || shorterTokens.Length == 0 || longerTokens.Length == 0)
            {
                return false;
            }

            var longerIndex = 0;
            foreach (var token in shorterTokens)
            {
                var found = false;
                while (longerIndex < longerTokens.Length)
                {
                    if (string.Equals(token, longerTokens[longerIndex], StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        longerIndex++;
                        break;
                    }

                    longerIndex++;
                }

                if (!found)
                {
                    return false;
                }
            }

            return true;
        }

        public static bool TitlesExactlyMatch(string titleA, string titleB)
        {
            var normalizedA = TitleUtils.NormalizeTitle(titleA);
            var normalizedB = TitleUtils.NormalizeTitle(titleB);

            return !string.IsNullOrWhiteSpace(normalizedA) &&
                   string.Equals(normalizedA, normalizedB, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsMatchingTitle(string title1, string title2)
        {
            if (string.IsNullOrEmpty(title1) || string.IsNullOrEmpty(title2))
                return false;

            return AreTitlesSimilar(title1, title2);
        }

        public static bool IsStrongStandaloneTitleMatch(string titleA, string titleB)
        {
            var normalizedA = NormalizeTitle(titleA);
            var normalizedB = NormalizeTitle(titleB);
            if (string.IsNullOrWhiteSpace(normalizedA) || string.IsNullOrWhiteSpace(normalizedB))
            {
                return false;
            }

            var shorter = normalizedA.Length <= normalizedB.Length ? normalizedA : normalizedB;
            var longer = normalizedA.Length <= normalizedB.Length ? normalizedB : normalizedA;
            var shorterTokenCount = shorter.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

            if (shorter.Length >= 15 || shorterTokenCount >= 3)
            {
                return true;
            }

            if (shorter.Length >= 10 &&
                (longer.StartsWith(shorter + " ", StringComparison.OrdinalIgnoreCase) ||
                 longer.EndsWith(" " + shorter, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return false;
        }
    }
}
