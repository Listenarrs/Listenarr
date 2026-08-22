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
using Listenarr.Api.Startup;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Listenarr.Tests.Features.Api.Startup;

[Trait("Name", "ListenarrUrlBaseStartupTests")]
[Trait("Category", "Api")]
public sealed class ListenarrUrlBaseStartupTests : BaseTests
{
    [Theory]
    [InlineData("/example", "/example")]
    [InlineData("example", "/example")]
    [InlineData("/example/", "/example")]
    [InlineData("  /example/audiobooks/  ", "/example/audiobooks")]
    public void NormalizeUrlBase_ProducesALeadingSlashPathWithoutATrailingSlash(string configured, string expected)
    {
        Assert.Equal(expected, ListenarrUrlBaseStartup.NormalizeUrlBase(configured));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData("///")]
    [InlineData("https://listenarr.example.com/example")]
    [InlineData("/example/../..")]
    [InlineData("\\example")]
    public void NormalizeUrlBase_ReturnsNull_ForRootOrUnusableValues(string? configured)
    {
        Assert.Null(ListenarrUrlBaseStartup.NormalizeUrlBase(configured));
    }

    [Fact]
    public void ForwardedHeaders_SetPathBaseFromForwardedPrefix_ForAProxyThatStripsThePrefix()
    {
        var options = new ServiceCollection()
            .AddListenarrReverseProxyHeaders()
            .BuildServiceProvider()
            .GetRequiredService<IOptions<ForwardedHeadersOptions>>()
            .Value;

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("fc00::1");
        context.Request.Path = "/api/v1/system/info";
        context.Request.Headers["X-Forwarded-Prefix"] = "/example";

        var middleware = new ForwardedHeadersMiddleware(
            _ => Task.CompletedTask,
            NullLoggerFactory.Instance,
            Options.Create(options));
        middleware.ApplyForwarders(context);

        Assert.Equal("/example", context.Request.PathBase.Value);
        Assert.Equal("/api/v1/system/info", context.Request.Path.Value);
    }

    [Fact]
    public async Task ConfiguredUrlBase_RoutesRequestsThatArriveWithThePrefixStillAttached()
    {
        using var factory = new ListenarrWebApplicationFactory();
        using var withUrlBase = CreateFactory(factory, "/example");
        using var withoutUrlBase = CreateFactory(factory, "/");
        var apiBase = TestUtils.ResolveApiBasePath(withUrlBase.Services);

        using var configuredClient = CreateClient(withUrlBase);
        using var defaultClient = CreateClient(withoutUrlBase);

        var configured = await configuredClient.GetAsync($"/example{apiBase}/system/info");
        var unconfigured = await defaultClient.GetAsync($"/example{apiBase}/system/info");
        var atSiteRoot = await configuredClient.GetAsync($"{apiBase}/system/info");

        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, unconfigured.StatusCode);

        // UsePathBase strips the prefix when it is present and leaves the request alone when it is
        // not, so a direct hit on the container keeps working alongside the proxied sub-path.
        Assert.Equal(HttpStatusCode.OK, atSiteRoot.StatusCode);
    }

    [Fact]
    public void ForwardedHeaders_IgnoreForwardedPrefix_FromAnAddressOutsideTheTrustedNetworks()
    {
        var options = new ServiceCollection()
            .AddListenarrReverseProxyHeaders()
            .BuildServiceProvider()
            .GetRequiredService<IOptions<ForwardedHeadersOptions>>()
            .Value;

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
        context.Request.Path = "/api/v1/system/info";
        context.Request.Headers["X-Forwarded-Prefix"] = "/spoofed";

        var middleware = new ForwardedHeadersMiddleware(
            _ => Task.CompletedTask,
            NullLoggerFactory.Instance,
            Options.Create(options));
        middleware.ApplyForwarders(context);

        Assert.Equal(string.Empty, context.Request.PathBase.Value);
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory)
    {
        return factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
    }

    private static WebApplicationFactory<Program> CreateFactory(
        ListenarrWebApplicationFactory factory,
        string urlBase)
    {
        return factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IStartupConfigService>(_ =>
                    new StartupConfigServiceMock(new StartupConfig
                    {
                        AuthenticationRequired = "false",
                        UrlBase = urlBase,
                    }));
            });
        });
    }
}
