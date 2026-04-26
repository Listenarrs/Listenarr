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
using System.Net;
using Listenarr.Api.Controllers;
using Listenarr.Api.Hubs;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Controllers
{
    public class ConfigurationControllerDownloadClientTests
    {
        [Fact]
        public async Task TestDownloadClientConfiguration_RemoteCaller_AllowsPrivateHost_AndRedactsSecrets()
        {
            // Arrange
            var configurationService = new Mock<IConfigurationService>(MockBehavior.Strict);
            var downloadService = new Mock<IDownloadService>(MockBehavior.Strict);
            var logger = NullLogger<ConfigurationController>.Instance;
            var userService = Mock.Of<IUserService>();
            var settingsHub = Mock.Of<IHubContext<SettingsHub>>();

            var testedClient = new DownloadClientConfiguration
            {
                Id = "client-1",
                Name = "NZBGet",
                Type = "nzbget",
                Host = "192.168.1.50",
                Port = 6789,
                Username = "nzb-user",
                Password = "nzb-pass",
                UseSSL = false,
                IsEnabled = true,
                Settings = new Dictionary<string, object>
                {
                    ["apiKey"] = "very-secret-api-key"
                }
            };

            downloadService
                .Setup(x => x.TestDownloadClientAsync(It.IsAny<DownloadClientConfiguration>()))
                .ReturnsAsync((true, "Connection successful", testedClient));

            var controller = new ConfigurationController(
                configurationService.Object,
                logger,
                userService,
                settingsHub,
                downloadService.Object,
                null!);

            var httpContext = new DefaultHttpContext();
            httpContext.Connection.RemoteIpAddress = IPAddress.Parse("8.8.8.8");
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

            var request = new DownloadClientConfiguration
            {
                // Keep ID empty so the endpoint does not try to merge from an existing saved config.
                Id = string.Empty,
                Name = "NZBGet",
                Type = "nzbget",
                Host = "192.168.1.50",
                Port = 6789,
                Username = "nzb-user",
                Password = "nzb-pass",
                UseSSL = false,
                IsEnabled = true,
                Settings = new Dictionary<string, object>
                {
                    ["apiKey"] = "very-secret-api-key"
                }
            };

            // Act
            var actionResult = await controller.TestDownloadClientConfiguration(request);

            // Assert
            var ok = Assert.IsType<OkObjectResult>(actionResult.Result);
            Assert.NotNull(ok.Value);
            var payload = ok.Value!;

            var successProp = payload.GetType().GetProperty("success");
            Assert.NotNull(successProp);
            Assert.True((bool)successProp!.GetValue(payload)!);

            var clientProp = payload.GetType().GetProperty("client");
            Assert.NotNull(clientProp);
            var redactedClient = Assert.IsType<DownloadClientConfiguration>(clientProp!.GetValue(payload));
            Assert.Equal(ApiResponseRedactor.RedactedValue, redactedClient.Username);
            Assert.Equal(ApiResponseRedactor.RedactedValue, redactedClient.Password);
            Assert.True(redactedClient.Settings.TryGetValue("apiKey", out var redactedApiKey));
            Assert.Equal(ApiResponseRedactor.RedactedValue, redactedApiKey?.ToString());

            downloadService.Verify(
                x => x.TestDownloadClientAsync(It.Is<DownloadClientConfiguration>(c =>
                    c.Host == "192.168.1.50" && c.Port == 6789)),
                Times.Once);
            configurationService.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task TestDownloadClientConfiguration_PrivateNetworkCaller_DoesNotRedactSecrets()
        {
            // Arrange
            var configurationService = new Mock<IConfigurationService>(MockBehavior.Strict);
            var downloadService = new Mock<IDownloadService>(MockBehavior.Strict);
            var logger = NullLogger<ConfigurationController>.Instance;
            var userService = Mock.Of<IUserService>();
            var settingsHub = Mock.Of<IHubContext<SettingsHub>>();

            var testedClient = new DownloadClientConfiguration
            {
                Id = "client-2",
                Name = "NZBGet",
                Type = "nzbget",
                Host = "192.168.1.50",
                Port = 6789,
                Username = "nzb-user",
                Password = "nzb-pass",
                UseSSL = false,
                IsEnabled = true,
                Settings = new Dictionary<string, object>
                {
                    ["apiKey"] = "very-secret-api-key"
                }
            };

            downloadService
                .Setup(x => x.TestDownloadClientAsync(It.IsAny<DownloadClientConfiguration>()))
                .ReturnsAsync((true, "Connection successful", testedClient));

            var controller = new ConfigurationController(
                configurationService.Object,
                logger,
                userService,
                settingsHub,
                downloadService.Object,
                null!);

            var httpContext = new DefaultHttpContext();
            // Simulate a trusted LAN/Synology-Docker caller.
            httpContext.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.23");
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

            var request = new DownloadClientConfiguration
            {
                Id = string.Empty,
                Name = "NZBGet",
                Type = "nzbget",
                Host = "192.168.1.50",
                Port = 6789,
                Username = "nzb-user",
                Password = "nzb-pass",
                UseSSL = false,
                IsEnabled = true,
                Settings = new Dictionary<string, object>
                {
                    ["apiKey"] = "very-secret-api-key"
                }
            };

            // Act
            var actionResult = await controller.TestDownloadClientConfiguration(request);

            // Assert
            var ok = Assert.IsType<OkObjectResult>(actionResult.Result);
            Assert.NotNull(ok.Value);
            var payload = ok.Value!;

            var successProp = payload.GetType().GetProperty("success");
            Assert.NotNull(successProp);
            Assert.True((bool)successProp!.GetValue(payload)!);

            var clientProp = payload.GetType().GetProperty("client");
            Assert.NotNull(clientProp);
            var returnedClient = Assert.IsType<DownloadClientConfiguration>(clientProp!.GetValue(payload));
            Assert.Equal("nzb-user", returnedClient.Username);
            Assert.Equal("nzb-pass", returnedClient.Password);
            Assert.True(returnedClient.Settings.TryGetValue("apiKey", out var apiKey));
            Assert.Equal("very-secret-api-key", apiKey?.ToString());

            downloadService.Verify(
                x => x.TestDownloadClientAsync(It.Is<DownloadClientConfiguration>(c =>
                    c.Host == "192.168.1.50" && c.Port == 6789)),
                Times.Once);
            configurationService.VerifyNoOtherCalls();
        }
    }
}
