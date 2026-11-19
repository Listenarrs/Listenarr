namespace Listenarr.Api.Plugins;

/// <summary>
/// Contract every Listenarr plugin assembly implements. At startup the host discovers each
/// public, parameterless-constructable implementation in <c>plugins/*.dll</c>, registers the
/// owning assembly as an MVC application part (so its controllers are routed), and invokes
/// <see cref="ConfigureServices"/>. A plugin owns its own state: it registers its own
/// DbContext and a hosted service to run its own migrations (with a distinct migrations
/// history table so it never collides with the core schema).
/// </summary>
// ponytail: contract lives in the host assembly so a plugin just references Listenarr.Api.
// Extract to a standalone Listenarr.Plugins.Abstractions package only if third-party plugins
// ever need to compile without referencing the host.
public interface IListenarrPlugin
{
    /// <summary>
    /// Register the plugin's services, DbContext, and migration runner against the host's
    /// DI container. Runs during host build, before the app starts.
    /// </summary>
    void ConfigureServices(IServiceCollection services, IConfiguration configuration);
}
