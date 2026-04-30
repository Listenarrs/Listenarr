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
using Microsoft.AspNetCore.DataProtection;

namespace Listenarr.Api.Services
{
    public class ConfigurationService : IConfigurationService
    {
        private readonly IApplicationSettingsRepository _settingsRepository;
        private readonly IApiConfigurationRepository _apiConfigRepository;
        private readonly IDownloadClientConfigurationRepository _downloadClientRepository;
        private readonly ILogger<ConfigurationService> _logger;
        private readonly IUserService _userService;
        private readonly IStartupConfigService _startupConfigService;
        private readonly IDataProtector _prowlarrImportProtector;

        public ConfigurationService(
            IApplicationSettingsRepository settingsRepository,
            IApiConfigurationRepository apiConfigRepository,
            IDownloadClientConfigurationRepository downloadClientRepository,
            ILogger<ConfigurationService> logger,
            IUserService userService,
            IStartupConfigService startupConfigService,
            IDataProtectionProvider? dataProtectionProvider = null)
        {
            _settingsRepository = settingsRepository;
            _apiConfigRepository = apiConfigRepository;
            _downloadClientRepository = downloadClientRepository;
            _logger = logger;
            _userService = userService;
            _startupConfigService = startupConfigService;
            _prowlarrImportProtector =
                (dataProtectionProvider ?? new EphemeralDataProtectionProvider())
                    .CreateProtector("Listenarr.ConfigurationService.ProwlarrImport");
        }

        // API Configuration methods
        public async Task<List<ApiConfiguration>> GetApiConfigurationsAsync()
        {
            try
            {
                return await _apiConfigRepository.GetAllAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error loading API configurations from database");
                return new List<ApiConfiguration>();
            }
        }

