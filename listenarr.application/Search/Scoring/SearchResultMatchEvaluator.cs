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


namespace Listenarr.Application.Search.Scoring
{
    internal static class SearchResultMatchEvaluator
    {
        public static double ComputeContainmentScore(SearchResult result, string query)
        {
            if (result == null || string.IsNullOrWhiteSpace(query))
            {
                return 0.0;
            }

            var hay = string.Join(" ", new[] { result.Title, result.Artist, result.Album, result.Description, result.Publisher, result.Narrator, result.Language, result.Series }
                .Where(x => !string.IsNullOrWhiteSpace(x)));

            var hayTokens = TokenizeAndNormalize(hay);
            var queryTokens = TokenizeAndNormalize(query);

            if (!queryTokens.Any())
            {
                return 0.0;
            }

            var haySet = new HashSet<string>(hayTokens, StringComparer.OrdinalIgnoreCase);
            var matched = queryTokens.Count(haySet.Contains);

            for (var i = 0; i < queryTokens.Count; i++)
            {
                var queryToken = queryTokens[i];
                if (haySet.Contains(queryToken))
                {
                    continue;
                }

                if (haySet.Any(hayToken => hayToken.Contains(queryToken) || queryToken.Contains(hayToken)))
                {
                    matched += 1;
                }
            }

            return Math.Min(1.0, (double)matched / Math.Max(1, queryTokens.Count));
        }

        public static double ComputeFuzzySimilarity(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b))
            {
                return 1.0;
            }

            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            {
                return 0.0;
            }

            var normalizedA = NormalizeForFuzzy(a);
            var normalizedB = NormalizeForFuzzy(b);
            var distance = LevenshteinDistance(normalizedA, normalizedB);
            var max = Math.Max(normalizedA.Length, normalizedB.Length);
            if (max == 0)
            {
                return 1.0;
            }

            var similarity = 1.0 - ((double)distance / max);
            return Math.Max(0.0, Math.Min(1.0, similarity));
        }

        private static List<string> TokenizeAndNormalize(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return new List<string>();
            }

            var normalized = input.ToLowerInvariant();
            var builder = new System.Text.StringBuilder(normalized.Length);
            foreach (var character in normalized)
            {
                builder.Append(char.IsLetterOrDigit(character) || character == '-' || char.IsWhiteSpace(character)
                    ? character
                    : ' ');
            }

            return builder
                .ToString()
                .Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(token => token.Length > 0)
                .ToList();
        }

        private static string NormalizeForFuzzy(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var lowered = value.ToLowerInvariant();
            var builder = new System.Text.StringBuilder(lowered.Length);
            foreach (var character in lowered.Where(character => char.IsLetterOrDigit(character) || character == '-'))
            {
                builder.Append(character);
            }

            return builder.ToString();
        }

        private static int LevenshteinDistance(string source, string target)
        {
            if (source == target)
            {
                return 0;
            }

            if (string.IsNullOrEmpty(source))
            {
                return target.Length;
            }

            if (string.IsNullOrEmpty(target))
            {
                return source.Length;
            }

            var sourceLength = source.Length;
            var targetLength = target.Length;
            var distances = new int[sourceLength + 1, targetLength + 1];

            for (var i = 0; i <= sourceLength; distances[i, 0] = i++)
            {
            }

            for (var j = 0; j <= targetLength; distances[0, j] = j++)
            {
            }

            for (var i = 1; i <= sourceLength; i++)
            {
                for (var j = 1; j <= targetLength; j++)
                {
                    var cost = target[j - 1] == source[i - 1] ? 0 : 1;
                    distances[i, j] = Math.Min(
                        Math.Min(distances[i - 1, j] + 1, distances[i, j - 1] + 1),
                        distances[i - 1, j - 1] + cost);
                }
            }

            return distances[sourceLength, targetLength];
        }
    }
}
