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
using Listenarr.Application.Common.Exceptions;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Configuration.Core
{
    public partial class ConfigurationService(
        IApplicationSettingsRepository settingsRepository,
        IApiConfigurationRepository apiConfigRepository,
        IDownloadClientConfigurationRepository downloadClientRepository,
        ILogger<ConfigurationService> logger,
        IUserService userService,
        IStartupConfigService startupConfigService,
        IRootFolderRepository rootFolderRepository,
        ISecretProtector secretProtector) : IConfigurationService
    {
        private static readonly SemaphoreSlim ApplicationSettingsInitializationLock = new(1, 1);

        // Application Settings methods
        public async Task<ApplicationSettings> GetApplicationSettingsAsync()
        {
            try
            {
                await ApplicationSettingsInitializationLock.WaitAsync();
                try
                {
                    var settings = await settingsRepository.GetAsync();

                    if (settings == null)
                    {
                        settings = await settingsRepository.InitializeIfMissingAsync(
                            new ApplicationSettings());
                    }

                    ApplyRuntimeDefaults(settings);
                    return await EnsureOutputPathAsync(settings);
                }
                finally
                {
                    ApplicationSettingsInitializationLock.Release();
                }
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                logger.LogError(exception, "Error loading application settings from database (no runtime ALTERs will be attempted)");
                throw;
            }
        }

        private async Task<ApplicationSettings> EnsureOutputPathAsync(ApplicationSettings settings)
        {
            if (!string.IsNullOrEmpty(settings.OutputPath))
            {
                return settings;
            }

            var outputPath = await ResolveDefaultOutputPathAsync();
            var candidate = settings;

            for (var attempt = 0; attempt < 3; attempt++)
            {
                ApplyRuntimeDefaults(candidate);
                if (!string.IsNullOrEmpty(candidate.OutputPath))
                {
                    return candidate;
                }

                candidate.OutputPath = outputPath;
                try
                {
                    return await settingsRepository.SaveAsync(candidate);
                }
                catch (ApplicationConflictException) when (attempt < 2)
                {
                    // Fresh startup can have several hosted services ask for settings at
                    // the same time. If another scope touched the singleton row first,
                    // reload and retry against the latest version instead of surfacing
                    // a benign startup conflict.
                    var latest = await settingsRepository.GetAsync();
                    if (latest == null)
                    {
                        throw;
                    }

                    candidate = latest;
                }
            }

            var finalSettings = await settingsRepository.GetAsync();
            if (finalSettings != null)
            {
                ApplyRuntimeDefaults(finalSettings);
                finalSettings.OutputPath = string.IsNullOrEmpty(finalSettings.OutputPath)
                    ? outputPath
                    : finalSettings.OutputPath;
                return finalSettings;
            }

            return candidate;
        }

        private async Task<string> ResolveDefaultOutputPathAsync()
        {
            var rootFolder = await rootFolderRepository.GetDefaultAsync();
            if (rootFolder != null)
            {
                logger.LogInformation($"OutputPath not configured, using default root folder: {rootFolder.Path}");
                return rootFolder.Path;
            }

            logger.LogInformation($"OutputPath not configured, using: {AppContext.BaseDirectory}");
            return AppContext.BaseDirectory;
        }

        public async Task SaveApplicationSettingsAsync(ApplicationSettings settings)
        {
            try
            {
                settings.Id = 1;

                // Preserve fields from existing settings when the incoming payload omits them.
                // Must run before normalization so null-checks catch truly absent fields.
                var existing = await settingsRepository.GetAsync();
                if (existing != null && settings.Version <= 0)
                {
                    throw new ApplicationConflictException(
                        "settings_concurrency_conflict",
                        "Application settings must include the current version. Reload and try again.");
                }

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

                if (!string.IsNullOrWhiteSpace(settings.OutputPath)
                    && !string.Equals(
                        settings.OutputPath,
                        existing?.OutputPath,
                        StringComparison.Ordinal))
                {
                    if (!FileUtils.TryNormalizeUserProvidedDirectoryPathForCurrentOs(
                            settings.OutputPath,
                            out var normalizedOutputPath,
                            out var outputPathReason,
                            allowFileSystemRoot: true,
                            rejectParentTraversal: true))
                    {
                        throw new ArgumentException(
                            $"OutputPath is invalid: {outputPathReason}",
                            nameof(settings));
                    }

                    settings.OutputPath = normalizedOutputPath;
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
                    logger.LogWarning(ex, "Failed to normalize notification triggers due to JSON error; saving with original values");
                }
                catch (FormatException ex)
                {
                    logger.LogWarning(ex, "Failed to normalize notification triggers due to formatting error; saving with original values");
                }

                await settingsRepository.SaveAsync(settings);

                try
                {
                    if (!string.IsNullOrWhiteSpace(settings.AdminUsername) && !string.IsNullOrWhiteSpace(settings.AdminPassword))
                    {
                        logger.LogDebug("Processing admin user credentials: {Username}", settings.AdminUsername);

                        var existingUser = await userService.GetByUsernameAsync(settings.AdminUsername!);
                        if (existingUser == null)
                        {
                            logger.LogInformation("Creating new admin user: {Username}", settings.AdminUsername);
                            await userService.CreateUserAsync(settings.AdminUsername!, settings.AdminPassword!, null, true);
                            logger.LogInformation("Admin user created successfully: {Username}", settings.AdminUsername);
                        }
                        else
                        {
                            logger.LogInformation("Updating existing admin user password: {Username}", settings.AdminUsername);
                            await userService.UpdatePasswordAsync(settings.AdminUsername!, settings.AdminPassword!);
                            logger.LogInformation("Admin user password updated successfully: {Username}", settings.AdminUsername);
                        }
                    }
                    else
                    {
                        logger.LogDebug("No admin credentials provided in settings update");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    // Admin provisioning failed after credentials were supplied. Surface
                    // the failure to the caller — SettingsView relies on this throwing
                    // before it persists AuthenticationRequired=true on its second
                    // request, otherwise the user can be locked out of an instance
                    // that has no working admin (password-policy rejection, repo I/O
                    // error, race against a concurrent admin write, etc.). The
                    // settings row above was already saved, which is intentional:
                    // non-admin changes (notification triggers, webhooks, etc.) are
                    // worth preserving even when credential provisioning fails.
                    logger.LogError(ex, "Failed to create or update admin user '{Username}' from application settings; surfacing failure to caller", settings.AdminUsername);
                    throw;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Error saving application settings to database (no runtime ALTERs will be attempted)");
                throw;
            }
        }

        public async Task<ProwlarrImportConnectionSettings> GetProwlarrImportSettingsAsync(bool includeSecret = false)
        {
            try
            {
                var settings = await settingsRepository.GetAsync();

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
                logger.LogError(ex, "Error loading saved Prowlarr import settings");
                return new ProwlarrImportConnectionSettings();
            }
        }

        public async Task<ProwlarrImportConnectionSettings> SaveProwlarrImportSettingsAsync(ProwlarrImportConnectionSettings settings)
        {
            try
            {
                var existing = await settingsRepository.GetAsync() ?? new ApplicationSettings { Id = 1 };

                existing.ProwlarrUrl = string.IsNullOrWhiteSpace(settings.Url) ? string.Empty : settings.Url.Trim();
                existing.ProwlarrPort = settings.Port;
                existing.ProwlarrTagFilter = string.IsNullOrWhiteSpace(settings.TagFilter) ? null : settings.TagFilter.Trim();

                if (!string.IsNullOrWhiteSpace(settings.ApiKey)
                    && !string.Equals(settings.ApiKey, ApiResponseRedactor.RedactedValue, StringComparison.Ordinal))
                {
                    existing.ProwlarrApiKeyEncrypted = secretProtector.Protect(settings.ApiKey.Trim());
                }

                await settingsRepository.SaveAsync(existing);
                return await GetProwlarrImportSettingsAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Error saving Prowlarr import settings");
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
                return secretProtector.Unprotect(encryptedApiKey);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Failed to decrypt saved Prowlarr import API key");
                return null;
            }
        }

        // Startup Configuration methods
        public Task<StartupConfig> GetStartupConfigAsync()
        {
            try
            {
                var config = startupConfigService.GetConfig();
                return Task.FromResult(config ?? new StartupConfig());
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Error retrieving startup configuration");
                return Task.FromResult(new StartupConfig());
            }
        }

        public async Task SaveStartupConfigAsync(StartupConfig config)
        {
            try
            {
                // Defense-in-depth backstop against the auth-enable lockout.
                // SaveApplicationSettingsAsync's throw-on-failure (above) only
                // covers the case where admin credentials were *supplied* but
                // provisioning failed. The settings DTO clears blank fields
                // before save, so a user who flips the login-screen toggle
                // with empty (or username-only) credentials silently skips
                // provisioning entirely — and without this check would still
                // reach the startup-config write below, locking themselves
                // out of an instance that has no working admin to log in as.
                //
                // Only enforced on the *transition* from auth-disabled to
                // auth-enabled. Once auth is already on, the admin must
                // already exist (or no one could have toggled it on through
                // this same check), and every subsequent unrelated save
                // — API key regenerations, port changes, log-level tweaks —
                // shouldn't have to re-prove the admin row is still there.
                // Demotion or deletion of the last admin row while auth is
                // enabled is a separate concern and belongs in the user
                // management path, not here.
                if (config != null && config.IsAuthenticationEnabled())
                {
                    var currentConfig = startupConfigService.GetConfig();
                    var wasAuthEnabled = currentConfig?.IsAuthenticationEnabled() == true;
                    if (!wasAuthEnabled)
                    {
                        var admins = await userService.GetAdminUsersAsync();
                        if (admins == null || admins.Count == 0)
                        {
                            throw new InvalidOperationException(
                                "Cannot enable the login screen: no admin user exists. " +
                                "Set an admin username and password in the same save to " +
                                "create one, or leave the login screen disabled.");
                        }
                    }
                }

                await startupConfigService.SaveAsync(config!);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Error saving startup configuration");
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
                logger.LogError(ex, "Error retrieving webhook configurations");
                return new List<WebhookConfiguration>();
            }
        }
    }
}
