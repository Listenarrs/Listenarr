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

namespace Listenarr.Infrastructure.SystemDiagnostics.Storage
{
    internal static class SystemStorageMapper
    {
        public static DiskStorageInfo MeasureDisk(IDiskSpaceProbe diskSpaceProbe, string label, string path)
        {
            if (diskSpaceProbe.TryGetDiskSpace(path, out var totalBytes, out var freeBytes))
            {
                return BuildDiskInfo(label, path, totalBytes, freeBytes);
            }

            return new DiskStorageInfo { Label = label, Path = path, Status = "unavailable" };
        }

        private static DiskStorageInfo BuildDiskInfo(string label, string path, long totalBytes, long freeBytes)
        {
            var usedBytes = Math.Clamp(totalBytes - freeBytes, 0, totalBytes);
            var usedPercentage = totalBytes > 0 ? Math.Clamp((double)usedBytes / totalBytes * 100, 0, 100) : 0;

            return new DiskStorageInfo
            {
                Label = label,
                Path = path,
                UsedBytes = usedBytes,
                TotalBytes = totalBytes,
                FreeBytes = freeBytes,
                UsedPercentage = Math.Round(usedPercentage, 2),
                UsedFormatted = SystemFormatters.FormatBytes(usedBytes),
                TotalFormatted = SystemFormatters.FormatBytes(totalBytes),
                FreeFormatted = SystemFormatters.FormatBytes(freeBytes),
                Status = "available"
            };
        }
    }
}
