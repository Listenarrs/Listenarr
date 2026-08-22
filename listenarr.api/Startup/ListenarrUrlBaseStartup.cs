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
        app.UsePathBase(urlBase);

        return app;
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
