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

namespace Listenarr.Infrastructure.Ffmpeg.Installation
{
    internal static class FfprobeChecksumParser
    {
        public static string? ParseForAsset(string checksumFileContent, string assetFileName)
        {
            if (string.IsNullOrEmpty(checksumFileContent) || string.IsNullOrEmpty(assetFileName)) return null;
            using var sr = new StringReader(checksumFileContent);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                // Common formats: "<checksum>  <filename>" or "<checksum>  *<filename>" or "<checksum> <filename>"
                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    var possibleHash = parts[0].Trim();
                    var possibleName = parts[^1].Trim();
                    if (possibleName.StartsWith("*")) possibleName = possibleName[1..];
                    if (possibleName.Equals(assetFileName, StringComparison.OrdinalIgnoreCase) || possibleName.EndsWith(assetFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        return possibleHash;
                    }
                }
                else
                {
                    // Some checksum files list "filename: hash" or JSON; do simple contains
                    if (trimmed.Contains(assetFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        var tokens = trimmed.Split(':', StringSplitOptions.RemoveEmptyEntries);
                        if (tokens.Length >= 2)
                        {
                            var candidate = tokens[1].Trim();
                            var candidateToken = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                            if (!string.IsNullOrEmpty(candidateToken)) return candidateToken;
                        }
                    }
                }
            }

            return null;
        }
    }
}
