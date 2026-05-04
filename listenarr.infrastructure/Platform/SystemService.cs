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
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Platform
{
    public class SystemService : ISystemService
    {
        private readonly IConfigurationService _configurationService;
        private readonly ILogger<SystemService> _logger;
        private readonly DateTime _startTime;
        private static readonly Process _currentProcess = Process.GetCurrentProcess();

        public SystemService(IConfigurationService configurationService, ILogger<SystemService> logger)
        {
            _configurationService = configurationService;
            _logger = logger;
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
                var assembly = Assembly.GetExecutingAssembly();
                var version = assembly.GetName().Version?.ToString() ?? "1.0.0";

                var uptime = DateTime.UtcNow - _startTime;
                var uptimeFormatted = FormatUptime(uptime);

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

        public StorageInfo GetStorageInfo()
        {
            try
            {
                // Get the drive where the application is running
                var appPath = AppDomain.CurrentDomain.BaseDirectory;
                var driveInfo = new DriveInfo(Path.GetPathRoot(appPath) ?? "C:\\");

                if (!driveInfo.IsReady)
                {
                    return new StorageInfo
                    {
                        Status = "unavailable",
                        DriveName = driveInfo.Name
                    };
                }

                var totalBytes = driveInfo.TotalSize;
                var freeBytes = driveInfo.AvailableFreeSpace;
                var usedBytes = totalBytes - freeBytes;
                var usedPercentage = (double)usedBytes / totalBytes * 100;

                return new StorageInfo
                {
                    UsedBytes = usedBytes,
                    TotalBytes = totalBytes,
                    FreeBytes = freeBytes,
                    UsedPercentage = Math.Round(usedPercentage, 2),
                    UsedFormatted = FormatBytes(usedBytes),
                    TotalFormatted = FormatBytes(totalBytes),
                    FreeFormatted = FormatBytes(freeBytes),
                    DriveName = driveInfo.Name
                };
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
                var assembly = Assembly.GetExecutingAssembly();
                var version = assembly.GetName().Version?.ToString() ?? "1.0.0";
                var uptime = DateTime.UtcNow - _startTime;
                var uptimeFormatted = FormatUptime(uptime);

                // Get download client health
                var downloadClientHealth = await GetDownloadClientHealthAsync();

                // Get external API health
                var externalApiHealth = await GetExternalApiHealthAsync();

                // Determine overall status
                var overallStatus = "healthy";
                if (downloadClientHealth.Status == "error" || externalApiHealth.Status == "error")
                {
                    overallStatus = "error";
                }
                else if (downloadClientHealth.Status == "warning" || externalApiHealth.Status == "warning")
                {
                    overallStatus = "warning";
                }

                return new ServiceHealth
                {
                    Status = overallStatus,
                    Version = version,
                    Uptime = uptimeFormatted,
                    DownloadClients = downloadClientHealth,
                    ExternalApis = externalApiHealth
                };
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
                var clientStatuses = new List<ClientStatus>();
                var connectedCount = 0;

                foreach (var client in clients)
                {
                    if (!client.IsEnabled)
                    {
                        continue;
                    }

                    // TODO: Implement actual connection testing for each client type
                    // For now, assume enabled clients are connected
                    var status = "connected";
                    connectedCount++;

                    clientStatuses.Add(new ClientStatus
                    {
                        Name = client.Name,
                        Status = status,
                        Type = client.Type
                    });
                }

                var totalEnabled = clients.Count(c => c.IsEnabled);
                var overallStatus = "healthy";
                if (connectedCount == 0 && totalEnabled > 0)
                {
                    overallStatus = "error";
                }
                else if (connectedCount < totalEnabled)
                {
                    overallStatus = "warning";
                }

                return new DownloadClientHealth
                {
                    Status = overallStatus,
                    Connected = connectedCount,
                    Total = totalEnabled,
                    Clients = clientStatuses
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error getting download client health");
                return new DownloadClientHealth
                {
                    Status = "error",
                    Connected = 0,
                    Total = 0,
                    Clients = new List<ClientStatus>()
                };
            }
        }

        private async Task<ExternalApiHealth> GetExternalApiHealthAsync()
        {
            try
            {
                var apis = await _configurationService.GetApiConfigurationsAsync();
                var apiStatuses = new List<ApiStatus>();
                var connectedCount = 0;

                foreach (var api in apis)
                {
                    if (!api.IsEnabled)
                    {
                        continue;
                    }

                    // TODO: Implement actual connection testing for each API
                    // For now, assume enabled APIs are connected
                    var status = "connected";
                    connectedCount++;

                    apiStatuses.Add(new ApiStatus
                    {
                        Name = api.Name,
                        Status = status,
                        Enabled = api.IsEnabled
                    });
                }

                var totalEnabled = apis.Count(c => c.IsEnabled);
                var overallStatus = "healthy";
                if (connectedCount == 0 && totalEnabled > 0)
                {
                    overallStatus = "error";
                }
                else if (connectedCount < totalEnabled)
                {
                    overallStatus = "warning";
                }

                return new ExternalApiHealth
                {
                    Status = overallStatus,
                    Connected = connectedCount,
                    Total = totalEnabled,
                    Apis = apiStatuses
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error getting external API health");
                return new ExternalApiHealth
                {
                    Status = "error",
                    Connected = 0,
                    Total = 0,
                    Apis = new List<ApiStatus>()
                };
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
                    UsedFormatted = FormatBytes(usedBytes),
                    TotalFormatted = FormatBytes(totalBytes),
                    FreeFormatted = FormatBytes(freeBytes)
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

        private string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;

            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }

            return $"{len:0.##} {sizes[order]}";
        }

        private string FormatUptime(TimeSpan uptime)
        {
            if (uptime.TotalDays >= 1)
            {
                return $"{(int)uptime.TotalDays} days, {uptime.Hours} hours";
            }
            else if (uptime.TotalHours >= 1)
            {
                return $"{(int)uptime.TotalHours} hours, {uptime.Minutes} minutes";
            }
            else if (uptime.TotalMinutes >= 1)
            {
                return $"{(int)uptime.TotalMinutes} minutes";
            }
            else
            {
                return $"{(int)uptime.TotalSeconds} seconds";
            }
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

                logs.AddRange(lines.Select(ParseLogLine).Where(logEntry => logEntry != null)!);

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
            // Get the logs directory from the application base path
            var logsDir = Path.Join(Directory.GetCurrentDirectory(), "config", "logs");

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

        private LogEntry? ParseLogLine(string line)
        {
            try
            {
                // Expected Serilog format: 2025-11-05 11:43:58.516 -05:00 [INF] Message here

                if (string.IsNullOrWhiteSpace(line))
                    return null;

                // Try to parse Serilog format with regex
                // Format: YYYY-MM-DD HH:MM:SS.FFF ZZZ [LEVEL] Message
                var match = Regex.Match(
                    line,
                    @"^(\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d{3}\s+[+-]\d{2}:\d{2})\s+\[(\w{3})\]\s+(.+)$"
                );

                if (match.Success)
                {
                    var timestampStr = match.Groups[1].Value;
                    var level = match.Groups[2].Value.ToUpperInvariant();
                    var message = match.Groups[3].Value;

                    // Parse timestamp
                    DateTime timestamp;
                    if (!DateTime.TryParse(timestampStr, out timestamp))
                    {
                        timestamp = DateTime.UtcNow;
                    }

                    // Map Serilog log levels
                    var mappedLevel = level switch
                    {
                        "VRB" => "Debug",  // Verbose
                        "DBG" => "Debug",  // Debug
                        "INF" => "Info",   // Information
                        "WRN" => "Warning", // Warning
                        "ERR" => "Error",  // Error
                        "FTL" => "Error",  // Fatal
                        _ => "Info"
                    };

                    return new LogEntry
                    {
                        Timestamp = timestamp,
                        Level = mappedLevel,
                        Message = message,
                        Source = "Application"
                    };
                }

                // Fallback: treat the whole line as info message
                return new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Info",
                    Message = line,
                    Source = "Application"
                };
            }
            catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException)
            {
                return null;
            }
        }
    }
}



