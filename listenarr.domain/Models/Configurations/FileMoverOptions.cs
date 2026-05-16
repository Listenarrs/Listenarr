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
namespace Listenarr.Domain.Models.Configurations
{
    public class FileMoverOptions
    {
        // Enable or disable using robocopy as a fallback on Windows
        public bool EnableRobocopy { get; set; } = true;

        // Timeout for robocopy/process runner calls in milliseconds
        public int RobocopyTimeoutMs { get; set; } = 60000;

        // Retry configuration for move attempts (number of attempts)
        public int MaxRetries { get; set; } = 4;

        // Backoff (ms) initial and maximum
        public int MinBackoffMs { get; set; } = 1000;
        public int MaxBackoffMs { get; set; } = 8000;
    }
}
