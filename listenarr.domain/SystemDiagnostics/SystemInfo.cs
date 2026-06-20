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

namespace Listenarr.Domain.SystemDiagnostics
{
    public class SystemInfo
    {
        public string Version { get; set; } = string.Empty;
        public string OperatingSystem { get; set; } = string.Empty;
        public string Runtime { get; set; } = string.Empty;
        public string Uptime { get; set; } = string.Empty;
        public MemoryInfo Memory { get; set; } = new();
        public CpuInfo Cpu { get; set; } = new();
        public DateTime StartTime { get; set; }
    }

    public class MemoryInfo
    {
        public long UsedBytes { get; set; }
        public long TotalBytes { get; set; }
        public long FreeBytes { get; set; }
        public double UsedPercentage { get; set; }
        public string UsedFormatted { get; set; } = string.Empty;
        public string TotalFormatted { get; set; } = string.Empty;
        public string FreeFormatted { get; set; } = string.Empty;
    }

    public class CpuInfo
    {
        public double UsagePercentage { get; set; }
        public int ProcessorCount { get; set; }
    }

    /// <summary>
    /// Storage information returned by <c>GET /system/storage</c>.
    /// </summary>
    /// <remarks>
    /// The top-level <c>UsedBytes</c>/<c>TotalBytes</c>/<c>FreeBytes</c>/<c>UsedPercentage</c>/
    /// <c>*Formatted</c>/<c>DriveName</c>/<c>Status</c> fields are retained for backwards
    /// compatibility and describe the <b>App Data disk</b> (the config volume holding the
    /// database, logs, and cache). Per-disk detail — the System disk (container root), the
    /// App Data disk, and one entry per configured root folder — is in <see cref="Disks"/>.
    /// </remarks>
    public class StorageInfo
    {
        /// <summary>Used bytes on the App Data disk (legacy; see <see cref="Disks"/> for per-disk detail).</summary>
        public long UsedBytes { get; set; }
        /// <summary>Total bytes on the App Data disk (legacy; see <see cref="Disks"/> for per-disk detail).</summary>
        public long TotalBytes { get; set; }
        /// <summary>Free bytes on the App Data disk (legacy; see <see cref="Disks"/> for per-disk detail).</summary>
        public long FreeBytes { get; set; }
        /// <summary>Used percentage of the App Data disk (legacy; see <see cref="Disks"/> for per-disk detail).</summary>
        public double UsedPercentage { get; set; }
        public string UsedFormatted { get; set; } = string.Empty;
        public string TotalFormatted { get; set; } = string.Empty;
        public string FreeFormatted { get; set; } = string.Empty;
        /// <summary>Path of the App Data disk that the legacy fields describe (legacy; see <see cref="Disks"/>).</summary>
        public string DriveName { get; set; } = string.Empty;
        public string Status { get; set; } = "available";
        /// <summary>Per-disk storage: System (container root), App Data, then one entry per configured root folder.</summary>
        public List<DiskStorageInfo> Disks { get; set; } = new();
    }

    public class DiskStorageInfo
    {
        public string Label { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public long UsedBytes { get; set; }
        public long TotalBytes { get; set; }
        public long FreeBytes { get; set; }
        public double UsedPercentage { get; set; }
        public string UsedFormatted { get; set; } = string.Empty;
        public string TotalFormatted { get; set; } = string.Empty;
        public string FreeFormatted { get; set; } = string.Empty;
        public string Status { get; set; } = "available";
    }

    public class ServiceHealth
    {
        public string Status { get; set; } = "unknown"; // healthy, warning, error, unknown
        public string Version { get; set; } = string.Empty;
        public string Uptime { get; set; } = string.Empty;
        public DownloadClientHealth DownloadClients { get; set; } = new();
        public ExternalApiHealth ExternalApis { get; set; } = new();
    }

    public class DownloadClientHealth
    {
        public string Status { get; set; } = "unknown";
        public int Connected { get; set; }
        public int Total { get; set; }
        public List<ClientStatus> Clients { get; set; } = new();
    }

    public class ExternalApiHealth
    {
        public string Status { get; set; } = "unknown";
        public int Connected { get; set; }
        public int Total { get; set; }
        public List<ApiStatus> Apis { get; set; } = new();
    }

    public class ClientStatus
    {
        public string Name { get; set; } = string.Empty;
        public string Status { get; set; } = "unknown"; // connected, disconnected, unknown
        public string? Type { get; set; }
    }

    public class ApiStatus
    {
        public string Name { get; set; } = string.Empty;
        public string Status { get; set; } = "unknown"; // connected, disconnected, unknown
        public bool Enabled { get; set; }
    }

    public class LogEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public DateTime Timestamp { get; set; }
        public string Level { get; set; } = string.Empty; // Info, Warning, Error, Debug
        public string Message { get; set; } = string.Empty;
        public string? Exception { get; set; }
        public string? Source { get; set; }
    }
}

