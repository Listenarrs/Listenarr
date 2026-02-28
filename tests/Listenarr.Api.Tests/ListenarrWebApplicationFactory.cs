using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace Listenarr.Api.Tests
{
    public class ListenarrWebApplicationFactory : WebApplicationFactory<Program>
    {
        private static readonly ConcurrentDictionary<string, byte> DbFilesToCleanup = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object CleanupSync = new();
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
                    _sqliteDbRootDir = Path.Combine(Path.GetTempPath(), "listenarr-tests", $"host-{Guid.NewGuid():N}");
                    Directory.CreateDirectory(_sqliteDbRootDir);
                    _sqliteDbPath = Path.Combine(_sqliteDbRootDir, "listenarr.db");
                    RegisterDbPathForCleanup(_sqliteDbPath);
                }

                var overrides = new Dictionary<string, string?>
                {
                    ["Listenarr:SqliteDbPath"] = _sqliteDbPath,
                    ["Playwright:Enabled"] = "false",
                    ["Listenarr:DisableHostedServices"] = "true"
                };

                config.AddInMemoryCollection(overrides);
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
                    var mockPath = Path.Combine(Path.GetTempPath(), "mock-ffprobe");
                    ffmpegMock.Setup(f => f.GetFfprobePathAsync(It.IsAny<bool>()))
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

            if (disposing)
            {
                CleanupInstanceDbFiles();
            }

            _disposed = true;
            base.Dispose(disposing);
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
