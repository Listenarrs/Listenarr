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
using Listenarr.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

namespace Listenarr.Api.Controllers
{
    [ApiController]
    [Route("api/v{version:apiVersion}/system")]
    [Tags("System")]
    public class SystemController : ControllerBase
    {
        private readonly ISystemService _systemService;
        private readonly ILogger<SystemController> _logger;

        public SystemController(ISystemService systemService, ILogger<SystemController> logger)
        {
            _systemService = systemService;
            _logger = logger;
        }

        /// <summary>
        /// Get current system information including OS, runtime, memory, and CPU usage.
        /// </summary>
        [HttpGet("info")]
        public ActionResult<SystemInfo> GetSystemInfo()
        {
            try
            {
                var cfg = _systemService.GetStartupConfig();
                var authEnabled = cfg?.AuthenticationRequired?.ToLowerInvariant() is "true" or "yes" or "1";
                if (authEnabled && !(User?.Identity?.IsAuthenticated ?? false))
                {
                    return Unauthorized();
                }
                var systemInfo = _systemService.GetSystemInfo();
                // Optionally redact sensitive fields here if needed
                return Ok(systemInfo);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error retrieving system info");
                return StatusCode(500, new { error = "Failed to retrieve system information" });
            }
        }

        /// <summary>
        /// Get storage information for the application's data directory.
        /// </summary>
        [HttpGet("storage")]
        public ActionResult<StorageInfo> GetStorageInfo()
        {
            try
            {
                var cfg = _systemService.GetStartupConfig();
                var authEnabled = cfg?.AuthenticationRequired?.ToLowerInvariant() is "true" or "yes" or "1";
                if (authEnabled && !(User?.Identity?.IsAuthenticated ?? false))
                {
                    return Unauthorized();
                }
                var storageInfo = _systemService.GetStorageInfo();
                return Ok(storageInfo);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
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
                var cfg = _systemService.GetStartupConfig();
                var authEnabled = cfg?.AuthenticationRequired?.ToLowerInvariant() is "true" or "yes" or "1";
                if (authEnabled && !(User?.Identity?.IsAuthenticated ?? false))
                {
                    return Unauthorized();
                }
                var serviceHealth = await _systemService.GetServiceHealthAsync();
                return Ok(serviceHealth);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
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
                var cfg = _systemService.GetStartupConfig();
                var authEnabled = cfg?.AuthenticationRequired?.ToLowerInvariant() is "true" or "yes" or "1";
                if (authEnabled && !(User?.Identity?.IsAuthenticated ?? false))
                {
                    return Unauthorized();
                }
                var logs = _systemService.GetRecentLogs(limit);
                // Optionally redact sensitive log entries here if needed
                return Ok(logs);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
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
                var cfg = _systemService.GetStartupConfig();
                var authEnabled = cfg?.AuthenticationRequired?.ToLowerInvariant() is "true" or "yes" or "1";
                if (authEnabled && !(User?.Identity?.IsAuthenticated ?? false))
                {
                    return Unauthorized();
                }
                _logger.LogInformation("Test Info log generated from API");
                _logger.LogWarning("Test Warning log generated from API");
                _logger.LogError("Test Error log generated from API");
                return Ok(new { message = "Test logs generated successfully" });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
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
                var cfg = _systemService.GetStartupConfig();
                var authEnabled = cfg?.AuthenticationRequired?.ToLowerInvariant() is "true" or "yes" or "1";
                if (authEnabled && !(User?.Identity?.IsAuthenticated ?? false))
                {
                    return Unauthorized();
                }
                var logFilePath = _systemService.GetLogFilePath();

                // If log file exists, return it
                if (System.IO.File.Exists(logFilePath))
                {
                    var fileBytes = System.IO.File.ReadAllBytes(logFilePath);
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
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error downloading logs");
                return StatusCode(500, new { error = "Failed to download logs" });
            }
        }
    }
}

