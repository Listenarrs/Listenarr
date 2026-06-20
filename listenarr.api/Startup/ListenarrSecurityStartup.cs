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

using Listenarr.Api.Middleware;
using Microsoft.AspNetCore.DataProtection;
using Serilog;

namespace Listenarr.Api.Startup;

public static class ListenarrSecurityStartup
{
    public static IServiceCollection AddListenarrSecurity(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        IFileSystem fileSystem)
    {
        var antiforgeryCookiePolicy = CookieSecurePolicy.SameAsRequest;
        var cfgPolicy = configuration["Antiforgery:Cookie:SecurePolicy"];
        if (!string.IsNullOrWhiteSpace(cfgPolicy) && Enum.TryParse<CookieSecurePolicy>(cfgPolicy, true, out var parsedPolicy))
        {
            antiforgeryCookiePolicy = parsedPolicy;
        }

        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-XSRF-TOKEN";
            options.Cookie.SecurePolicy = antiforgeryCookiePolicy;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });

        if (environment.IsProduction())
        {
            Log.Logger.Information("Antiforgery cookie SecurePolicy set to {Policy}. Ensure the app runs behind HTTPS or forwards X-Forwarded-Proto from a TLS-terminating proxy.", antiforgeryCookiePolicy);
        }

        if (environment.IsDevelopment())
        {
            services.AddAntiforgery(options =>
            {
                options.HeaderName = "X-XSRF-TOKEN";
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.Cookie.SameSite = SameSiteMode.Lax;
            });
        }

        var keyDir = Path.Join(environment.ContentRootPath, "config", "dataprotection-keys");
        if (!fileSystem.DirectoryExists(keyDir)) fileSystem.CreateDirectory(keyDir);
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keyDir))
            .SetApplicationName("Listenarr");

        return services;
    }

    public static WebApplication UseListenarrSecurityMiddleware(this WebApplication app)
    {
        app.UseMiddleware<SessionAuthenticationMiddleware>();
        app.UseMiddleware<ApiKeyMiddleware>();
        app.UseMiddleware<AuthenticationEnforcerMiddleware>();
        app.UseMiddleware<AntiforgeryValidationMiddleware>();
        return app;
    }
}
