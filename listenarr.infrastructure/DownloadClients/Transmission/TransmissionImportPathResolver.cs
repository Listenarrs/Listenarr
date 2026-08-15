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
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.DownloadClients.Transmission
{
    internal static class TransmissionImportPathResolver
    {
        public static bool IsExistingLocalPath(string? path)
        {
            return !string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path));
        }

        public static string? BuildContentPath(string? downloadDir, string? name, string? fallbackPath = null)
        {
            return !string.IsNullOrEmpty(downloadDir) && !string.IsNullOrEmpty(name)
                ? FileUtils.CombineWithOptionalBase(downloadDir, name)
                : fallbackPath;
        }

        public static List<string> BuildSourceFiles(string? downloadDir, JsonElement filesElement)
        {
            return [.. TorrentClientPathMapper.BuildTransmissionSourceFiles(downloadDir, filesElement).Where(path => !string.IsNullOrEmpty(path))];
        }
    }
}
