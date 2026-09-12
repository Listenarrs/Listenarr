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
        var configurationService = new Mock<IConfigurationService>();
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