        public async Task<ApiConfiguration?> GetApiConfigurationAsync(string id)
        {
            try
            {
                return await _apiConfigRepository.GetByIdAsync(id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error loading API configuration {Id} from database", id);
                return null;
            }
        }

        public async Task<string> SaveApiConfigurationAsync(ApiConfiguration config)
        {
            try
            {
                var saved = await _apiConfigRepository.SaveAsync(config);
                return saved.Id;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error saving API configuration to database");
                throw;
            }
        }

        public async Task<bool> DeleteApiConfigurationAsync(string id)
        {
            try
            {
                return await _apiConfigRepository.DeleteAsync(id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error deleting API configuration from database");
                return false;
            }
        }

        // Download Client Configuration methods
        public async Task<List<DownloadClientConfiguration>> GetDownloadClientConfigurationsAsync()
        {
            try
            {
                return await _downloadClientRepository.GetAllAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error loading download client configurations from database");
                return new List<DownloadClientConfiguration>();
            }
        }

        public async Task<DownloadClientConfiguration?> GetDownloadClientConfigurationAsync(string id)
        {
            try
            {
                return await _downloadClientRepository.GetByIdAsync(id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error loading download client configuration {Id} from database", id);
                return null;
            }
        }

        public async Task<string> SaveDownloadClientConfigurationAsync(DownloadClientConfiguration config)
        {
            try
            {
                var saved = await _downloadClientRepository.SaveAsync(config);
                return saved.Id;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error saving download client configuration to database");
                throw;
            }
        }

        public async Task<bool> DeleteDownloadClientConfigurationAsync(string id)
        {
            try
            {
                return await _downloadClientRepository.DeleteAsync(id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error deleting download client configuration from database");
                return false;
            }
        }

        // Application Settings methods
        public async Task<ApplicationSettings> GetApplicationSettingsAsync()
        {
            try
            {
                var settings = await _settingsRepository.GetAsync();

                if (settings == null)
                {
                    settings = new ApplicationSettings();
                    await _settingsRepository.SaveAsync(settings);
                }

                settings.ImportBlacklistExtensions ??= new List<string>();
                settings.EnabledNotificationTriggers ??= new List<string>();
                settings.Webhooks ??= new List<WebhookConfiguration>();

                return settings;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error loading application settings from database (no runtime ALTERs will be attempted)");
                return new ApplicationSettings();
            }
        }

        public async Task SaveApplicationSettingsAsync(ApplicationSettings settings)
        {
            try
            {
                settings.Id = 1;

                // Preserve fields from existing settings when the incoming payload omits them.
                // Must run before normalization so null-checks catch truly absent fields.
                var existing = await _settingsRepository.GetAsync();
                if (existing != null)
                {
                    if (settings.ProwlarrUrl == null)
                        settings.ProwlarrUrl = existing.ProwlarrUrl;
                    if (settings.ProwlarrPort == null)
                        settings.ProwlarrPort = existing.ProwlarrPort;
                    if (settings.ProwlarrTagFilter == null)
                        settings.ProwlarrTagFilter = existing.ProwlarrTagFilter;
                    if (string.IsNullOrWhiteSpace(settings.ProwlarrApiKeyEncrypted)
                        || string.Equals(settings.ProwlarrApiKeyEncrypted, ApiResponseRedactor.RedactedValue, StringComparison.Ordinal))
                    {
                        settings.ProwlarrApiKeyEncrypted = existing.ProwlarrApiKeyEncrypted;
                    }
                    if (settings.EnabledNotificationTriggers == null)
                        settings.EnabledNotificationTriggers = existing.EnabledNotificationTriggers;
                    if (settings.Webhooks == null)
                        settings.Webhooks = existing.Webhooks;
                }

                try
                {
                    settings.EnabledNotificationTriggers = NormalizeTriggerList(settings.EnabledNotificationTriggers) ?? new List<string>();

                    if (settings.Webhooks != null)
                    {
                        foreach (var w in settings.Webhooks)
                        {
                            w.Triggers = NormalizeTriggerList(w.Triggers) ?? new List<string>();
                        }
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to normalize notification triggers due to JSON error; saving with original values");
                }
                catch (FormatException ex)
                {
                    _logger.LogWarning(ex, "Failed to normalize notification triggers due to formatting error; saving with original values");
                }

                await _settingsRepository.SaveAsync(settings);

                try
                {
                    if (!string.IsNullOrWhiteSpace(settings.AdminUsername) && !string.IsNullOrWhiteSpace(settings.AdminPassword))
                    {
                        _logger.LogDebug("Processing admin user credentials: {Username}", settings.AdminUsername);

                        var existingUser = await _userService.GetByUsernameAsync(settings.AdminUsername!);
                        if (existingUser == null)
                        {
                            _logger.LogInformation("Creating new admin user: {Username}", settings.AdminUsername);
                            await _userService.CreateUserAsync(settings.AdminUsername!, settings.AdminPassword!, null, true);
                            _logger.LogInformation("Admin user created successfully: {Username}", settings.AdminUsername);
                        }
                        else
                        {
                            _logger.LogInformation("Updating existing admin user password: {Username}", settings.AdminUsername);
                            await _userService.UpdatePasswordAsync(settings.AdminUsername!, settings.AdminPassword!);
                            _logger.LogInformation("Admin user password updated successfully: {Username}", settings.AdminUsername);
                        }
                    }
                    else
                    {
                        _logger.LogDebug("No admin credentials provided in settings update");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "Failed to create or update admin user '{Username}' from application settings. Settings will still be saved.", settings.AdminUsername);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error saving application settings to database (no runtime ALTERs will be attempted)");
                throw;
            }
        }

        public async Task<ProwlarrImportConnectionSettings> GetProwlarrImportSettingsAsync(bool includeSecret = false)
        {
            try
            {
                var settings = await _settingsRepository.GetAsync();

                if (settings == null)
                {
                    return new ProwlarrImportConnectionSettings();
                }

                var result = new ProwlarrImportConnectionSettings
                {
                    Url = settings.ProwlarrUrl?.Trim() ?? string.Empty,
                    Port = settings.ProwlarrPort,
                    TagFilter = settings.ProwlarrTagFilter?.Trim(),
                    HasSavedApiKey = !string.IsNullOrWhiteSpace(settings.ProwlarrApiKeyEncrypted),
                };

                if (includeSecret && result.HasSavedApiKey)
                {
                    result.ApiKey = TryUnprotectProwlarrApiKey(settings.ProwlarrApiKeyEncrypted);
                    if (string.IsNullOrWhiteSpace(result.ApiKey))
                    {
                        result.HasSavedApiKey = false;
                    }
                }

                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error loading saved Prowlarr import settings");
                return new ProwlarrImportConnectionSettings();
            }
        }

        public async Task<ProwlarrImportConnectionSettings> SaveProwlarrImportSettingsAsync(ProwlarrImportConnectionSettings settings)
        {
            try
            {
                var existing = await _settingsRepository.GetAsync() ?? new ApplicationSettings { Id = 1 };

                existing.ProwlarrUrl = string.IsNullOrWhiteSpace(settings.Url) ? string.Empty : settings.Url.Trim();
                existing.ProwlarrPort = settings.Port;
                existing.ProwlarrTagFilter = string.IsNullOrWhiteSpace(settings.TagFilter) ? null : settings.TagFilter.Trim();

                if (!string.IsNullOrWhiteSpace(settings.ApiKey)
                    && !string.Equals(settings.ApiKey, ApiResponseRedactor.RedactedValue, StringComparison.Ordinal))
                {
                    existing.ProwlarrApiKeyEncrypted = _prowlarrImportProtector.Protect(settings.ApiKey.Trim());
                }

                await _settingsRepository.SaveAsync(existing);
                return await GetProwlarrImportSettingsAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error saving Prowlarr import settings");
                throw;
            }
        }

        private string? TryUnprotectProwlarrApiKey(string? encryptedApiKey)
        {
            if (string.IsNullOrWhiteSpace(encryptedApiKey))
            {
                return null;
            }

            try
            {
                return _prowlarrImportProtector.Unprotect(encryptedApiKey);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to decrypt saved Prowlarr import API key");
                return null;
            }
        }

        private static List<string>? NormalizeTriggerList(List<string>? list)
        {
            if (list == null) return null;
            if (list.Count == 1)
            {
                var first = list[0];
                if (!string.IsNullOrWhiteSpace(first) && first.TrimStart().StartsWith("["))
                {
                    try
                    {
                        var decoded = System.Text.Json.JsonSerializer.Deserialize<List<string>>(first);
                        if (decoded != null && decoded.Count > 0) return decoded;
                    }
                    catch (JsonException)
                    {
                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                    }
                    catch (NotSupportedException)
                    {
                        System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                    }
                }
            }

            return list;
        }

        // Startup Configuration methods
        public Task<StartupConfig> GetStartupConfigAsync()
        {
            try
            {
                var config = _startupConfigService.GetConfig();
                return Task.FromResult(config ?? new StartupConfig());
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error retrieving startup configuration");
                return Task.FromResult(new StartupConfig());
            }
        }

        public async Task SaveStartupConfigAsync(StartupConfig config)
        {
            try
            {
                await _startupConfigService.SaveAsync(config);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error saving startup configuration");
                throw;
            }
        }

        // Webhook Configuration methods
        public async Task<List<WebhookConfiguration>> GetWebhookConfigurationsAsync()
        {
            try
            {
                var settings = await GetApplicationSettingsAsync();
                return settings?.Webhooks ?? new List<WebhookConfiguration>();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error retrieving webhook configurations");
                return new List<WebhookConfiguration>();
            }
        }
    }
}
