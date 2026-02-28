using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace Listenarr.Api.Services
{
    /// <summary>
    /// Background service that ensures ffprobe is installed without blocking application startup.
    /// It will attempt installation once and broadcast a SignalR message when finished.
    /// </summary>
    public class FfmpegInstallBackgroundService : BackgroundService
    {
        private readonly IFfmpegService _ffmpegService;
        private readonly IHubContext<Listenarr.Api.Hubs.DownloadHub> _hubContext;
        private readonly ILogger<FfmpegInstallBackgroundService> _logger;

        public FfmpegInstallBackgroundService(IFfmpegService ffmpegService, IHubContext<Listenarr.Api.Hubs.DownloadHub> hubContext, ILogger<FfmpegInstallBackgroundService> logger)
        {
            _ffmpegService = ffmpegService;
            _hubContext = hubContext;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Delay a little to allow the app to finish startup wiring (optional)
            try
            {
                _logger.LogInformation("FFmpeg installer background service started. Will attempt installation in the background if needed.");

                // Attempt installation once; don't block startup.
                var path = await _ffmpegService.EnsureFfprobeInstalledAsync();

                if (!string.IsNullOrEmpty(path))
                {
                    _logger.LogInformation("ffprobe installed/available at {Path}", path);
                    // Notify connected clients that ffprobe is now available
                    try
                    {
                        await _hubContext.Clients.All.SendAsync("FfmpegInstallStatus", new { status = "Installed", path }, cancellationToken: stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                        _logger.LogDebug(ex, "Failed to broadcast ffprobe install success message");
                    }
                }
                else
                {
                    _logger.LogWarning("ffprobe was not installed or auto-install disabled");
                    try
                    {
                        await _hubContext.Clients.All.SendAsync("FfmpegInstallStatus", new { status = "NotInstalled" }, cancellationToken: stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                        _logger.LogDebug(ex, "Failed to broadcast ffprobe install failure message");
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutdown requested
                            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogWarning(ex, "Error while attempting background ffprobe installation");
                try
                {
                    await _hubContext.Clients.All.SendAsync("FfmpegInstallStatus", new { status = "Error" });
                }
                catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException) { 
                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                }
            }
        }
    }
}

