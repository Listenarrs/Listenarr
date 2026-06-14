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

namespace Listenarr.Infrastructure.Cache
{
    internal static class ImageCacheRefreshWorkflow
    {
        public static async Task<string?> RefreshWithBackupAsync(
            string destinationPath,
            string tempPath,
            Func<Task<string?>> downloadAsync,
            Func<string, string> getRelativePath)
        {
            string? backupPath = null;

            try
            {
                if (File.Exists(destinationPath))
                {
                    backupPath = destinationPath + ".bak";
                    File.Copy(destinationPath, backupPath, overwrite: true);
                    File.Delete(destinationPath);
                }

                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                var refreshed = await downloadAsync();
                if (string.IsNullOrWhiteSpace(refreshed) && !string.IsNullOrWhiteSpace(backupPath))
                {
                    File.Move(backupPath, destinationPath, overwrite: true);
                    return getRelativePath(destinationPath);
                }

                if (!string.IsNullOrWhiteSpace(backupPath) && File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }

                return null;
            }
            catch
            {
                if (!string.IsNullOrWhiteSpace(backupPath) &&
                    File.Exists(backupPath) &&
                    !File.Exists(destinationPath))
                {
                    File.Move(backupPath, destinationPath, overwrite: true);
                }

                throw;
            }
        }
    }
}
