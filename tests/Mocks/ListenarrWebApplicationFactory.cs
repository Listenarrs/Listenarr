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
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Moq;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Listenarr.Tests.Mocks
{
    public class ListenarrWebApplicationFactory : WebApplicationFactory<Program>
    {
        private static readonly ConcurrentDictionary<string, byte> DbFilesToCleanup = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Lock CleanupSync = new();
        private static bool _isProcessExitCleanupHooked;

        private string? _sqliteDbPath;
        private string? _sqliteDbRootDir;
        private bool _disposed;

        public ListenarrWebApplicationFactory()
        {
            Environment.SetEnvironmentVariable("LISTENARR_TEST_MODE", "true");
            EnsureProcessExitCleanupHook();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Use "Test" environment (matches GitHub Actions ASPNETCORE_ENVIRONMENT=Test)
            builder.UseEnvironment("Test");

            builder.ConfigureAppConfiguration((context, config) =>
            {
                if (string.IsNullOrWhiteSpace(_sqliteDbPath))
                {
                    _sqliteDbRootDir = Path.Join(Path.GetTempPath(), "listenarr-tests", $"host-{Guid.NewGuid():N}");
                    Directory.CreateDirectory(_sqliteDbRootDir);
                    _sqliteDbPath = Path.Join(_sqliteDbRootDir, "listenarr.db");
                    RegisterDbPathForCleanup(_sqliteDbPath);
                }

                var overrides = new Dictionary<string, string?>
                {
                    ["Listenarr:SqliteDbPath"] = _sqliteDbPath,
                    ["Listenarr:DisableHostedServices"] = "true"
                };

                config.AddInMemoryCollection(overrides);
            });

            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
            });

            builder.ConfigureServices(services =>
            {
                // Keep API integration tests deterministic regardless of a local config.json
                // in the repo/environment. Individual tests can still override this with
                // WithWebHostBuilder(...ConfigureServices(...)).
                services.RemoveAll<IStartupConfigService>();
                services.AddSingleton<IStartupConfigService>(_ =>
                {
                    var mock = new Mock<IStartupConfigService>();
                    mock.Setup(s => s.GetConfig()).Returns(new StartupConfig { AuthenticationRequired = "false" });
                    mock.Setup(s => s.ReloadAsync()).Returns(Task.CompletedTask);
                    mock.Setup(s => s.SaveAsync(It.IsAny<StartupConfig>())).Returns(Task.CompletedTask);
                    return mock.Object;
                });

                services.RemoveAll<IFfmpegService>();
                services.AddSingleton<IFfmpegService>(_ =>
                {
                    var ffmpegMock = new Mock<IFfmpegService>();
                    var mockPath = Path.Join(Path.GetTempPath(), "mock-ffprobe");
                    ffmpegMock.Setup(f => f.GetFfprobePathAsync())
                        .ReturnsAsync(mockPath);
                    ffmpegMock.Setup(f => f.EnsureFfprobeInstalledAsync())
                        .ReturnsAsync(mockPath);
                    return ffmpegMock.Object;
                });
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;

            base.Dispose(disposing);

            if (disposing)
            {
                CleanupInstanceDbFiles();
            }

            _disposed = true;
        }

        private static void EnsureProcessExitCleanupHook()
        {
            lock (CleanupSync)
            {
                if (_isProcessExitCleanupHooked) return;
                AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupRegisteredDbFiles();
                _isProcessExitCleanupHooked = true;
            }
        }

        private static void RegisterDbPathForCleanup(string dbPath)
        {
            if (string.IsNullOrWhiteSpace(dbPath)) return;
            DbFilesToCleanup.TryAdd(dbPath, 0);
        }

        private static void CleanupRegisteredDbFiles()
        {
            foreach (var dbPath in DbFilesToCleanup.Keys)
            {
                TryDeleteDbWithCompanions(dbPath);
                TryDeleteParentDirectoryIfEmpty(Path.GetDirectoryName(dbPath));
            }
        }

        private void CleanupInstanceDbFiles()
        {
            if (!string.IsNullOrWhiteSpace(_sqliteDbPath))
            {
                TryDeleteDbWithCompanions(_sqliteDbPath);
            }

            if (!string.IsNullOrWhiteSpace(_sqliteDbRootDir))
            {
                try
                {
                    if (Directory.Exists(_sqliteDbRootDir))
                    {
                        Directory.Delete(_sqliteDbRootDir, recursive: true);
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    Debug.WriteLine($"Failed to delete test sqlite root directory '{_sqliteDbRootDir}': {ex.Message}");
                }
            }
        }

        private static void TryDeleteDbWithCompanions(string dbPath)
        {
            TryDeleteFile(dbPath);
            TryDeleteFile($"{dbPath}-wal");
            TryDeleteFile($"{dbPath}-shm");
        }

        private static void TryDeleteFile(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;

            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                Debug.WriteLine($"Failed to delete test sqlite file '{filePath}': {ex.Message}");
            }
        }

        private static void TryDeleteParentDirectoryIfEmpty(string? directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath)) return;

            try
            {
                if (Directory.Exists(directoryPath) && Directory.GetFileSystemEntries(directoryPath).Length == 0)
                {
                    Directory.Delete(directoryPath);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                Debug.WriteLine($"Failed to delete test sqlite directory '{directoryPath}': {ex.Message}");
            }
        }
    }
}
