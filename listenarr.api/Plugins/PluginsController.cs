using Listenarr.Api.Attributes;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Plugins;

/// <summary>
/// Runtime plugin management API. Lets an admin add plugin repositories and
/// install / update / uninstall plugins. Install and uninstall touch the filesystem and then
/// restart the app (the dll only (un)loads at startup); Docker's restart policy relaunches it.
///
/// Trust model: installing from a repository URL downloads and runs arbitrary code — identical to
/// *arr custom repositories. Admin/local-network only, hence <see cref="RequireAdminOrApiKey"/>.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/plugins")]
[RequireAdminOrApiKey]
public sealed class PluginsController : ControllerBase
{
    private readonly PluginManager _manager;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<PluginsController> _logger;

    public PluginsController(PluginManager manager, IHostApplicationLifetime lifetime, ILogger<PluginsController> logger)
    {
        _manager = manager;
        _lifetime = lifetime;
        _logger = logger;
    }

    public sealed record RepoBody(string Url);
    public sealed record InstallBody(string Id);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await _manager.ListAsync(ct));

    [HttpGet("repositories")]
    public IActionResult ListRepositories() => Ok(_manager.GetRepositories());

    [HttpPost("repositories")]
    public async Task<IActionResult> AddRepository([FromBody] RepoBody body, CancellationToken ct)
    {
        try
        {
            await _manager.AddRepositoryAsync(body.Url, ct);
            return Ok(_manager.GetRepositories());
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("repositories")]
    public IActionResult RemoveRepository([FromQuery] string url)
    {
        _manager.RemoveRepository(url);
        return Ok(_manager.GetRepositories());
    }

    [HttpPost("install")]
    public async Task<IActionResult> Install([FromBody] InstallBody body, CancellationToken ct)
    {
        try
        {
            await _manager.InstallAsync(body.Id, ct);
            ScheduleRestart();
            return Ok(new { restarting = true });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("{id}")]
    public IActionResult Uninstall(string id)
    {
        try
        {
            _manager.Uninstall(id);
            ScheduleRestart();
            return Ok(new { restarting = true });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    // Respond first, then stop the host shortly after so the dll (un)loads on the next boot.
    private void ScheduleRestart()
    {
        _logger.LogInformation("Plugin change applied — restarting to load.");
        _ = Task.Run(async () =>
        {
            await Task.Delay(700);
            _lifetime.StopApplication();
        });
    }
}
