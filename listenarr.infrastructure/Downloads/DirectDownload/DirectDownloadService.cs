/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Downloads.DirectDownload;

/// <summary>
/// Runs the internal direct-download pipeline. DDLs are not handed to an
/// external download client, so this worker owns fetching the file before the
/// normal import job pipeline takes over.
/// </summary>
internal sealed class DirectDownloadService(
    IDirectDownloadProcessor processor,
    ILogger<DirectDownloadService> logger,
    IWorkerCycleRunner cycleRunner) : BackgroundService
{
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("DirectDownloadService background task started");

        await cycleRunner.RunPeriodicAsync(
            nameof(DirectDownloadService),
            initialDelay: null,
            intervalProvider: () => PollingInterval,
            runCycle: processor.RunCycleAsync,
            cancellationToken: stoppingToken);

        logger.LogInformation("DirectDownloadService background task stopped");
    }
}
