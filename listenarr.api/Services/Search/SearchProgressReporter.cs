using Listenarr.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Listenarr.Api.Services.Search;

/// <summary>
/// Handles broadcasting search progress updates to connected clients via SignalR.
/// </summary>
public class SearchProgressReporter
{
    private readonly IHubContext<DownloadHub>? _hubContext;
    private readonly ILogger<SearchProgressReporter> _logger;

    public SearchProgressReporter(IHubContext<DownloadHub>? hubContext, ILogger<SearchProgressReporter> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <summary>
    /// Broadcasts a search progress message to all connected SignalR clients.
    /// </summary>
    /// <param name="message">The progress message to broadcast</param>
    /// <param name="asin">Optional ASIN associated with this progress update</param>
    public async Task BroadcastAsync(string message, string? asin = null)
    {
        try
        {
            if (_hubContext != null)
            {
                // Structured payload: include a type so clients can distinguish interactive vs automatic
                await _hubContext.Clients.All.SendAsync("SearchProgress", new { message, asin, type = "interactive" });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
            _logger.LogDebug(ex, "Failed to broadcast SearchProgress: {Message}", ex.Message);
        }
    }
}

