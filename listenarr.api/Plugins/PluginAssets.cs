using System.Text.Json;
using Microsoft.Extensions.FileProviders;

namespace Listenarr.Api.Plugins;

/// <summary>
/// Serves runtime-installable plugin frontends. Each plugin package is a folder under the
/// plugins directory containing <c>plugin.json</c> and a <c>ui/</c> folder with its prebuilt
/// bundle. This exposes a manifest (<c>GET /plugins/manifest</c>) the host frontend fetches at
/// startup, and static-serves each plugin's <c>ui/</c> assets. Generic; knows no specific plugin.
/// </summary>
public static class PluginAssets
{
    private sealed record RawManifest(string? Id, string? Name, string? Version, string? Frontend, string[]? Styles);

    public static void UseListenarrPluginAssets(this WebApplication app, string pluginsDir)
    {
        if (!Directory.Exists(pluginsDir))
        {
            return;
        }

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var loaded = new List<RawManifest>();

        foreach (var dir in Directory.GetDirectories(pluginsDir))
        {
            var manifestPath = Path.Combine(dir, "plugin.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            RawManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<RawManifest>(File.ReadAllText(manifestPath), options);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[plugins] bad plugin.json in {Path.GetFileName(dir)}: {ex.Message}");
                continue;
            }

            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
            {
                continue;
            }

            var uiDir = Path.Combine(dir, "ui");
            if (Directory.Exists(uiDir))
            {
                // Only the ui/ folder is web-exposed — never the dll.
                app.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = new PhysicalFileProvider(uiDir),
                    RequestPath = $"/plugins/{manifest.Id}/ui",
                });
            }

            loaded.Add(manifest);
        }

        app.MapGet("/plugins/manifest", (HttpContext ctx) =>
        {
            var basePath = ctx.Request.PathBase.HasValue ? ctx.Request.PathBase.Value : string.Empty;
            var result = loaded.Select(m => new
            {
                id = m.Id,
                name = m.Name ?? m.Id,
                version = m.Version ?? "0.0.0",
                frontend = string.IsNullOrWhiteSpace(m.Frontend) ? null : $"{basePath}/plugins/{m.Id}/ui/{m.Frontend}",
                styles = (m.Styles ?? []).Select(s => $"{basePath}/plugins/{m.Id}/ui/{s}").ToArray(),
            });
            return Results.Json(result);
        });

        Console.WriteLine($"[plugins] serving {loaded.Count} plugin UI package(s)");
    }
}
