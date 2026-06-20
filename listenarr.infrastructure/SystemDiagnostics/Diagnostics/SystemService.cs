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

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.SystemDiagnostics.Diagnostics
{
    public class SystemService : ISystemService
    {
        private readonly IConfigurationService _configurationService;
        private readonly ILogger<SystemService> _logger;
        private readonly IApplicationPathService _applicationPathService;
        private readonly IApplicationVersionService _applicationVersionService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IDiskSpaceProbe _diskSpaceProbe;
        private readonly DateTime _startTime;
        private static readonly Process _currentProcess = Process.GetCurrentProcess();

        public SystemService(
            IConfigurationService configurationService,
            ILogger<SystemService> logger,
            IApplicationPathService applicationPathService,
            IApplicationVersionService applicationVersionService,
            IRootFolderService rootFolderService,
            IDiskSpaceProbe diskSpaceProbe)
        {
            _configurationService = configurationService;
            _logger = logger;
            _applicationPathService = applicationPathService;
            _applicationVersionService = applicationVersionService;
            _rootFolderService = rootFolderService;
            _diskSpaceProbe = diskSpaceProbe;
            _startTime = DateTime.UtcNow;
        }

        public StartupConfig GetStartupConfig()
        {
            try
            {
                return _configurationService.GetStartupConfigAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error retrieving startup configuration");
                return new StartupConfig();
            }
        }

        public SystemInfo GetSystemInfo()
        {
            try
            {
                var version = _applicationVersionService.Resolve();

                var uptime = DateTime.UtcNow - _startTime;
                var uptimeFormatted = SystemFormatters.FormatUptime(uptime);

                var memoryInfo = GetMemoryInfo();
                var cpuInfo = GetCpuInfo();

                return new SystemInfo
                {
                    Version = version,
                    OperatingSystem = GetOperatingSystemInfo(),
                    Runtime = GetRuntimeInfo(),
                    Uptime = uptimeFormatted,
                    Memory = memoryInfo,
                    Cpu = cpuInfo,
                    StartTime = _startTime
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error getting system info");
                throw;
            }
        }

        public async Task<StorageInfo> GetStorageInfoAsync()
        {
            try
            {
                // Compute the App Data disk first so the legacy top-level fields can mirror
                // it (existing consumers of /system/storage keep working unchanged); the
                // Disks list itself is ordered System -> App Data -> root folders below.
                // Prefer the config root (database/logs/cache — the mounted volume in
                // Docker) over the install dir, falling back when it has not been created yet.
                var appDataPath = Directory.Exists(_applicationPathService.ConfigRootPath)
                    ? _applicationPathService.ConfigRootPath
                    : _applicationPathService.ContentRootPath;
                var appDisk = SystemStorageMapper.MeasureDisk(_diskSpaceProbe, "App Data", appDataPath);

                var storageInfo = new StorageInfo
                {
                    UsedBytes = appDisk.UsedBytes,
                    TotalBytes = appDisk.TotalBytes,
                    FreeBytes = appDisk.FreeBytes,
                    UsedPercentage = appDisk.UsedPercentage,
                    UsedFormatted = appDisk.UsedFormatted,
                    TotalFormatted = appDisk.TotalFormatted,
                    FreeFormatted = appDisk.FreeFormatted,
                    DriveName = appDisk.Path,
                    Status = appDisk.Status
                };
                // System disk first: the filesystem hosting the application install itself —
                // in Docker this is the container root (e.g. docker.img on Unraid),
                // which is worth watching independently of the config volume.
                var systemRoot = Path.GetPathRoot(_applicationPathService.ContentRootPath);
                if (!string.IsNullOrEmpty(systemRoot))
                {
                    storageInfo.Disks.Add(SystemStorageMapper.MeasureDisk(_diskSpaceProbe, "System", systemRoot));
                }

                storageInfo.Disks.Add(appDisk);

                List<RootFolder> rootFolders;
                try
                {
                    rootFolders = await _rootFolderService.GetAllAsync();
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Could not load root folders for storage info");
                    rootFolders = new List<RootFolder>();
                }

                foreach (var folder in rootFolders)
                {
                    var label = string.IsNullOrWhiteSpace(folder.Name) ? folder.Path : folder.Name;
                    storageInfo.Disks.Add(SystemStorageMapper.MeasureDisk(_diskSpaceProbe, label, folder.Path));
                }

                return storageInfo;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error getting storage info");
                throw;
            }
        }

        public async Task<ServiceHealth> GetServiceHealthAsync()
        {
            try
            {
                var version = _applicationVersionService.Resolve();
                var uptime = DateTime.UtcNow - _startTime;
                var uptimeFormatted = SystemFormatters.FormatUptime(uptime);

                // Get download client health
                var downloadClientHealth = await GetDownloadClientHealthAsync();

                // Get external API health
                var externalApiHealth = await GetExternalApiHealthAsync();

                return SystemHealthMapper.BuildServiceHealth(version, uptimeFormatted, downloadClientHealth, externalApiHealth);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error getting service health");
                throw;
            }
        }

        private async Task<DownloadClientHealth> GetDownloadClientHealthAsync()
        {
            try
            {
                var clients = await _configurationService.GetDownloadClientConfigurationsAsync();
                return SystemHealthMapper.BuildDownloadClientHealth(clients);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error getting download client health");
                return SystemHealthMapper.BuildDownloadClientHealthError();
            }
        }

        private async Task<ExternalApiHealth> GetExternalApiHealthAsync()
        {
            try
            {
                var apis = await _configurationService.GetApiConfigurationsAsync();
                return SystemHealthMapper.BuildExternalApiHealth(apis);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error getting external API health");
                return SystemHealthMapper.BuildExternalApiHealthError();
            }
        }

        private MemoryInfo GetMemoryInfo()
        {
            try
            {
                _currentProcess.Refresh();
                var usedBytes = _currentProcess.WorkingSet64;

                // Get total system memory
                var totalBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                var freeBytes = totalBytes - usedBytes;
                var usedPercentage = (double)usedBytes / totalBytes * 100;

                return new MemoryInfo
                {
                    UsedBytes = usedBytes,
                    TotalBytes = totalBytes,
                    FreeBytes = freeBytes,
                    UsedPercentage = Math.Round(usedPercentage, 2),
                    UsedFormatted = SystemFormatters.FormatBytes(usedBytes),
                    TotalFormatted = SystemFormatters.FormatBytes(totalBytes),
                    FreeFormatted = SystemFormatters.FormatBytes(freeBytes)
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error getting memory info");
                return new MemoryInfo();
            }
        }

        private CpuInfo GetCpuInfo()
        {
            try
            {
                _currentProcess.Refresh();
                var cpuUsage = _currentProcess.TotalProcessorTime.TotalMilliseconds /
                              (DateTime.UtcNow - _currentProcess.StartTime).TotalMilliseconds * 100;

                return new CpuInfo
                {
                    UsagePercentage = Math.Round(Math.Min(cpuUsage, 100), 2),
                    ProcessorCount = Environment.ProcessorCount
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error getting CPU info");
                return new CpuInfo
                {
                    ProcessorCount = Environment.ProcessorCount
                };
            }
        }

        private string GetOperatingSystemInfo()
        {
            var os = Environment.OSVersion;
            var architecture = RuntimeInformation.OSArchitecture.ToString();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return $"Windows {os.Version} ({architecture})";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return $"Linux {os.Version} ({architecture})";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return $"macOS {os.Version} ({architecture})";
            }
            else
            {
                return $"{os.Platform} {os.Version} ({architecture})";
            }
        }

        private string GetRuntimeInfo()
        {
            var framework = RuntimeInformation.FrameworkDescription;
            return framework;
        }

        public List<LogEntry> GetRecentLogs(int limit = 100)
        {
            var logs = new List<LogEntry>();

            try
            {
                var logFilePath = GetLogFilePath();

                if (!File.Exists(logFilePath))
                {
                    // Return some sample logs if file doesn't exist yet
                    logs.Add(new LogEntry
                    {
                        Timestamp = DateTime.UtcNow.AddMinutes(-5),
                        Level = "Info",
                        Message = "Listenarr application started",
                        Source = "Application"
                    });
                    logs.Add(new LogEntry
                    {
                        Timestamp = DateTime.UtcNow.AddMinutes(-3),
                        Level = "Info",
                        Message = "Database connection established",
                        Source = "Database"
                    });
                    logs.Add(new LogEntry
                    {
                        Timestamp = DateTime.UtcNow.AddMinutes(-2),
                        Level = "Info",
                        Message = "System health check completed successfully",
                        Source = "System"
                    });
                    logs.Add(new LogEntry
                    {
                        Timestamp = DateTime.UtcNow.AddMinutes(-1),
                        Level = "Info",
                        Message = "Ready to accept requests",
                        Source = "Application"
                    });
                    return logs;
                }

                // Read the last N lines from the log file with shared read access
                List<string> lines;
                using (var fileStream = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(fileStream))
                {
                    var allLines = new List<string>();
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        allLines.Add(line);
                    }

                    // Take the last N lines
                    lines = allLines.TakeLast(limit).ToList();
                }

                logs.AddRange(lines.Select(SystemLogParser.ParseLogLine).Where(logEntry => logEntry != null)!);

                // If no logs were parsed, return sample logs
                if (logs.Count == 0)
                {
                    logs.Add(new LogEntry
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Info",
                        Message = "Log file exists but contains no parseable entries",
                        Source = "System"
                    });
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error reading log file");
                logs.Add(new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Error",
                    Message = $"Failed to read log file: {ex.Message}",
                    Source = "System"
                });
            }

            return logs;
        }

        public string GetLogFilePath()
        {
            // Use the host content root so local development lands in
            // listenarr.api/config/logs and production stays under the deployed root.
            var logsDir = _applicationPathService.LogsRootPath;

            // Ensure the directory exists
            if (!Directory.Exists(logsDir))
            {
                Directory.CreateDirectory(logsDir);
            }

            // Use today's date for the log file name (Serilog format with RollingInterval.Day)
            // Serilog will create files like: listenarr-20251105.log
            var logFileName = $"listenarr-{DateTime.UtcNow:yyyyMMdd}.log";
            var todayLogPath = Path.Join(logsDir, logFileName);

            // If today's log doesn't exist yet, find the most recent log file
            if (!File.Exists(todayLogPath))
            {
                var logFiles = Directory.GetFiles(logsDir, "listenarr-*.log")
                    .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                    .ToList();

                return logFiles.FirstOrDefault() ?? todayLogPath;
            }

            return todayLogPath;
        }

    }
}
