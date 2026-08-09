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
        public async Task<AudiobookshelfConnectionSettings> GetAudiobookshelfSettingsAsync(bool includeSecret = false)
        {
            try
            {
                var settings = await settingsRepository.GetAsync();

                if (settings == null)
                {
                    return new AudiobookshelfConnectionSettings();
                }

                var result = new AudiobookshelfConnectionSettings
                {
                    Url = settings.AudiobookshelfUrl?.Trim() ?? string.Empty,
                    LibraryId = settings.AudiobookshelfLibraryId?.Trim(),
                    NotifyOnImport = settings.AudiobookshelfNotifyOnImport == true,
                    HasSavedApiKey = !string.IsNullOrWhiteSpace(settings.AudiobookshelfApiKeyEncrypted),
                };

                if (includeSecret && result.HasSavedApiKey)
                {
                    result.ApiKey = TryUnprotectAudiobookshelfApiKey(settings.AudiobookshelfApiKeyEncrypted);
                    if (string.IsNullOrWhiteSpace(result.ApiKey))
                    {
                        result.HasSavedApiKey = false;
                    }
                }

                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Error loading saved Audiobookshelf settings");
                return new AudiobookshelfConnectionSettings();
            }
        }

        public async Task<AudiobookshelfConnectionSettings> SaveAudiobookshelfSettingsAsync(AudiobookshelfConnectionSettings settings)
        {
            try
            {
                var existing = await settingsRepository.GetAsync() ?? new ApplicationSettings { Id = 1 };

                existing.AudiobookshelfUrl = string.IsNullOrWhiteSpace(settings.Url) ? string.Empty : settings.Url.Trim();
                existing.AudiobookshelfLibraryId = string.IsNullOrWhiteSpace(settings.LibraryId) ? null : settings.LibraryId.Trim();
                existing.AudiobookshelfNotifyOnImport = settings.NotifyOnImport;

                if (!string.IsNullOrWhiteSpace(settings.ApiKey)
                    && !string.Equals(settings.ApiKey, ApiResponseRedactor.RedactedValue, StringComparison.Ordinal))
                {
                    existing.AudiobookshelfApiKeyEncrypted = secretProtector.Protect(settings.ApiKey.Trim());
                }
                else if (string.IsNullOrWhiteSpace(existing.AudiobookshelfUrl))
                {
                    // Clearing the URL disconnects the integration; drop the stored token with it.
                    existing.AudiobookshelfApiKeyEncrypted = null;
                }

                await settingsRepository.SaveAsync(existing);
                return await GetAudiobookshelfSettingsAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Error saving Audiobookshelf settings");
                throw;
            }
        }

        private string? TryUnprotectAudiobookshelfApiKey(string? encryptedApiKey)
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
                logger.LogWarning(ex, "Failed to decrypt saved Audiobookshelf API token");
                return null;
            }
        }
    }
}
