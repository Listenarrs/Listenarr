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

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.SystemDiagnostics.Storage
{
    /// <summary>
    /// Measures disk space for a path, using the Windows native <c>GetDiskFreeSpaceEx</c>
    /// call (which handles drive paths, mounted-folder junctions, and UNC/NAS shares) and
    /// falling back to <see cref="DriveInfo"/> on Unix-like systems.
    /// </summary>
    public class DiskSpaceProbe : IDiskSpaceProbe
    {
        private readonly ILogger<DiskSpaceProbe> _logger;

        public DiskSpaceProbe(ILogger<DiskSpaceProbe> logger)
        {
            _logger = logger;
        }

        public bool TryGetDiskSpace(string path, out long totalBytes, out long freeBytes)
        {
            totalBytes = 0;
            freeBytes = 0;

            try
            {
                // Missing directories report the same on every platform; without this
                // check Windows would silently fall back to drive-root stats because
                // the DriveInfo constructor normalizes "C:\missing\dir" to "C:\".
                if (!Directory.Exists(path))
                {
                    return false;
                }

                if (OperatingSystem.IsWindows())
                {
                    // DriveInfo throws on UNC/NAS roots (\\server\share). GetDiskFreeSpaceEx
                    // accepts any directory — drive paths, mounted-folder junctions and UNC
                    // shares alike — and reports quota-aware free space for the caller.
                    if (!NativeMethods.GetDiskFreeSpaceEx(path, out var freeForCaller, out var total, out _))
                    {
                        _logger.LogWarning(
                            "GetDiskFreeSpaceEx failed for {Path} (error {Error})",
                            path, Marshal.GetLastWin32Error());
                        return false;
                    }

                    totalBytes = (long)total;
                    freeBytes = (long)freeForCaller;
                    return true;
                }

                // DriveInfo on the path itself (not Path.GetPathRoot): on Linux this
                // stats the filesystem containing the path, which is what makes Docker
                // volume mounts like /audiobooks report their own free space instead
                // of the container root's.
                var driveInfo = new DriveInfo(path);
                if (!driveInfo.IsReady)
                {
                    return false;
                }

                totalBytes = driveInfo.TotalSize;
                freeBytes = driveInfo.AvailableFreeSpace;
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Could not read disk info at {Path}", path);
                totalBytes = 0;
                freeBytes = 0;
                return false;
            }
        }

        private static class NativeMethods
        {
            // GetDiskFreeSpaceExW accepts a directory or UNC path and returns the free
            // bytes available to the caller plus the volume total. Used on Windows where
            // DriveInfo cannot handle UNC/NAS roots.
            [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, CharSet = CharSet.Unicode)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetDiskFreeSpaceEx(
                string lpDirectoryName,
                out ulong lpFreeBytesAvailableToCaller,
                out ulong lpTotalNumberOfBytes,
                out ulong lpTotalNumberOfFreeBytes);
        }
    }
}
