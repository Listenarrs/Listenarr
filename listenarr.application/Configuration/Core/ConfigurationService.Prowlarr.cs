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

using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Configuration.Core
{
    public partial class ConfigurationService
    {
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
    }
}
