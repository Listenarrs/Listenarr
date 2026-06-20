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
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Ffmpeg.Installation
{
    /// <summary>
    /// Background service that ensures ffprobe is installed without blocking application startup.
    /// It will attempt installation once and broadcast a SignalR message when finished.
    /// </summary>
    public class FfmpegInstallBackgroundService(
        IFfmpegInstallProcessor processor,
        IHubContext<DownloadHub> hubContext,
        ILogger<FfmpegInstallBackgroundService> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                logger.LogInformation("FFmpeg installer background service started. Will attempt installation in the background if needed.");

                await processor.EnsureInstalledAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.LogDebug("FFmpeg installer background service canceled due to host shutdown.");
            }
            catch (OperationCanceledException ex)
            {
                logger.LogWarning(ex, "FFmpeg installer background service canceled/timed out; continuing without bundled ffprobe.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Error while attempting background ffprobe installation");
                try
                {
                    await hubContext.Clients.All.SendAsync("FfmpegInstallStatus", new { status = "Error" }, cancellationToken: stoppingToken);
                }
                catch (Exception caughtEx) when (caughtEx is not OperationCanceledException && caughtEx is not OutOfMemoryException && caughtEx is not StackOverflowException)
                {
                    logger.LogDebug(caughtEx, "Failed to broadcast ffprobe install error message");
                }
            }
        }
    }

    public class FfmpegInstallProcessor(
        IFfmpegService ffmpegService,
        IHubContext<DownloadHub> hubContext,
        ILogger<FfmpegInstallProcessor> logger) : IFfmpegInstallProcessor
    {

        public async Task EnsureInstalledAsync(CancellationToken cancellationToken)
        {
            var path = await ffmpegService.EnsureFfprobeInstalledAsync();

            if (!string.IsNullOrEmpty(path))
            {
                logger.LogInformation("ffprobe installed/available at {Path}", path);
                try
                {
                    await hubContext.Clients.All.SendAsync("FfmpegInstallStatus", new { status = "Installed", path }, cancellationToken: cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    logger.LogDebug(ex, "Failed to broadcast ffprobe install success message");
                }
            }
            else
            {
                logger.LogWarning("ffprobe was not installed or auto-install disabled");
                try
                {
                    await hubContext.Clients.All.SendAsync("FfmpegInstallStatus", new { status = "NotInstalled" }, cancellationToken: cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    logger.LogDebug(ex, "Failed to broadcast ffprobe install failure message");
                }
            }
        }
    }
}
