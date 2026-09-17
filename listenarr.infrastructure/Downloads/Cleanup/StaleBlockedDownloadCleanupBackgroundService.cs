/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Downloads.Cleanup;

/// <summary>
/// Periodically reaps stale <c>ImportBlocked</c> download records (see
/// <see cref="StaleBlockedDownloadCleanupProcessor"/>). This is low-urgency housekeeping, so it
/// runs on a relaxed fixed interval rather than the download polling cadence.
/// </summary>
public class StaleBlockedDownloadCleanupService(
    IStaleBlockedDownloadCleanupProcessor processor,
    ILogger<StaleBlockedDownloadCleanupService> logger,
    IWorkerCycleRunner cycleRunner) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("StaleBlockedDownloadCleanupService background task started");

        await cycleRunner.RunPeriodicAsync(
            nameof(StaleBlockedDownloadCleanupService),
            initialDelay: InitialDelay,
            intervalProvider: () => Interval,
            runCycle: processor.RunCycleAsync,
            cancellationToken);

        logger.LogInformation("StaleBlockedDownloadCleanupService background task stopped");
    }
}
