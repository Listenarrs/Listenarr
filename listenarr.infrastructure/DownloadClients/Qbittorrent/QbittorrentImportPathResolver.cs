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
using System.Text.Json;

namespace Listenarr.Infrastructure.DownloadClients.Qbittorrent
{
    internal static class QbittorrentImportPathResolver
    {
        public static List<string> BuildSourceFiles(
            string savePath,
            List<Dictionary<string, JsonElement>> files)
        {
            return TorrentClientPathMapper.BuildQbittorrentSourceFiles(savePath, files);
        }

        public static bool HasUnusableFileMetadata(List<Dictionary<string, JsonElement>> files)
        {
            return files.Count > 0
                && files.All(file => IsZero(file, "priority") && IsZero(file, "progress"));
        }

        private static bool IsZero(Dictionary<string, JsonElement> file, string key)
        {
            if (!file.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.Number)
            {
                return false;
            }

            // Inspect the validated JSON mantissa so nonzero values cannot underflow to zero.
            foreach (var character in value.GetRawText())
            {
                if (character is 'e' or 'E') break;
                if (character is >= '1' and <= '9') return false;
            }

            return true;
        }

        public static List<string> TranslateSourceFiles(IEnumerable<string> sourceFiles)
        {
            return sourceFiles
                .Where(path => !string.IsNullOrEmpty(path))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        public static string ResolveContentPath(
            string savePath,
            List<Dictionary<string, JsonElement>> files)
        {
            return TorrentClientPathMapper.ResolveQbittorrentContentPath(savePath, files);
        }
    }
}
