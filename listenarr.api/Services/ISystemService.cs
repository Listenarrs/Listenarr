/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2025 Robbie Davis
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

using Listenarr.Domain.Models;

namespace Listenarr.Api.Services
{
    public interface ISystemService
    {
        /// <summary>
        /// Get current system information including OS, runtime, memory, CPU usage
        /// </summary>
        SystemInfo GetSystemInfo();

        /// <summary>
        /// Get storage information for the application's data directory
        /// </summary>
        StorageInfo GetStorageInfo();

        /// <summary>
        /// Get health status of all services including download clients and external APIs
        /// </summary>
        Task<ServiceHealth> GetServiceHealthAsync();

        /// <summary>
        /// Get recent log entries from the log file
        /// </summary>
        List<LogEntry> GetRecentLogs(int limit = 100);

        /// <summary>
        /// Get the path to the current log file
        /// </summary>
        string GetLogFilePath();

        /// <summary>
        /// Get the startup configuration (for dynamic auth checks)
        /// </summary>
        StartupConfig GetStartupConfig();
    }
}

