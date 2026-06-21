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

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.SystemDiagnostics
{
    [ApiController]
    [Route("api/v{version:apiVersion}/system")]
    [Tags("System")]
    public class SystemController : ControllerBase
    {
        private readonly ISystemService _systemService;
        private readonly ISystemReadinessService _readinessService;
        private readonly ILogger<SystemController> _logger;
        private readonly IFileSystem _fileSystem;

        public SystemController(
            ISystemService systemService,
            ISystemReadinessService readinessService,
            ILogger<SystemController> logger,
            IFileSystem fileSystem)
        {
            _systemService = systemService;
            _readinessService = readinessService;
            _logger = logger;
            _fileSystem = fileSystem;
        }

        /// <summary>
        /// Lightweight readiness probe for local tooling and reverse proxies.
        /// </summary>
        [AllowAnonymous]
        [HttpGet("ready")]
        public async Task<IActionResult> GetReady(CancellationToken cancellationToken)
        {
            var readiness = await _readinessService.CheckAsync(cancellationToken);
            return readiness.IsReady
                ? Ok(readiness)
                : StatusCode(StatusCodes.Status503ServiceUnavailable, readiness);
        }

        /// <summary>
        /// Get current system information including OS, runtime, memory, and CPU usage.
        /// </summary>
        [HttpGet("info")]
        public ActionResult<SystemInfo> GetSystemInfo()
        {
            try
            {
                var systemInfo = _systemService.GetSystemInfo();
                // Optionally redact sensitive fields here if needed
                return Ok(systemInfo);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error retrieving system info");
                return StatusCode(500, new { error = "Failed to retrieve system information" });
            }
        }

        /// <summary>
        /// Get storage information for the application's data directory and all configured root folders.
        /// </summary>
        [HttpGet("storage")]
        public async Task<ActionResult<StorageInfo>> GetStorageInfo()
        {
            try
            {
                var storageInfo = await _systemService.GetStorageInfoAsync();
                return Ok(storageInfo);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error retrieving storage info");
                return StatusCode(500, new { error = "Failed to retrieve storage information" });
            }
        }

        /// <summary>
        /// Get health status of all services including download clients and external APIs.
        /// </summary>
        [HttpGet("health")]
        public async Task<ActionResult<ServiceHealth>> GetServiceHealth()
        {
            try
            {
                var serviceHealth = await _systemService.GetServiceHealthAsync();
                return Ok(serviceHealth);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error retrieving service health");
                return StatusCode(500, new { error = "Failed to retrieve service health" });
            }
        }

        /// <summary>
        /// Get recent log entries.
        /// </summary>
        /// <param name="limit">Maximum number of log entries to return (default 100).</param>
        [HttpGet("logs")]
        public ActionResult<List<LogEntry>> GetLogs([FromQuery] int limit = 100)
        {
            try
            {
                var logs = _systemService.GetRecentLogs(limit);
                // Optionally redact sensitive log entries here if needed
                return Ok(logs);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error retrieving logs");
                return StatusCode(500, new { error = "Failed to retrieve logs" });
            }
        }

        /// <summary>
        /// Generate test log messages at Info, Warning, and Error levels for debugging log broadcasting.
        /// </summary>
        [HttpPost("logs/test")]
        public ActionResult TestLogs()
        {
            try
            {
                _logger.LogInformation("Test Info log generated from API");
                _logger.LogWarning("Test Warning log generated from API");
                _logger.LogError("Test Error log generated from API");
                return Ok(new { message = "Test logs generated successfully" });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error generating test logs");
                return StatusCode(500, new { error = "Failed to generate test logs" });
            }
        }

        /// <summary>
        /// Download the current log file as a text file attachment.
        /// </summary>
        [HttpGet("logs/download")]
        public ActionResult DownloadLogs()
        {
            try
            {
                var logFilePath = _systemService.GetLogFilePath();

                // If log file exists, return it
                if (_fileSystem.FileExists(logFilePath))
                {
                    var fileBytes = _fileSystem.ReadAllBytes(logFilePath);
                    var fileName = $"listenarr-logs-{DateTime.UtcNow:yyyy-MM-dd-HHmmss}.log";
                    return File(fileBytes, "text/plain", fileName);
                }

                // If no log file exists, generate one from current logs
                var logs = _systemService.GetRecentLogs(1000); // Get up to 1000 logs
                var logContent = new System.Text.StringBuilder();

                logContent.AppendLine($"Listenarr Log Export - {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
                logContent.AppendLine("==========================================");
                logContent.AppendLine();

                foreach (var log in logs)
                {
                    var timestamp = log.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
                    logContent.AppendLine($"[{timestamp}] [{log.Level}] {log.Message}");
                    if (!string.IsNullOrEmpty(log.Source))
                    {
                        logContent.AppendLine($"  Source: {log.Source}");
                    }
                    if (!string.IsNullOrEmpty(log.Exception))
                    {
                        logContent.AppendLine($"  Exception: {log.Exception}");
                    }
                    logContent.AppendLine();
                }

                var generatedBytes = System.Text.Encoding.UTF8.GetBytes(logContent.ToString());
                var generatedFileName = $"listenarr-logs-{DateTime.UtcNow:yyyy-MM-dd-HHmmss}.log";

                return File(generatedBytes, "text/plain", generatedFileName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error downloading logs");
                return StatusCode(500, new { error = "Failed to download logs" });
            }
        }
    }
}
