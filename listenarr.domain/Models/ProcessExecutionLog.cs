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
namespace Listenarr.Domain.Models
{
    public class ProcessExecutionLog
    {
        public int Id { get; set; }
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

        // Optional source tag to identify where the process was launched from (e.g. "PlaywrightInstall", "FfmpegInstaller")
        public string? Source { get; set; }

        // Executable/file and arguments
        public string? FileName { get; set; }
        public string? Arguments { get; set; }

        // Result
        public int? ExitCode { get; set; }
        public bool TimedOut { get; set; }
        public string? Stdout { get; set; }
        public string? Stderr { get; set; }

        // Duration in milliseconds (optional)
        public int? DurationMs { get; set; }
    }
}

