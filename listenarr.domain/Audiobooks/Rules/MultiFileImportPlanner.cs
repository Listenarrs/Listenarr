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

namespace Listenarr.Domain.Audiobooks.Rules
{
    public static class MultiFileImportPlanner
    {
        private static readonly Regex NumericChunkPattern = new(@"\d+|\D+", RegexOptions.Compiled);
        private static readonly Regex DiskPattern = new(
            @"\b(?:disc|disk|cd|part|pt|volume|vol)\s*[-_. ]*0*(\d+)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ChapterPattern = new(
            @"\b(?:chapter|ch|track)\s*[-_. ]*0*(\d+)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex LeadingNumberPattern = new(
            @"^(?:track\s*)?0*(\d+)(?:[\s._-]|$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex TrailingNumberPattern = new(
            @"(?:^|[\s._\-\(\[])\s*0*(\d+)\s*(?:[\)\]]|$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static List<PlannedImportFile> BuildPlans(IEnumerable<(string FullPath, string? RelativePath)> inputs)
        {
            var distinct = new Dictionary<string, (string FullPath, string? RelativePath)>(StringComparer.OrdinalIgnoreCase);
            foreach (var input in inputs)
            {
                if (string.IsNullOrWhiteSpace(input.FullPath))
                {
                    continue;
                }

                if (!distinct.ContainsKey(input.FullPath))
                {
                    distinct[input.FullPath] = input;
                }
            }

            var ordered = distinct.Values
                .OrderBy(i => ToNaturalSortKey(GetComparablePath(i.RelativePath, i.FullPath)), StringComparer.Ordinal)
                .ThenBy(i => GetComparablePath(i.RelativePath, i.FullPath), StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var plans = new List<PlannedImportFile>(ordered.Count);
            for (var index = 0; index < ordered.Count; index++)
            {
                var item = ordered[index];
                var (diskNumber, chapterNumber) = InferOrderHints(item.RelativePath, item.FullPath);

                if (ordered.Count > 1)
                {
                    diskNumber ??= chapterNumber;
                    chapterNumber ??= diskNumber;

                    if (!diskNumber.HasValue && !chapterNumber.HasValue)
                    {
                        diskNumber = index + 1;
                        chapterNumber = index + 1;
                    }
                }

                plans.Add(new PlannedImportFile
                {
                    FullPath = item.FullPath,
                    RelativePath = item.RelativePath,
                    SequenceNumber = index + 1,
                    DiskNumberHint = diskNumber,
                    ChapterNumberHint = chapterNumber
                });
            }

            return plans;
        }

        public static Dictionary<string, int> BuildStableNamingNumbers(
            IEnumerable<PlannedImportFile> plans,
            Func<PlannedImportFile, int?> selector)
        {
            var orderedPlans = plans
                .OrderBy(p => p.SequenceNumber)
                .ToList();

            var rawValues = orderedPlans
                .Select(selector)
                .ToList();

            var canUseRawValues = rawValues.Count == orderedPlans.Count
                && rawValues.All(v => v.HasValue)
                && AreStrictlyIncreasing(rawValues.Select(v => v!.Value));

            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < orderedPlans.Count; i++)
            {
                result[orderedPlans[i].FullPath] = canUseRawValues
                    ? rawValues[i]!.Value
                    : orderedPlans[i].SequenceNumber;
            }

            return result;
        }

        private static (int? DiskNumber, int? ChapterNumber) InferOrderHints(string? relativePath, string fullPath)
        {
            var pathForHints = GetComparablePath(relativePath, fullPath);
            var fileStem = Path.GetFileNameWithoutExtension(pathForHints);
            var parentDirectory = Path.GetFileName(Path.GetDirectoryName(pathForHints) ?? string.Empty);
            var combined = fileStem;
            if (!string.IsNullOrWhiteSpace(parentDirectory)
                && Regex.IsMatch(parentDirectory, @"\b(?:disc|disk|cd|part|pt|volume|vol|chapter|ch|track)\b", RegexOptions.IgnoreCase))
            {
                combined = parentDirectory + " " + fileStem;
            }

            int? diskNumber = TryParseNumber(DiskPattern.Match(combined));
            int? chapterNumber = TryParseNumber(ChapterPattern.Match(fileStem));

            chapterNumber ??= TryParseNumber(LeadingNumberPattern.Match(fileStem));
            chapterNumber ??= TryParseNumber(TrailingNumberPattern.Match(fileStem));

            if (!chapterNumber.HasValue)
            {
                chapterNumber = TryParseNumber(ChapterPattern.Match(combined));
            }

            return (diskNumber, chapterNumber);
        }

        private static string GetComparablePath(string? relativePath, string fullPath)
        {
            var preferred = string.IsNullOrWhiteSpace(relativePath) ? fullPath : relativePath;
            return preferred.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        }

        private static string ToNaturalSortKey(string value)
        {
            return string.Concat(
                NumericChunkPattern.Matches(value).Select(match =>
                {
                    var chunk = match.Value;
                    return int.TryParse(chunk, out var numeric)
                        ? numeric.ToString("D12")
                        : chunk.ToUpperInvariant();
                }));
        }

        private static int? TryParseNumber(Match match)
        {
            if (!match.Success)
            {
                return null;
            }

            return int.TryParse(match.Groups[1].Value, out var value) ? value : null;
        }

        private static bool AreStrictlyIncreasing(IEnumerable<int> values)
        {
            var hasPrevious = false;
            var previous = 0;

            foreach (var value in values)
            {
                if (hasPrevious && value <= previous)
                {
                    return false;
                }

                previous = value;
                hasPrevious = true;
            }

            return true;
        }
    }
}
