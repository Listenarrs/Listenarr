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

namespace Listenarr.Api.Startup;

public static class ListenarrUrlBaseStartup
{
    /// <summary>
    /// Applies the configured <c>UrlBase</c> as the request path base, so a reverse proxy can
    /// forward an un-rewritten sub-path (for example <c>/example/api/v1/system/info</c>) and
    /// have it routed as <c>/api/v1/system/info</c>.
    /// </summary>
    /// <remarks>
    /// Proxies that strip the prefix themselves should send <c>X-Forwarded-Prefix</c> instead;
    /// that path is handled by the forwarded headers middleware and needs no configuration here.
    /// </remarks>
    public static WebApplication UseListenarrUrlBase(this WebApplication app)
    {
        var configuredUrlBase = app.Services
            .GetRequiredService<IStartupConfigService>()
            .GetConfig()?
            .UrlBase;

        var urlBase = NormalizeUrlBase(configuredUrlBase);
        if (urlBase is null)
        {
            return app;
        }

        app.Logger.LogInformation("Serving Listenarr under URL base {UrlBase}", urlBase);

        var pathBase = new PathString(urlBase);
        app.Use(async (context, next) =>
        {
            ApplyUrlBase(context, pathBase);
            await next(context);
        });

        return app;
    }

    /// <summary>
    /// Moves the configured base off the request path and onto <c>PathBase</c>.
    /// </summary>
    /// <remarks>
    /// This is <c>UsePathBase</c> with one difference: it assigns <c>PathBase</c> rather than
    /// appending to it. A proxy that sets <c>X-Forwarded-Prefix</c> and also forwards the path
    /// un-rewritten would otherwise end up with the prefix twice, because the forwarded headers
    /// middleware has already set <c>PathBase</c> from the header by the time this runs. Routing
    /// survives a doubled value, since the path ends up right either way, but anything derived
    /// from <c>PathBase</c> does not: the antiforgery cookie takes its <c>Path</c> attribute from
    /// it when <c>AntiforgeryOptions.Cookie.Path</c> is left null, which it is.
    /// A request that does not carry the prefix is left alone, so a direct hit on the container's
    /// own port keeps working.
    /// </remarks>
    internal static void ApplyUrlBase(HttpContext context, PathString urlBase)
    {
        var request = context.Request;
        if (!request.Path.StartsWithSegments(urlBase, StringComparison.OrdinalIgnoreCase, out var remaining))
        {
            return;
        }

        request.Path = remaining;
        request.PathBase = urlBase;
    }

    /// <summary>
    /// Reduces a configured URL base to a leading-slash path with no trailing slash, or null when
    /// it names the site root or cannot be used as a path base.
    /// </summary>
    internal static string? NormalizeUrlBase(string? configuredUrlBase)
    {
        var candidate = (configuredUrlBase ?? string.Empty).Trim();

        if (candidate.Length == 0 ||
            candidate.Contains("://", StringComparison.Ordinal) ||
            candidate.Contains('\\', StringComparison.Ordinal))
        {
            return null;
        }

        var segments = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            return null;
        }

        return $"/{string.Join('/', segments)}";
    }
}
