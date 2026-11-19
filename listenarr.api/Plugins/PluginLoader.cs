using System.Reflection;

namespace Listenarr.Api.Plugins;

/// <summary>
/// Generic, plugin-agnostic loader. Scans a directory for plugin assemblies, registers their
/// controllers as MVC application parts, and lets each plugin configure its own services.
/// Knows nothing about any specific plugin.
/// </summary>
public static class PluginLoader
{
    /// <summary>
    /// Load plugins from <paramref name="pluginsDir"/>. Safe no-op when the directory is
    /// absent or contains no plugin assemblies, so a stock install behaves exactly as before.
    /// </summary>
    public static void AddListenarrPlugins(this WebApplicationBuilder builder, string pluginsDir)
    {
        if (!Directory.Exists(pluginsDir))
        {
            return;
        }

        var assemblies = new List<Assembly>();
        var plugins = new List<IListenarrPlugin>();

        // Support both a flat layout (plugins/*.dll) and self-contained packages
        // (plugins/<id>/<id>.dll alongside plugin.json + ui/ for runtime-installable plugins).
        var dlls = Directory.GetFiles(pluginsDir, "*.dll")
            .Concat(Directory.GetDirectories(pluginsDir).SelectMany(d => Directory.GetFiles(d, "*.dll")));

        foreach (var dll in dlls)
        {
            Assembly assembly;
            try
            {
                assembly = Assembly.LoadFrom(dll);
            }
            catch (Exception ex)
            {
                // A non-plugin dll (a dependency, a native lib) is expected here — skip it.
                Console.Error.WriteLine($"[plugins] skipped {Path.GetFileName(dll)}: {ex.Message}");
                continue;
            }

            Type[] exported;
            try
            {
                exported = assembly.GetExportedTypes();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[plugins] could not inspect {Path.GetFileName(dll)}: {ex.Message}");
                continue;
            }

            var found = false;
            foreach (var type in exported)
            {
                if (typeof(IListenarrPlugin).IsAssignableFrom(type)
                    && type is { IsAbstract: false, IsInterface: false }
                    && type.GetConstructor(Type.EmptyTypes) is not null)
                {
                    plugins.Add((IListenarrPlugin)Activator.CreateInstance(type)!);
                    found = true;
                }
            }

            if (found)
            {
                assemblies.Add(assembly);
            }
        }

        if (plugins.Count == 0)
        {
            return;
        }

        // Calling AddControllers again returns a builder over the shared ApplicationPartManager;
        // adding parts here makes the plugins' controllers discoverable.
        var mvc = builder.Services.AddControllers();
        foreach (var assembly in assemblies)
        {
            mvc.AddApplicationPart(assembly);
        }

        foreach (var plugin in plugins)
        {
            plugin.ConfigureServices(builder.Services, builder.Configuration);
        }

        Console.WriteLine($"[plugins] loaded {plugins.Count} plugin(s) from {assemblies.Count} assembly(ies) in {pluginsDir}");
    }
}
