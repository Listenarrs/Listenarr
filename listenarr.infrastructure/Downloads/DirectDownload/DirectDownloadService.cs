// Listenarr - Audiobook Management System
// Copyright (C) 2024-2026 Listenarr Contributors

using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Downloads.DirectDownload;

/// <summary>
/// Runs the internal direct-download pipeline. DDLs are not handed to an
/// external download client, so this worker owns fetching the file before the
/// normal import job pipeline takes over.
/// </summary>
public sealed class DirectDownloadService(
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
