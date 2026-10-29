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
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace Listenarr.Tests.Features.Api.Features.Configuration
{
    [Trait("Area", "ConfigurationApi")]
    [Trait("Name", "StartupConfigMergeTests")]
    [Trait("Category", "StartupConfigurationController")]
    public class StartupConfigMergeTests : BaseTests
    {
        private static StartupConfig OnDisk() => new()
        {
            LogLevel = "Information",
            EnableSsl = true,
            Port = 5000,
            SslPort = 6868,
            UrlBase = "/listenarr",
            BindAddress = "*",
            ApiKey = "on-disk-api-key",
            UpdateMechanism = "BuiltIn",
            LaunchBrowser = true,
            Branch = "main",
            InstanceName = "Listenarr",
            SyslogPort = 514,
            AnalyticsEnabled = false,
            ApiVersion = "1",
            AuthenticationRequired = "false",
            SslCertPath = "/certs/listenarr.pfx",
            SslCertPassword = "on-disk-cert-password",
            Ffmpeg = new FfmpegConfig
            {
                Provider = "gyan",
                ReleaseOverride = "6.0",
                ChecksumUrl = "https://example.invalid/sha256",
                Arch = "x86_64",
            },
        };

        private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement.Clone();

        [Fact]
        public void ApplyTo_PartialBody_KeepsEveryPropertyTheBodyOmits()
        {
            var merged = StartupConfigPatchReader.ApplyTo(
                OnDisk(),
                Body("""{"authenticationRequired":"true"}"""));

            Assert.Equal("true", merged.AuthenticationRequired);
            Assert.Equal("on-disk-api-key", merged.ApiKey);
            Assert.Equal("*", merged.BindAddress);
            Assert.Equal(5000, merged.Port);
            Assert.Equal(6868, merged.SslPort);
            Assert.True(merged.EnableSsl);
            Assert.Equal("/certs/listenarr.pfx", merged.SslCertPath);
            Assert.Equal("on-disk-cert-password", merged.SslCertPassword);
            Assert.Equal("Information", merged.LogLevel);
            Assert.Equal("/listenarr", merged.UrlBase);
            Assert.Equal(514, merged.SyslogPort);
            Assert.Equal("gyan", merged.Ffmpeg?.Provider);
        }

        [Fact]
        public void ApplyTo_EmptyStringIsAValue_NotAnOmission()
        {
            // The control for the test above: if the merge just ignored everything it
            // could not tell apart from a default, this would silently keep the old
            // values and the endpoint would have become impossible to clear a field with.
            var merged = StartupConfigPatchReader.ApplyTo(
                OnDisk(),
                Body("""{"urlBase":"","sslCertPath":"","instanceName":""}"""));

            Assert.Equal(string.Empty, merged.UrlBase);
            Assert.Equal(string.Empty, merged.SslCertPath);
            Assert.Equal(string.Empty, merged.InstanceName);
            Assert.Equal("on-disk-api-key", merged.ApiKey);
            Assert.Equal(5000, merged.Port);
        }

        [Fact]
        public void ApplyTo_ExplicitNullClearsTheProperty()
        {
            var merged = StartupConfigPatchReader.ApplyTo(
                OnDisk(),
                Body("""{"syslogPort":null,"sslCertPassword":null}"""));

            Assert.Null(merged.SyslogPort);
            Assert.Null(merged.SslCertPassword);
            Assert.Equal("/certs/listenarr.pfx", merged.SslCertPath);
            Assert.Equal("on-disk-api-key", merged.ApiKey);
        }

        [Fact]
        public void ApplyTo_ZeroAndFalseAreValues_NotOmissions()
        {
            var merged = StartupConfigPatchReader.ApplyTo(
                OnDisk(),
                Body("""{"syslogPort":0,"enableSsl":false,"launchBrowser":false}"""));

            Assert.Equal(0, merged.SyslogPort);
            Assert.False(merged.EnableSsl);
            Assert.False(merged.LaunchBrowser);
            Assert.Equal("on-disk-api-key", merged.ApiKey);
        }

        [Fact]
        public void ApplyTo_FullBody_StillReplacesEveryProperty()
        {
            var replacement = new StartupConfig
            {
                LogLevel = "Debug",
                EnableSsl = false,
                Port = 7878,
                SslPort = 7879,
                UrlBase = "/",
                BindAddress = "127.0.0.1",
                ApiKey = "replacement-api-key",
                UpdateMechanism = "Docker",
                LaunchBrowser = false,
                Branch = "develop",
                InstanceName = "Replacement",
                SyslogPort = 1514,
                AnalyticsEnabled = true,
                ApiVersion = "2",
                AuthenticationRequired = "true",
                SslCertPath = "/certs/replacement.pfx",
                SslCertPassword = "replacement-cert-password",
                Ffmpeg = new FfmpegConfig
                {
                    Provider = "johnvansickle",
                    ReleaseOverride = "7.0",
                    ChecksumUrl = "https://example.invalid/other",
                    Arch = "arm64",
                },
            };

            var merged = StartupConfigPatchReader.ApplyTo(
                OnDisk(),
                JsonSerializer.SerializeToElement(replacement));

            // Comparing the serialized form asserts every property, including any added
            // later, rather than the handful this test would otherwise remember to name.
            Assert.Equal(JsonSerializer.Serialize(replacement), JsonSerializer.Serialize(merged));
        }

        [Fact]
        public void ApplyTo_NestedFfmpegBody_KeepsTheSiblingsItOmits()
        {
            var merged = StartupConfigPatchReader.ApplyTo(
                OnDisk(),
                Body("""{"ffmpeg":{"arch":"arm64"}}"""));

            Assert.Equal("arm64", merged.Ffmpeg?.Arch);
            Assert.Equal("gyan", merged.Ffmpeg?.Provider);
            Assert.Equal("6.0", merged.Ffmpeg?.ReleaseOverride);
            Assert.Equal("https://example.invalid/sha256", merged.Ffmpeg?.ChecksumUrl);
        }

        [Fact]
        public void ApplyTo_DoesNotMutateTheConfigItWasGiven()
        {
            var onDisk = OnDisk();

            var merged = StartupConfigPatchReader.ApplyTo(
                onDisk,
                Body("""{"port":9999,"ffmpeg":{"arch":"arm64"}}"""));

            Assert.Equal(9999, merged.Port);
            Assert.Equal(5000, onDisk.Port);
            Assert.Equal("x86_64", onDisk.Ffmpeg?.Arch);
        }

        [Fact]
        public void ApplyTo_NumericStringForNumericProperty_IsAccepted()
        {
            var merged = StartupConfigPatchReader.ApplyTo(
                OnDisk(),
                Body("""{"port":"8080"}"""));

            Assert.Equal(8080, merged.Port);
            Assert.Equal("on-disk-api-key", merged.ApiKey);
        }

        [Fact]
        public void ApplyTo_BodyThatIsNotAnObject_Throws()
        {
            Assert.Throws<ArgumentException>(
                () => StartupConfigPatchReader.ApplyTo(OnDisk(), Body("[]")));
        }

        [Fact]
        public async Task SaveStartupConfig_PartialBody_SavesTheMergedConfig()
        {
            StartupConfig? saved = null;
            var configurationService = new Mock<IConfigurationService>();
            configurationService
                .Setup(service => service.GetStartupConfigAsync())
                .ReturnsAsync(() => saved ?? OnDisk());
            configurationService
                .Setup(service => service.SaveStartupConfigAsync(It.IsAny<StartupConfig>()))
                .Callback<StartupConfig>(config => saved = config)
                .Returns(Task.CompletedTask);

            var controller = BuildController(configurationService);

            var result = await controller.SaveStartupConfig(
                Body("""{"authenticationRequired":"true","port":8686}"""));

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var payload = Assert.IsType<StartupConfig>(ok.Value);
            Assert.Equal("true", payload.AuthenticationRequired);
            Assert.Equal(8686, payload.Port);
            Assert.Equal("on-disk-api-key", payload.ApiKey);
            Assert.Equal("*", payload.BindAddress);
            Assert.Equal("/certs/listenarr.pfx", payload.SslCertPath);

            Assert.NotNull(saved);
            Assert.Equal("on-disk-api-key", saved!.ApiKey);
            Assert.Equal(6868, saved.SslPort);
            Assert.Equal("gyan", saved.Ffmpeg?.Provider);
        }

        [Fact]
        public async Task SaveStartupConfig_BodyThatIsNotAnObject_IsRejectedWithoutSaving()
        {
            var configurationService = new Mock<IConfigurationService>();
            configurationService
                .Setup(service => service.GetStartupConfigAsync())
                .ReturnsAsync(OnDisk());

            var controller = BuildController(configurationService);

            var result = await controller.SaveStartupConfig(Body("\"not-an-object\""));

            Assert.IsType<BadRequestObjectResult>(result.Result);
            configurationService.Verify(
                service => service.SaveStartupConfigAsync(It.IsAny<StartupConfig>()),
                Times.Never);
        }

        [Fact]
        public async Task SaveStartupConfig_BodyWithAMistypedProperty_IsRejectedWithoutSaving()
        {
            var configurationService = new Mock<IConfigurationService>();
            configurationService
                .Setup(service => service.GetStartupConfigAsync())
                .ReturnsAsync(OnDisk());

            var controller = BuildController(configurationService);

            var result = await controller.SaveStartupConfig(Body("""{"port":"not-a-number"}"""));

            Assert.IsType<BadRequestObjectResult>(result.Result);
            configurationService.Verify(
                service => service.SaveStartupConfigAsync(It.IsAny<StartupConfig>()),
                Times.Never);
        }

        private static StartupConfigurationController BuildController(
            Mock<IConfigurationService> configurationService)
        {
            var startupConfigService = new Mock<IStartupConfigService>();
            startupConfigService
                .Setup(service => service.NormalizeApiVersion(It.IsAny<string?>(), It.IsAny<string?>()))
                .Returns<string?, string?>((configured, _) => configured ?? "1");

            return new StartupConfigurationController(
                configurationService.Object,
                startupConfigService.Object,
                NullLogger<StartupConfigurationController>.Instance)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext(),
                },
            };
        }
    }
}
