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
using System.Text.Json;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Configuration.Core
{
    public class StartupConfigService : IStartupConfigService
    {
        private readonly ILogger<StartupConfigService> _logger;
        private readonly IFileSystem _fileSystem;
        private readonly string _configPath;
        private StartupConfig? _config;

        public StartupConfigService(ILogger<StartupConfigService> logger, IApplicationPathService applicationPathService, IFileSystem fileSystem)
        {
            _logger = logger;
            _fileSystem = fileSystem;

            // Determine config.json path. Prefer the repository copy at
            // <repoRoot>/listenarr.api/config/config.json for local development
            // so `npm run dev` uses the repo config file. Otherwise fall back to
            // content-root-based config (e.g., published/bin layouts).
            var contentRoot = applicationPathService.ContentRootPath ?? AppContext.BaseDirectory;

            // In Development, try to resolve the repository root from the current
            // working directory (most reliable when running via `npm run dev`).
            var isDev = string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase);
            var isDocker = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";
            if (isDev && !isDocker)
            {
                try
                {
                    // Search upward from the current working directory for listenarr.sln
                    var cwd = _fileSystem.CurrentDirectory;
                    string? repoRootFromCwd = null;
                    try
                    {
                        var dir = cwd;
                        const int maxDepth2 = 8;
                        int depth2 = 0;
                        while (!string.IsNullOrWhiteSpace(dir) && depth2++ < maxDepth2)
                        {
                            if (_fileSystem.FileExists(Path.Join(dir, "listenarr.sln")))
                            {
                                repoRootFromCwd = dir;
                                break;
                            }
                            dir = _fileSystem.GetParentDirectory(dir);
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        _logger.LogDebug(ex, "[StartupConfigService] Failed to probe for repo root from current directory '{Cwd}'", cwd);
                    }

                    var devRepoRoot = repoRootFromCwd ?? contentRoot;
                    bool rootIsListenarrApi = string.Equals(
                        Path.GetFileName(devRepoRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                        "listenarr.api",
                        StringComparison.OrdinalIgnoreCase);

                    var devRepoConfig = rootIsListenarrApi
                        ? Path.Join(devRepoRoot, "config", "config.json")
                        : Path.Join(devRepoRoot, "listenarr.api", "config", "config.json");

                    if (_fileSystem.FileExists(devRepoConfig) || _fileSystem.DirectoryExists(Path.GetDirectoryName(devRepoConfig)!))
                    {
                        _configPath = devRepoConfig;
                        _logger.LogInformation("[StartupConfigService] Development mode detected - forcing startup config to repo path: {DevRepoConfig}", devRepoConfig);
                    }
                }
                catch (Exception ex) when (
                    ex is IOException ||
                    ex is UnauthorizedAccessException ||
                    ex is DirectoryNotFoundException ||
                    ex is PathTooLongException ||
                    ex is System.Security.SecurityException)
                {
                    _logger.LogWarning(ex, "[StartupConfigService] Failed to resolve dev repo config using working directory; continuing with content-root probing");
                }
            }

            try
            {
                // If the content root equals the current AppContext base directory
                // (common in unit tests) prefer the local content-root config and
                // skip ancestor probing to keep tests isolated and deterministic.
                if (FileUtils.AreFilesystemPathsEquivalentForCurrentOs(contentRoot, AppContext.BaseDirectory))
                {
                    _configPath = Path.Join(contentRoot, "config", "config.json");
                }
                else
                {
                    var dirInfo = contentRoot;

                    // First pass: search ancestors for any local config at
                    // <ancestor>/config/config.json. This ensures local/test folders
                    // are preferred for isolation and determinism.
                    const int maxDepth = 8;
                    int depth = 0;
                    while (!string.IsNullOrWhiteSpace(dirInfo) && depth++ < maxDepth)
                    {
                        var candidateLocal = Path.Join(dirInfo, "config", "config.json");
                        if (_fileSystem.FileExists(candidateLocal))
                        {
                            _configPath = candidateLocal;
                            break;
                        }

                        dirInfo = _fileSystem.GetParentDirectory(dirInfo);
                    }

                    // Second pass (only if local not found): search for a repository-style
                    // config at <ancestor>/listenarr.api/config/config.json and prefer that.
                    if (string.IsNullOrEmpty(_configPath))
                    {
                        dirInfo = contentRoot;
                        depth = 0;
                        while (!string.IsNullOrWhiteSpace(dirInfo) && depth++ < maxDepth)
                        {
                            var candidateConfigPath = Path.Join(dirInfo, "listenarr.api", "config", "config.json");
                            if (_fileSystem.FileExists(candidateConfigPath))
                            {
                                _configPath = candidateConfigPath;
                                break;
                            }

                            dirInfo = _fileSystem.GetParentDirectory(dirInfo);
                        }
                    }
                }
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Error while probing for repository config.json; falling back to content root");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogDebug(ex, "Error while probing for repository config.json; falling back to content root");
            }

            // Fallback: use content-root/config/config.json if nothing else was found
            if (string.IsNullOrEmpty(_configPath))
            {
                _configPath = Path.Join(contentRoot, "config", "config.json");
            }

            _logger.LogInformation("[StartupConfigService] Using startup config path: {Path}", _configPath);

            Load();
        }

        private void Load()
        {
            try
            {
                if (!_fileSystem.FileExists(_configPath))
                {
                    _logger.LogInformation("Startup config not found at {Path}, creating default config", _configPath);
                    _config = CreateDefaultConfig();
                    SaveDefaultConfig();
                    return;
                }

                var json = _fileSystem.ReadAllText(_configPath);
                _config = JsonSerializer.Deserialize<StartupConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                // Auto-generate API key if missing from existing config
                if (_config != null && string.IsNullOrWhiteSpace(_config.ApiKey))
                {
                    _config.ApiKey = GenerateApiKey();
                    SaveConfigFile(_config); // Save the updated config with new API key
                    _logger.LogInformation("Auto-generated API key for existing configuration");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to load startup config from {Path}", _configPath);
                _config = new StartupConfig();
            }
        }

        public StartupConfig? GetConfig() => _config;

        public bool IsAuthenticationRequired()
            => (_config ?? new StartupConfig()).IsAuthenticationEnabled();

        public string GetEffectiveApiVersion(string? requestedApiVersion = null)
            => NormalizeApiVersion(_config?.ApiVersion, requestedApiVersion);

        public string NormalizeApiVersion(string? configuredApiVersion, string? requestedApiVersion = null)
            => ApiVersionNormalizer.NormalizeApiVersionString(configuredApiVersion)
               ?? ApiVersionNormalizer.NormalizeApiVersionString(requestedApiVersion)
               ?? ApiVersionNormalizer.DefaultApiVersion;

        public Task ReloadAsync()
        {
            Load();
            return Task.CompletedTask;
        }

        public Task SaveAsync(StartupConfig config)
        {
            // Preserve current config so we can roll back on failure
            StartupConfig? previous = _config;
            try
            {
                _logger.LogInformation("[StartupConfigService] Attempting to save config to {Path}", _configPath);
                // Update in-memory config first so callers observe the change immediately.
                // If the file write fails we revert to the previous config to avoid
                // leaving the service in an inconsistent in-memory state.
                _config = config;

                // Accept whatever value the caller provides for AuthenticationRequired.
                // This allows the frontend 'require login' toggle to control the flag.
                SaveConfigFile(config);
                _logger.LogInformation("[StartupConfigService] Successfully saved config to {Path}", _configPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "[StartupConfigService] Exception while saving config to {Path}", _configPath);
                // Revert in-memory config on failure
                try
                {
                    _config = previous;
                }
                catch (Exception rollbackEx) when (rollbackEx is not OutOfMemoryException &&
                                                   rollbackEx is not ThreadAbortException &&
                                                   rollbackEx is not StackOverflowException &&
                                                   rollbackEx is not ThreadInterruptedException)
                {
                    _logger.LogError(rollbackEx, "[StartupConfigService] Failed to revert in-memory startup config after error while saving to {Path}", _configPath);
                }
                throw;
            }

            return Task.CompletedTask;
        }

        private StartupConfig CreateDefaultConfig()
        {
            // Generate a cryptographically secure API key for initial setup
            var apiKey = GenerateApiKey();

            return new StartupConfig
            {
                // Basic configuration with sensible defaults
                LogLevel = "Information",
                EnableSsl = false,
                Port = 5000,
                SslPort = 6868,
                UrlBase = "/",
                // Absolute external URL used for links and images in outbound notifications.
                // No sensible default: left unset, notifications fall back to the URL the
                // request arrived on.
                ApplicationUrl = null,
                BindAddress = "*",
                ApiKey = apiKey, // Auto-generated on first run
                // Authentication: Set to "true" to require login, "false" for open access
                // When enabled, browser clients authenticate via secure session cookies
                AuthenticationRequired = "false",
                UpdateMechanism = "BuiltIn",
                LaunchBrowser = true,
                Branch = "main",
                InstanceName = "Listenarr",
                SyslogPort = null,
                AnalyticsEnabled = false,
                ApiVersion = "1",
                SslCertPath = null,
                SslCertPassword = null,
                Ffmpeg = new FfmpegConfig
                {
                    Provider = "gyan", // Default to gyan.dev for Windows
                    ReleaseOverride = null,
                    ChecksumUrl = null,
                    Arch = null
                }
            };
        }

        private static string GenerateApiKey()
        {
            var bytes = new byte[32];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            return Convert.ToBase64String(bytes).TrimEnd('=');
        }

        private void SaveDefaultConfig()
        {
            try
            {
                SaveConfigFile(_config);
                _logger.LogInformation("Default config.json created at {Path}", _configPath);
                _logger.LogInformation("Authentication is DISABLED by default. Set 'AuthenticationRequired' to 'true' in config.json to enable secure login.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to save default config to {Path}", _configPath);
            }
        }

        private void SaveConfigFile(StartupConfig? config)
        {
            if (config == null)
            {
                _logger.LogWarning("[StartupConfigService] SaveConfigFile called with null config. Path: {Path}", _configPath);
                return;
            }

            // Ensure the config directory exists
            var configDir = Path.GetDirectoryName(_configPath);
            if (string.IsNullOrWhiteSpace(configDir))
            {
                throw new IOException("Startup config path has no resolved config directory.");
            }

            if (!_fileSystem.TryValidateMutationTarget(_configPath, [configDir], out var safeConfigPath, out var reason))
            {
                throw new IOException($"Startup config path is outside the resolved config directory: {reason}");
            }

            if (!string.IsNullOrEmpty(configDir) && !_fileSystem.DirectoryExists(configDir))
            {
                _logger.LogWarning("[StartupConfigService] Config directory did not exist. Creating: {Dir}", configDir);
                _fileSystem.CreateDirectory(configDir);
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(config, options);
            try
            {
                _fileSystem.WriteAllText(safeConfigPath, json);
                _logger.LogInformation("[StartupConfigService] Config write succeeded for {Path}", safeConfigPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "[StartupConfigService] Config write failed for {Path}", _configPath);
                throw;
            }
        }
    }
}
