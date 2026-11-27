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

using Serilog;

namespace Listenarr.Api.Startup;

public static class ListenarrStaticAssetsStartup
{
    public static WebApplication MapListenarrStaticAssets(this WebApplication app)
    {
        var fileSystem = app.Services.GetRequiredService<IFileSystem>();
        var frontendPlaceholderPath = Path.Join(app.Environment.ContentRootPath, "..", "fe", "public", "placeholder.svg");
        app.MapGet("/placeholder.svg", async context =>
        {
            try
            {
                if (fileSystem.FileExists(frontendPlaceholderPath))
                {
                    context.Response.ContentType = "image/svg+xml";
                    context.Response.Headers.CacheControl = "public, max-age=300";
                    await context.Response.SendFileAsync(frontendPlaceholderPath);
                    return;
                }

                var fallback = Path.Join(app.Environment.ContentRootPath, "wwwroot", "placeholder.svg");
                if (fileSystem.FileExists(fallback))
                {
                    context.Response.ContentType = "image/svg+xml";
                    context.Response.Headers.CacheControl = "public, max-age=300";
                    await context.Response.SendFileAsync(fallback);
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status404NotFound;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                Log.Logger.Debug(ex, "Failed to serve fallback placeholder image");
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }
        });

        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx =>
            {
                // Vite emits content-hashed filenames under /assets/ — safe to
                // cache forever; a new build means a new URL. index.html (and
                // anything un-hashed) must revalidate every load or a deploy
                // can leave browsers on a stale SPA shell pointing at purged
                // asset hashes (ETag makes the revalidation a 304).
                if (ctx.Context.Request.Path.StartsWithSegments("/assets"))
                {
                    ctx.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                }
                else
                {
                    ctx.Context.Response.Headers.CacheControl = "no-cache";
                }
            }
        });

        var cacheImagesPath = Path.Join(app.Environment.ContentRootPath, "config", "cache", "images");
        if (fileSystem.DirectoryExists(cacheImagesPath))
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(cacheImagesPath),
                RequestPath = "/config/cache/images",
                OnPrepareResponse = ctx =>
                {
                    // Direct-path consumers of the image cache get the same
                    // long-lived policy as the /images API (see
                    // ImageResponseBuilder); the middleware's ETag covers
                    // post-expiry revalidation.
                    ctx.Context.Response.Headers.CacheControl = "public, max-age=604800";
                }
            });
        }

        return app;
    }
}
