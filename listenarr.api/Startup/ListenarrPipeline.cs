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

using Asp.Versioning.ApiExplorer;
using Listenarr.Api.Middleware;
namespace Listenarr.Api.Startup;

public static class ListenarrPipeline
{
    public static WebApplication UseListenarrRequestPipeline(
        this WebApplication app,
        Action<IEndpointRouteBuilder> mapRealtimeHubs)
    {
        app.UseListenarrSwaggerUi();
        app.UseMiddleware<RequestTelemetryMiddleware>();
        app.UseListenarrExceptionHandler();
        app.UseForwardedHeaders();
        app.UseListenarrUrlBase();
        app.MapListenarrStaticAssets();
        app.UseRouting();
        app.UseMiddleware<RequestBodyLoggingMiddleware>();
        app.UseListenarrDevelopmentCors();
        app.UseListenarrSecurityMiddleware();
        app.UseAuthorization();
        app.MapControllers();
        mapRealtimeHubs(app);
        app.MapFallbackToFile("index.html");

        return app;
    }

    private static void UseListenarrSwaggerUi(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
        {
            return;
        }

        var apiVersionDescriptionProvider = app.Services.GetRequiredService<IApiVersionDescriptionProvider>();
        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            foreach (var description in apiVersionDescriptionProvider.ApiVersionDescriptions)
            {
                options.SwaggerEndpoint(
                    $"/swagger/{description.GroupName}/swagger.json",
                    $"Listenarr API {description.GroupName.ToUpperInvariant()}");
            }
        });
    }

    private static void UseListenarrExceptionHandler(this WebApplication app)
    {
        app.UseExceptionHandler();
    }

    private static void UseListenarrDevelopmentCors(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.UseCors("DevOnly");
        }
    }
}
