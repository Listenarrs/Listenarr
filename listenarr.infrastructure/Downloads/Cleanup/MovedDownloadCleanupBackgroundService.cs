using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Downloads.Cleanup;

/// <summary>
/// Background service that handles moved downloads to remove them from the client.
/// Runs every 10 seconds to check for moved downloads.
/// </summary>
public class MovedDownloadCleanupService(
    IMovedDownloadCleanupProcessor processor,
    ILogger<MovedDownloadCleanupService> logger,
    IWorkerCycleRunner cycleRunner,
    IServiceScopeFactory scopeFactory) : BackgroundService
{
    private TimeSpan _pollingInterval = TimeSpan.FromSeconds(10);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("MovedDownloadCleanupService starting");

        try
        {
            using var scope = scopeFactory.CreateScope();
            var configurationService = scope.ServiceProvider
                .GetRequiredService<IConfigurationService>();
            var settings = await configurationService.GetApplicationSettingsAsync();
            if (settings.PollingIntervalSeconds > 0)
            {
                _pollingInterval = TimeSpan.FromSeconds(
                    settings.PollingIntervalSeconds);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("MovedDownloadCleanupService startup canceled");
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(
                ex,
                "MovedDownloadCleanupService settings load canceled/timed out during startup; using default interval");
        }
        catch (Exception ex)
            when (ex is not (OperationCanceledException
                or OutOfMemoryException
                or StackOverflowException))
        {
            logger.LogWarning(
                ex,
                "Failed to load polling interval from settings, using default");
        }

        await base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("MovedDownloadCleanupService stopping");
        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "MovedDownloadCleanupService background task started");

        await cycleRunner.RunPeriodicAsync(
            nameof(MovedDownloadCleanupService),
            initialDelay: null,
            intervalProvider: () => _pollingInterval,
            runCycle: processor.RunCycleAsync,
            cancellationToken);

        logger.LogInformation(
            "MovedDownloadCleanupService background task stopped");
    }
}
