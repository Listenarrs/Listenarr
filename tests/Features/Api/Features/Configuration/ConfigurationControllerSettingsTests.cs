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
using Listenarr.Application.Common.Exceptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace Listenarr.Tests.Features.Api.Features.Configuration
{
    public class ConfigurationControllerSettingsTests
    {
        [Fact]
        public async Task SaveApplicationSettings_MissingVersion_ReturnsStableConflictWithoutBroadcast()
        {
            var configurationService = new Mock<IConfigurationService>(MockBehavior.Strict);
            configurationService
                .Setup(service => service.SaveApplicationSettingsAsync(
                    It.Is<ApplicationSettings>(settings => settings.Version == 0)))
                .ThrowsAsync(new ApplicationConflictException(
                    "settings_concurrency_conflict",
                    "Application settings must include the current version. Reload and try again."));
            var broadcaster = new Mock<IHubBroadcaster>(MockBehavior.Strict);
            var controller = new SettingsController(
                configurationService.Object,
                NullLogger<SettingsController>.Instance,
                broadcaster.Object);

            var result = await controller.SaveApplicationSettings(
                new ApplicationSettings { Version = 0 });

            var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
            var payload = JsonSerializer.SerializeToElement(conflict.Value);
            Assert.Equal(
                "settings_concurrency_conflict",
                payload.GetProperty("code").GetString());
            Assert.Equal(
                "Application settings must include the current version. Reload and try again.",
                payload.GetProperty("message").GetString());
            configurationService.Verify(service => service.SaveApplicationSettingsAsync(
                It.IsAny<ApplicationSettings>()), Times.Once);
            configurationService.VerifyNoOtherCalls();
            broadcaster.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task SaveApplicationSettings_UsesCommittedPayloadWithoutPostCommitRead()
        {
            var configurationService = new Mock<IConfigurationService>(MockBehavior.Strict);
            configurationService
                .Setup(service => service.SaveApplicationSettingsAsync(It.IsAny<ApplicationSettings>()))
                .Callback<ApplicationSettings>(settings => settings.Version = 8)
                .Returns(Task.CompletedTask);
            var broadcaster = new Mock<IHubBroadcaster>(MockBehavior.Strict);
            broadcaster.Setup(candidate => candidate.BroadcastAsync(
                    RealtimeHubTarget.Settings,
                    "SettingsUpdated",
                    It.IsAny<object>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var controller = new SettingsController(
                configurationService.Object,
                NullLogger<SettingsController>.Instance,
                broadcaster.Object);

            var result = await controller.SaveApplicationSettings(
                new ApplicationSettings { Version = 7, OutputPath = "library" });

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var saved = Assert.IsType<ApplicationSettings>(ok.Value);
            Assert.Equal(8, saved.Version);
            Assert.Equal("library", saved.OutputPath);
            configurationService.Verify(candidate => candidate.SaveApplicationSettingsAsync(
                It.IsAny<ApplicationSettings>()), Times.Once);
            configurationService.VerifyNoOtherCalls();
            broadcaster.Verify(candidate => candidate.BroadcastAsync(
                RealtimeHubTarget.Settings,
                "SettingsUpdated",
                It.IsAny<object>(),
                It.IsAny<CancellationToken>()), Times.Once);
            broadcaster.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task GetApplicationSettings_DoesNotReturnEncryptedProwlarrApiKey()
        {
            var configurationService = new Mock<IConfigurationService>(MockBehavior.Strict);
            configurationService
                .Setup(x => x.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings
                {
                    Id = 1,
                    ProwlarrUrl = "http://localhost",
                    ProwlarrPort = 9696,
                    ProwlarrApiKeyEncrypted = "ciphertext",
                    ProwlarrTagFilter = "audiobooks"
                });

            var controller = new SettingsController(
                configurationService.Object,
                NullLogger<SettingsController>.Instance,
                Mock.Of<IHubBroadcaster>());

            var result = await controller.GetApplicationSettings();
            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var settings = Assert.IsType<ApplicationSettings>(ok.Value);

            Assert.Equal("http://localhost", settings.ProwlarrUrl);
            Assert.Equal(9696, settings.ProwlarrPort);
            Assert.Equal("audiobooks", settings.ProwlarrTagFilter);
            Assert.Null(settings.ProwlarrApiKeyEncrypted);

            configurationService.Verify(x => x.GetApplicationSettingsAsync(), Times.Once);
            configurationService.VerifyNoOtherCalls();
        }
    }
}
