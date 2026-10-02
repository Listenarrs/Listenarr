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
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Notifications.Payloads;

[Trait("Name", "NotificationPayloadContextResolverTests")]
[Trait("Category", "Application")]
public sealed class NotificationPayloadContextResolverTests : BaseTests
{
    private const string ExternalUrl = "https://listenarr.example.com";
    private const string ProxyPath = "/listenarr";

    private static IConfigurationService ConfigWith(string? applicationUrl, string? urlBase)
    {
        var mock = new Mock<IConfigurationService>();
        mock.Setup(x => x.GetStartupConfigAsync())
            .ReturnsAsync(new StartupConfig { ApplicationUrl = applicationUrl, UrlBase = urlBase });
        return mock.Object;
    }

    private static IRequestContextAccessor RequestFrom(string scheme, string host)
    {
        var mock = new Mock<IRequestContextAccessor>();
        mock.SetupGet(x => x.Current)
            .Returns(new RequestContextSnapshot("/api/v1/notifications", scheme, host, null, false));
        return mock.Object;
    }

    /// <summary>
    /// Reads one variable and nothing else, so a value left in the real environment by another
    /// test cannot decide the outcome here.
    /// </summary>
    private static Func<string, string?> EnvironmentWith(string? publicUrl) =>
        name => name == NotificationPayloadContextResolver.PublicUrlVariable ? publicUrl : null;

    private static Func<string, string?> EmptyEnvironment() => EnvironmentWith(null);

    [Fact]
    public async Task ApplicationUrl_IsUsedAsTheNotificationBase()
    {
        var context = await NotificationPayloadContextResolver.ResolveAsync(
            ConfigWith(applicationUrl: ExternalUrl, urlBase: ProxyPath),
            requestContextAccessor: null,
            NullLogger.Instance,
            validateImageBaseUrl: true,
            EmptyEnvironment());

        Assert.Equal(ExternalUrl, context.BaseUrl);
    }

    [Fact]
    public async Task PathUrlBase_DoesNotBecomeTheNotificationBase()
    {
        // The whole point of the split: a UrlBase set for sub-path serving must not be
        // mistaken for an external URL.
        var context = await NotificationPayloadContextResolver.ResolveAsync(
            ConfigWith(applicationUrl: null, urlBase: ProxyPath),
            requestContextAccessor: null,
            NullLogger.Instance,
            validateImageBaseUrl: true,
            EmptyEnvironment());

        Assert.Null(context.BaseUrl);
    }

    [Fact]
    public async Task PathUrlBase_NoLongerBlocksTheRequestContextFallback()
    {
        // Before the split this returned null: the path was taken as the base, which
        // suppressed the fallback, and then failed validation. Images were dropped.
        var context = await NotificationPayloadContextResolver.ResolveAsync(
            ConfigWith(applicationUrl: null, urlBase: ProxyPath),
            RequestFrom("https", "listenarr.example.com"),
            NullLogger.Instance,
            validateImageBaseUrl: true,
            EmptyEnvironment());

        Assert.Equal(ExternalUrl, context.BaseUrl);
    }

    [Fact]
    public async Task AbsoluteUrlBase_IsStillHonoured_WhenApplicationUrlIsUnset()
    {
        // Installations configured before ApplicationUrl existed keep working.
        var logger = new Mock<ILogger>();

        var context = await NotificationPayloadContextResolver.ResolveAsync(
            ConfigWith(applicationUrl: null, urlBase: ExternalUrl),
            requestContextAccessor: null,
            logger.Object,
            validateImageBaseUrl: true,
            EmptyEnvironment());

        Assert.Equal(ExternalUrl, context.BaseUrl);
        Assert.Contains(logger.Invocations, i => i.ToString()!.Contains("ApplicationUrl", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ApplicationUrl_TakesPrecedence_OverAnAbsoluteUrlBase()
    {
        var context = await NotificationPayloadContextResolver.ResolveAsync(
            ConfigWith(applicationUrl: ExternalUrl, urlBase: "https://stale.example.com"),
            requestContextAccessor: null,
            NullLogger.Instance,
            validateImageBaseUrl: true,
            EmptyEnvironment());

        Assert.Equal(ExternalUrl, context.BaseUrl);
    }

    [Fact]
    public async Task ApplicationUrl_ThatIsNotAbsolute_IsRejected_WhenValidating()
    {
        var context = await NotificationPayloadContextResolver.ResolveAsync(
            ConfigWith(applicationUrl: "listenarr.example.com", urlBase: null),
            requestContextAccessor: null,
            NullLogger.Instance,
            validateImageBaseUrl: true,
            EmptyEnvironment());

        Assert.Null(context.BaseUrl);
    }

    [Fact]
    public async Task PublicUrlVariable_IsUsedAsTheNotificationBase()
    {
        // DiscordBotService.GetListenarrUrl() reads this variable first. The payload resolver
        // never looked at it, so the two notification paths disagreed about the same URL.
        var context = await NotificationPayloadContextResolver.ResolveAsync(
            ConfigWith(applicationUrl: null, urlBase: ProxyPath),
            RequestFrom("http", "container.internal"),
            NullLogger.Instance,
            validateImageBaseUrl: true,
            EnvironmentWith(ExternalUrl));

        Assert.Equal(ExternalUrl, context.BaseUrl);
    }

    [Fact]
    public async Task PublicUrlVariable_TakesPrecedence_OverApplicationUrl()
    {
        var context = await NotificationPayloadContextResolver.ResolveAsync(
            ConfigWith(applicationUrl: "https://stale.example.com", urlBase: null),
            requestContextAccessor: null,
            NullLogger.Instance,
            validateImageBaseUrl: true,
            EnvironmentWith(ExternalUrl));

        Assert.Equal(ExternalUrl, context.BaseUrl);
    }

    [Fact]
    public async Task PublicUrlVariable_LosesItsTrailingSlash()
    {
        // The Discord bot trims one too, so a variable set with a trailing slash produces the
        // same base on both paths rather than a doubled slash on one of them.
        var context = await NotificationPayloadContextResolver.ResolveAsync(
            ConfigWith(applicationUrl: null, urlBase: null),
            requestContextAccessor: null,
            NullLogger.Instance,
            validateImageBaseUrl: true,
            EnvironmentWith(ExternalUrl + "/"));

        Assert.Equal(ExternalUrl, context.BaseUrl);
    }

    [Fact]
    public async Task BlankPublicUrlVariable_FallsThroughToApplicationUrl()
    {
        // An empty variable is how Docker renders an unset one in a compose file, so it must not
        // count as a configured value.
        var context = await NotificationPayloadContextResolver.ResolveAsync(
            ConfigWith(applicationUrl: ExternalUrl, urlBase: null),
            requestContextAccessor: null,
            NullLogger.Instance,
            validateImageBaseUrl: true,
            EnvironmentWith("   "));

        Assert.Equal(ExternalUrl, context.BaseUrl);
    }

    [Fact]
    public async Task PublicUrlVariable_ThatIsNotAbsolute_IsRejected_AndNamedInTheWarning()
    {
        var logger = new Mock<ILogger>();

        var context = await NotificationPayloadContextResolver.ResolveAsync(
            ConfigWith(applicationUrl: null, urlBase: null),
            requestContextAccessor: null,
            logger.Object,
            validateImageBaseUrl: true,
            EnvironmentWith("listenarr.example.com"));

        Assert.Null(context.BaseUrl);
        Assert.Contains(
            logger.Invocations,
            i => i.ToString()!.Contains(NotificationPayloadContextResolver.PublicUrlVariable, StringComparison.Ordinal));
    }
}
