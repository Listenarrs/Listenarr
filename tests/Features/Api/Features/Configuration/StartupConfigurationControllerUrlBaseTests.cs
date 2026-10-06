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

namespace Listenarr.Tests.Features.Api.Features.Configuration;

[Trait("Name", "StartupConfigurationControllerUrlBaseTests")]
[Trait("Category", "Api")]
public sealed class StartupConfigurationControllerUrlBaseTests : BaseTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/listenarr")]
    [InlineData("listenarr")]
    [InlineData("/listenarr/audiobooks")]
    [InlineData("/https-is-not-a-scheme-here")]
    public void UrlBaseThatIsAPath_IsAccepted(string? urlBase)
    {
        Assert.True(StartupConfigurationController.IsValidUrlBase(urlBase));
    }

    [Theory]
    [InlineData("https://listenarr.example.com")]
    [InlineData("http://listenarr.example.com/listenarr")]
    [InlineData("HTTPS://LISTENARR.EXAMPLE.COM")]
    [InlineData("/https://listenarr.example.com")]
    [InlineData("  https://listenarr.example.com  ")]
    public void UrlBaseThatIsAFullUrl_IsRejected(string urlBase)
    {
        Assert.False(StartupConfigurationController.IsValidUrlBase(urlBase));
    }

    [Fact]
    public async Task SaveStartupConfig_ReturnsBadRequest_AndSavesNothing_ForAFullUrl()
    {
        // The value would be persisted and then silently ignored at startup, which reads as a
        // proxy fault rather than a rejected setting. The *arr projects refuse it here.
        // Nothing is stored yet, so there is no legacy value for the new absolute URL to match.
        var configurationService = new Mock<IConfigurationService>();
        configurationService
            .Setup(service => service.GetStartupConfigAsync())
            .ReturnsAsync(() => null!);
        var controller = BuildController(configurationService);

        var result = await controller.SaveStartupConfig(new StartupConfig
        {
            UrlBase = "https://listenarr.example.com",
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains(
            StartupConfigurationController.InvalidUrlBaseMessage,
            badRequest.Value!.ToString(),
            StringComparison.Ordinal);
        configurationService.Verify(
            service => service.SaveStartupConfigAsync(It.IsAny<StartupConfig>()),
            Times.Never);
    }

    [Fact]
    public async Task SaveStartupConfig_SavesAPathUrlBase()
    {
        var configurationService = new Mock<IConfigurationService>();
        configurationService
            .Setup(service => service.GetStartupConfigAsync())
            .ReturnsAsync(new StartupConfig { UrlBase = "/listenarr" });
        var controller = BuildController(configurationService);

        var result = await controller.SaveStartupConfig(new StartupConfig { UrlBase = "/listenarr" });

        Assert.IsType<OkObjectResult>(result.Result);
        configurationService.Verify(
            service => service.SaveStartupConfigAsync(It.Is<StartupConfig>(c => c.UrlBase == "/listenarr")),
            Times.Once);
    }

    [Fact]
    public async Task SaveStartupConfig_AcceptsAnAbsoluteUrlBaseLeftUnchanged()
    {
        // An absolute UrlBase already on disk predates ApplicationUrl and was, until that
        // setting existed, the only way to put images in outbound notifications.
        // SettingsView round-trips the whole stored config on every save, so a save that
        // leaves a legacy absolute value exactly as it was must not be refused.
        const string legacyAbsoluteUrlBase = "https://listenarr.example.com";
        var configurationService = new Mock<IConfigurationService>();
        configurationService
            .Setup(service => service.GetStartupConfigAsync())
            .ReturnsAsync(new StartupConfig { UrlBase = legacyAbsoluteUrlBase });
        var controller = BuildController(configurationService);

        var result = await controller.SaveStartupConfig(new StartupConfig { UrlBase = legacyAbsoluteUrlBase });

        Assert.IsType<OkObjectResult>(result.Result);
        configurationService.Verify(
            service => service.SaveStartupConfigAsync(It.Is<StartupConfig>(c => c.UrlBase == legacyAbsoluteUrlBase)),
            Times.Once);
    }

    [Fact]
    public async Task SaveStartupConfig_RejectsANewAbsoluteUrlBase_EvenWithALegacyValueStored()
    {
        // The legacy tolerance only covers resubmitting what is already there. Submitting a
        // different absolute value is still a new mistake and is still refused.
        var configurationService = new Mock<IConfigurationService>();
        configurationService
            .Setup(service => service.GetStartupConfigAsync())
            .ReturnsAsync(new StartupConfig { UrlBase = "https://old.listenarr.example.com" });
        var controller = BuildController(configurationService);

        var result = await controller.SaveStartupConfig(new StartupConfig
        {
            UrlBase = "https://new.listenarr.example.com",
        });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        configurationService.Verify(
            service => service.SaveStartupConfigAsync(It.IsAny<StartupConfig>()),
            Times.Never);
    }

    [Fact]
    public async Task SaveStartupConfig_AcceptsARelativeUrlBase_EvenWhenALegacyAbsoluteValueWasStored()
    {
        // Moving an install from the legacy absolute UrlBase to a path is the normal upgrade
        // path, not a "changed from one absolute value to another" case, and is not blocked.
        var configurationService = new Mock<IConfigurationService>();
        configurationService
            .Setup(service => service.GetStartupConfigAsync())
            .ReturnsAsync(new StartupConfig { UrlBase = "https://listenarr.example.com" });
        var controller = BuildController(configurationService);

        var result = await controller.SaveStartupConfig(new StartupConfig { UrlBase = "/listenarr" });

        Assert.IsType<OkObjectResult>(result.Result);
        configurationService.Verify(
            service => service.SaveStartupConfigAsync(It.Is<StartupConfig>(c => c.UrlBase == "/listenarr")),
            Times.Once);
    }

    [Theory]
    [InlineData("https://listenarr.example.com", "https://listenarr.example.com/")]
    [InlineData("https://listenarr.example.com", "HTTPS://LISTENARR.EXAMPLE.COM")]
    public async Task SaveStartupConfig_RejectsAnAbsoluteUrlBase_EvenWhenOnlyTrailingSlashOrCaseDiffers(
        string storedUrlBase,
        string submittedUrlBase)
    {
        // The legacy tolerance is a strict, literal match against what is already on disk, not a
        // normalized comparison. A trailing slash or a case change is still a different string,
        // so it is still a new value and still refused, even though it names the same site.
        var configurationService = new Mock<IConfigurationService>();
        configurationService
            .Setup(service => service.GetStartupConfigAsync())
            .ReturnsAsync(new StartupConfig { UrlBase = storedUrlBase });
        var controller = BuildController(configurationService);

        var result = await controller.SaveStartupConfig(new StartupConfig { UrlBase = submittedUrlBase });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        configurationService.Verify(
            service => service.SaveStartupConfigAsync(It.IsAny<StartupConfig>()),
            Times.Never);
    }

    private static StartupConfigurationController BuildController(Mock<IConfigurationService> configurationService)
    {
        var startupConfigService = new Mock<IStartupConfigService>();
        startupConfigService
            .Setup(service => service.NormalizeApiVersion(It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns("v1");

        return new StartupConfigurationController(
            configurationService.Object,
            startupConfigService.Object,
            NullLogger<StartupConfigurationController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }
}
