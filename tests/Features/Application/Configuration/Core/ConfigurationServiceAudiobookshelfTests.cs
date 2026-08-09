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
using Listenarr.Domain.Integrations;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Configuration.Core
{
    [Trait("Name", "ConfigurationServiceAudiobookshelfTests")]
    [Trait("Category", "ConfigurationService")]
    public class ConfigurationServiceAudiobookshelfTests : BaseTests
    {
        [Fact]
        public async Task SaveAudiobookshelfSettings_RoundTripsAndProtectsApiKey()
        {
            var svc = _provider.GetRequiredService<IConfigurationService>();

            var saved = await svc.SaveAudiobookshelfSettingsAsync(new AudiobookshelfConnectionSettings
            {
                Url = "http://abs.local:13378",
                ApiKey = "secret-token",
                LibraryId = "lib_123",
                NotifyOnImport = true,
            });

            Assert.Equal("http://abs.local:13378", saved.Url);
            Assert.Equal("lib_123", saved.LibraryId);
            Assert.True(saved.NotifyOnImport);
            Assert.True(saved.HasSavedApiKey);
            Assert.Null(saved.ApiKey);

            // The token is stored encrypted, never verbatim
            var raw = await _applicationSettingsRepository.GetAsync();
            Assert.NotNull(raw!.AudiobookshelfApiKeyEncrypted);
            Assert.NotEqual("secret-token", raw.AudiobookshelfApiKeyEncrypted);

            var withSecret = await svc.GetAudiobookshelfSettingsAsync(includeSecret: true);
            Assert.Equal("secret-token", withSecret.ApiKey);
        }

        [Fact]
        public async Task SaveAudiobookshelfSettings_BlankOrRedactedApiKey_KeepsSavedToken()
        {
            var svc = _provider.GetRequiredService<IConfigurationService>();

            await svc.SaveAudiobookshelfSettingsAsync(new AudiobookshelfConnectionSettings
            {
                Url = "http://abs.local:13378",
                ApiKey = "secret-token",
            });

            await svc.SaveAudiobookshelfSettingsAsync(new AudiobookshelfConnectionSettings
            {
                Url = "http://abs.local:13378",
                ApiKey = null,
                NotifyOnImport = true,
            });

            await svc.SaveAudiobookshelfSettingsAsync(new AudiobookshelfConnectionSettings
            {
                Url = "http://abs.local:13378",
                ApiKey = ApiResponseRedactor.RedactedValue,
                NotifyOnImport = true,
            });

            var result = await svc.GetAudiobookshelfSettingsAsync(includeSecret: true);
            Assert.True(result.HasSavedApiKey);
            Assert.Equal("secret-token", result.ApiKey);
            Assert.True(result.NotifyOnImport);
        }

        [Fact]
        public async Task SaveApplicationSettings_PartialPayload_PreservesAudiobookshelfSettings()
        {
            var svc = _provider.GetRequiredService<IConfigurationService>();

            await svc.SaveAudiobookshelfSettingsAsync(new AudiobookshelfConnectionSettings
            {
                Url = "http://abs.local:13378",
                ApiKey = "secret-token",
                LibraryId = "lib_123",
                NotifyOnImport = true,
            });

            // Simulate a settings save from a UI payload that omits the Audiobookshelf fields
            await svc.SaveApplicationSettingsAsync(new ApplicationSettings
            {
                Id = 1,
                OutputPath = FileUtils.GetAbsolutePath("partial-update"),
            });

            var result = await svc.GetAudiobookshelfSettingsAsync(includeSecret: true);
            Assert.Equal("http://abs.local:13378", result.Url);
            Assert.Equal("lib_123", result.LibraryId);
            Assert.True(result.NotifyOnImport);
            Assert.Equal("secret-token", result.ApiKey);
        }

        [Fact]
        public async Task SaveAudiobookshelfSettings_ClearingUrl_DropsSavedToken()
        {
            var svc = _provider.GetRequiredService<IConfigurationService>();

            await svc.SaveAudiobookshelfSettingsAsync(new AudiobookshelfConnectionSettings
            {
                Url = "http://abs.local:13378",
                ApiKey = "secret-token",
            });

            var cleared = await svc.SaveAudiobookshelfSettingsAsync(new AudiobookshelfConnectionSettings
            {
                Url = string.Empty,
            });

            Assert.Equal(string.Empty, cleared.Url);
            Assert.False(cleared.HasSavedApiKey);
        }
    }
}
