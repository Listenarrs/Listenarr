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


namespace Listenarr.Domain.Downloads
{
    public class ImportResult
    {
        public bool Success { get; set; }
        public string? SourcePath { get; set; }
        public string? FinalPath { get; set; }
        public string? Message { get; set; }
        public FileAction Action { get; set; }
        public bool WasRegisteredToAudiobook { get; set; }
        public DateTime? Timestamp { get; set; } = DateTime.UtcNow;

        public override string ToString()
        {
            return $"Success:{Success}, Action:{Action}, Message: {Message}, Destination:{FinalPath}";
        }

        public static ImportResult ImportSuccess(FileAction action, string sourcePath, string finalPath, bool wasRegisteredToAudiobook = false)
        {
            return new ImportResult
            {
                Success = true,
                Action = action,
                SourcePath = sourcePath,
                FinalPath = finalPath,
                WasRegisteredToAudiobook = wasRegisteredToAudiobook
            };
        }

        public static ImportResult ImportFailure(FileAction action, string sourcePath, string finalPath)
        {
            return new ImportResult
            {
                Success = false,
                Action = action,
                SourcePath = sourcePath,
                FinalPath = finalPath,
                Message = $"Unable to perform {action} on {sourcePath} to {finalPath}"
            };
        }

        public static ImportResult Exception(Exception exception, string sourcePath = "")
        {
            return new ImportResult
            {
                Success = false,
                Message = exception.Message,
                SourcePath = sourcePath
            };
        }

        public static ImportResult Skipped(string message)
        {
            return new ImportResult
            {
                Success = true,
                Message = message
            };
        }
    }
}
