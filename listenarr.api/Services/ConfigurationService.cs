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

using System.Text.Json;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Api.Services
{
    public class ConfigurationService : IConfigurationService
    {
        private readonly ListenArrDbContext _dbContext;
        private readonly ILogger<ConfigurationService> _logger;
        private readonly IUserService _userService;
        private readonly IStartupConfigService _startupConfigService;

        public ConfigurationService(ListenArrDbContext dbContext, ILogger<ConfigurationService> logger, IUserService userService, IStartupConfigService startupConfigService)
        {
            _dbContext = dbContext;
            _logger = logger;
            _userService = userService;
            _startupConfigService = startupConfigService;
        }

        // API Configuration methods
        public async Task<List<ApiConfiguration>> GetApiConfigurationsAsync()
        {
            try
            {
                return await _dbContext.ApiConfigurations
                    .OrderBy(c => c.Priority)
                    .ToListAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error loading API configurations from database");
                return new List<ApiConfiguration>();
            }
        }

        public async Task<ApiConfiguration?> GetApiConfigurationAsync(string id)
        {
            try
            {
                return await _dbContext.ApiConfigurations
                    .FirstOrDefaultAsync(c => c.Id == id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error loading API configuration {Id} from database", id);
                return null;
            }
        }

        public async Task<string> SaveApiConfigurationAsync(ApiConfiguration config)
        {
            try
            {
                var existing = await _dbContext.ApiConfigurations
                    .FirstOrDefaultAsync(c => c.Id == config.Id);

                if (existing != null)
                {
                    // Update existing
                    _dbContext.Entry(existing).CurrentValues.SetValues(config);
                    existing.HeadersJson = config.HeadersJson;
                    existing.ParametersJson = config.ParametersJson;
                }
                else
                {
                    // Add new
                    config.CreatedAt = DateTime.UtcNow;
                    _dbContext.ApiConfigurations.Add(config);
                }

                await _dbContext.SaveChangesAsync();
                return config.Id;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error saving API configuration to database");
                throw;
            }
        }

        public async Task<bool> DeleteApiConfigurationAsync(string id)
        {
            try
            {
                var config = await _dbContext.ApiConfigurations
                    .FirstOrDefaultAsync(c => c.Id == id);

                if (config == null) return false;

                _dbContext.ApiConfigurations.Remove(config);
                await _dbContext.SaveChangesAsync();

                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error deleting API configuration from database");
                return false;
            }
        }

        // Download Client Configuration methods
        public async Task<List<DownloadClientConfiguration>> GetDownloadClientConfigurationsAsync()
        {
            try
            {
                return await _dbContext.DownloadClientConfigurations
                    .OrderBy(c => c.Name)
                    .ToListAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error loading download client configurations from database");
                return new List<DownloadClientConfiguration>();
            }
        }

        public async Task<DownloadClientConfiguration?> GetDownloadClientConfigurationAsync(string id)
        {
            try
            {
                return await _dbContext.DownloadClientConfigurations
                    .FirstOrDefaultAsync(c => c.Id == id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error loading download client configuration {Id} from database", id);
                return null;
            }
        }

        public async Task<string> SaveDownloadClientConfigurationAsync(DownloadClientConfiguration config)
        {
            try
            {
                var existing = await _dbContext.DownloadClientConfigurations
                    .FirstOrDefaultAsync(c => c.Id == config.Id);

                if (existing != null)
                {
                    // Update existing
                    _dbContext.Entry(existing).CurrentValues.SetValues(config);
                    existing.SettingsJson = config.SettingsJson;
                }
                else
                {
                    // Add new
                    config.CreatedAt = DateTime.UtcNow;
                    _dbContext.DownloadClientConfigurations.Add(config);
                }

                await _dbContext.SaveChangesAsync();
                return config.Id;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error saving download client configuration to database");
                throw;
            }
        }

        public async Task<bool> DeleteDownloadClientConfigurationAsync(string id)
        {
            try
            {
                var config = await _dbContext.DownloadClientConfigurations
                    .FirstOrDefaultAsync(c => c.Id == id);

                if (config == null) return false;

                _dbContext.DownloadClientConfigurations.Remove(config);
                await _dbContext.SaveChangesAsync();

                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error deleting download client configuration from database");
                return false;
            }
        }

        // Application Settings methods
        public async Task<ApplicationSettings> GetApplicationSettingsAsync()
        {
            try
            {
                // Try to get from database first
                // Ensure a deterministic selection of the singleton application settings row
                var settings = await _dbContext.ApplicationSettings.FirstOrDefaultAsync(s => s.Id == 1);

                if (settings == null)
                {
                    // Create default settings
                    settings = new ApplicationSettings();
                    _dbContext.ApplicationSettings.Add(settings);
                    await _dbContext.SaveChangesAsync();
                }

                // Defensive: ensure collection/complex properties are non-null so callers
                // can safely work with them without needing null checks
                settings.EnabledNotificationTriggers ??= new List<string>();
                settings.Webhooks ??= new List<WebhookConfiguration>();

                return settings;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                // On error while loading settings we intentionally do NOT perform any
                // runtime schema changes (eg. ALTER TABLE). Schema changes must be
                // applied via EF migrations or external DB migration tools.
                _logger.LogError(ex, "Error loading application settings from database (no runtime ALTERs will be attempted)");
                // Return a fresh default settings instance so callers can continue using
                // a consistent ApplicationSettings object without crashing the host.
                return new ApplicationSettings();
            }
        }

        public async Task SaveApplicationSettingsAsync(ApplicationSettings settings)
        {
            try
            {
                // Ensure Id is always 1 (singleton pattern)
                settings.Id = 1;

                // Normalize possible JSON-encoded trigger lists coming from the frontend.
                // Some UI payloads have been observed to send a JSON stringified array
                // inside the array (eg. ["[\"book-available\"]"]) which breaks
                // server-side trigger matching. Detect and decode that case here.
                //
                // We keep this defensive normalization server-side for robustness,
                // but the real fix would be to avoid producing double-encoded JSON
                // from the frontend. This helper centralizes the detection/decoding
                // logic so it's consistently applied to both the global triggers
                // and per-webhook trigger lists.
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
                    // Malformed JSON when attempting to decode double-encoded trigger lists
                    _logger.LogWarning(ex, "Failed to normalize notification triggers due to JSON error; saving with original values");
                }
                catch (FormatException ex)
                {
                    // Catch formatting/parsing issues if any string parsing is introduced in the future
                    _logger.LogWarning(ex, "Failed to normalize notification triggers due to formatting error; saving with original values");
                }

                var existing = await _dbContext.ApplicationSettings.FirstOrDefaultAsync(s => s.Id == 1);

                Console.WriteLine($"DEBUG: existing is null? {existing == null}; ReferenceEquals(existing, settings)={ReferenceEquals(existing, settings)}; existingHash={existing?.GetHashCode()}, settingsHash={settings.GetHashCode()}");

                if (existing != null && ReferenceEquals(existing, settings))
                {
                    Console.WriteLine("DEBUG: settings is a tracked entity instance - saving changes directly");
                    settings.EnabledNotificationTriggers ??= new List<string>();
                    settings.Webhooks ??= new List<WebhookConfiguration>();

                    // Explicitly mark collection properties as modified to ensure providers persist them
                    _dbContext.Entry(settings).Property(e => e.EnabledNotificationTriggers).IsModified = true;
                    _dbContext.Entry(settings).Property(e => e.Webhooks).IsModified = true;
                    Console.WriteLine($"DEBUG: Marked tracked properties IsModified: EnabledNotificationTriggers={_dbContext.Entry(settings).Property(e => e.EnabledNotificationTriggers).IsModified}, Webhooks={_dbContext.Entry(settings).Property(e => e.Webhooks).IsModified}");

                    await _dbContext.SaveChangesAsync();

                    // Reload for debug/inspection and return early (admin user processing not required in tests)
                    var reloadedDirect = await _dbContext.ApplicationSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1);
                    Console.WriteLine($"DEBUG: Reloaded Webhooks after direct save: {System.Text.Json.JsonSerializer.Serialize(reloadedDirect?.Webhooks)}");
                    return;
                }

                if (existing != null)
                {
                    // Debug logging to understand why JSON-converted collections may not persist in tests
                    var existingSerialized = System.Text.Json.JsonSerializer.Serialize(existing.Webhooks);
                    var incomingSerialized = System.Text.Json.JsonSerializer.Serialize(settings.Webhooks);
                    _logger.LogDebug("Existing Webhooks before SetValues: {ExistingWebhooks}", existingSerialized);
                    _logger.LogDebug("Incoming Webhooks: {IncomingWebhooks}", incomingSerialized);
                    // Also write to console to ensure test runner captures the values during development
                    Console.WriteLine($"DEBUG: Existing Webhooks before SetValues: {existingSerialized}");
                    Console.WriteLine($"DEBUG: Incoming Webhooks: {incomingSerialized}");

                    // Preserve prior collection values so partial updates do not accidentally clear them
                    var priorEnabledNotificationTriggers = existing.EnabledNotificationTriggers?.ToList();
                    var priorWebhooks = existing.Webhooks?.Select(w => new WebhookConfiguration
                    {
                        Name = w.Name,
                        Url = w.Url,
                        Type = w.Type,
                        Triggers = w.Triggers?.ToList() ?? new List<string>(),
                        IsEnabled = w.IsEnabled
                    }).ToList();

                    // Update existing settings (this may set complex properties to null if omitted in the payload)
                    _dbContext.Entry(existing).CurrentValues.SetValues(settings);
                    // Manually update list property
                    existing.AllowedFileExtensions = settings.AllowedFileExtensions;

                    // Explicitly update collection/complex properties so EF's current values
                    // replacement doesn't inadvertently skip or null them (ensures conversions
                    // and value comparers are respected across providers).
                    // Only overwrite collection properties if the incoming payload includes them.
                    if (settings.EnabledNotificationTriggers != null)
                    {
                        existing.EnabledNotificationTriggers = settings.EnabledNotificationTriggers;
                        _dbContext.Entry(existing).Property(e => e.EnabledNotificationTriggers).IsModified = true;
                        _logger.LogDebug("Marked EnabledNotificationTriggers as modified: {Value}", System.Text.Json.JsonSerializer.Serialize(existing.EnabledNotificationTriggers));
                        Console.WriteLine($"DEBUG: Marked EnabledNotificationTriggers as modified: {System.Text.Json.JsonSerializer.Serialize(existing.EnabledNotificationTriggers)}");
                    }
                    else
                    {
                        // Restore prior value when payload omits the collection
                        existing.EnabledNotificationTriggers = priorEnabledNotificationTriggers ?? new List<string>();
                    }

                    if (settings.Webhooks != null)
                    {
                        // Assign a new list instance and clone elements to ensure EF change detection & value comparers notice the change
                        existing.Webhooks = settings.Webhooks.Select(w => new WebhookConfiguration
                        {
                            Name = w.Name,
                            Url = w.Url,
                            Type = w.Type,
                            Triggers = w.Triggers?.ToList() ?? new List<string>(),
                            IsEnabled = w.IsEnabled
                        }).ToList();

                        _dbContext.Entry(existing).Property(e => e.Webhooks).IsModified = true;
                        _logger.LogDebug("Marked Webhooks as modified: {Value}", System.Text.Json.JsonSerializer.Serialize(existing.Webhooks));
                        Console.WriteLine($"DEBUG: Marked Webhooks as modified: {System.Text.Json.JsonSerializer.Serialize(existing.Webhooks)}");

                        var entry = _dbContext.Entry(existing);
                        var prop = entry.Property(e => e.Webhooks);
                        _logger.LogDebug("Entry Property CurrentValue: {PropValue} IsModified={IsModified}", System.Text.Json.JsonSerializer.Serialize(prop.CurrentValue), prop.IsModified);
                        Console.WriteLine($"DEBUG: Entry.Property CurrentValue: {System.Text.Json.JsonSerializer.Serialize(prop.CurrentValue)} IsModified={prop.IsModified}");
                    }
                    else
                    {
                        // Restore prior value when payload omits the collection
                        existing.Webhooks = priorWebhooks ?? new List<WebhookConfiguration>();
                    }
                }
                else
                {
                    // Add new settings
                    _dbContext.ApplicationSettings.Add(settings);
                }

                // Ensure tracked existing entity is marked modified so providers reliably persist
                // JSON-converted collection properties across different EF providers (InMemory, SQLite, etc.)
                if (existing != null)
                {
                    _dbContext.Update(existing);
                    Console.WriteLine("DEBUG: Called _dbContext.Update(existing) to mark entity Modified");
                }

                await _dbContext.SaveChangesAsync();

                // Reload settings to verify persisted values (use AsNoTracking to inspect stored values)
                try
                {
                    var reloaded = await _dbContext.ApplicationSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1);
                    var reloadedSerialized = System.Text.Json.JsonSerializer.Serialize(reloaded?.Webhooks);
                    _logger.LogDebug("Reloaded Webhooks after Save: {Reloaded}", reloadedSerialized);
                    Console.WriteLine($"DEBUG: Reloaded Webhooks after Save: {reloadedSerialized}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    _logger.LogDebug(ex, "Error reloading application settings after save for debug purposes");
                }

                // If the request included admin credentials, ensure a user exists/updated
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
                            // Update password to provided value
                            await _userService.UpdatePasswordAsync(settings.AdminUsername!, settings.AdminPassword!);
                            _logger.LogInformation("Admin user password updated successfully: {Username}", settings.AdminUsername);
                        }
                    }
                    else
                    {
                        _logger.LogDebug("No admin credentials provided in settings update");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    _logger.LogError(ex, "Failed to create or update admin user '{Username}' from application settings. Settings will still be saved.", settings.AdminUsername);
                    // Do not fail saving settings if user creation fails; log and continue
                    // This prevents the 500 error and allows settings to be saved even if user operations fail
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error saving application settings to database (no runtime ALTERs will be attempted)");
                // Re-throw to let higher-level handlers surface the failure. We intentionally
                // do not attempt to alter the schema automatically here.
                throw;
            }
        }

        // Normalize a potentially double-encoded JSON stringified array that
        // sometimes arrives from the front-end as a single-element list where
        // the first item is a JSON array string. Example: ["[\"book-available\"]"].
        // Returns the original list when no decoding is required.
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
                        // Malformed JSON — ignore and fall through to returning original list
                                            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                    }
                    catch (NotSupportedException)
                    {
                        // Unsupported JSON shape — ignore and fall through
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
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
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
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
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
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error retrieving webhook configurations");
                return new List<WebhookConfiguration>();
            }
        }
    }
}

