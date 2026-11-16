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
    /// Background service that resolves ffprobe without blocking application startup.
    /// It runs once and broadcasts a SignalR message with the result.
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
                logger.LogInformation("ffprobe availability check started in the background.");

                await processor.EnsureInstalledAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.LogDebug("ffprobe availability check canceled due to host shutdown.");
            }
            catch (OperationCanceledException ex)
            {
                logger.LogWarning(ex, "ffprobe availability check canceled or timed out; continuing without ffprobe.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Error while resolving ffprobe in the background");
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
                logger.LogInformation("ffprobe available at {Path}", path);
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
                logger.LogWarning("No ffprobe available; audio metadata extraction is disabled");
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
