using Listenarr.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Scanning;

public partial class ScanJobProcessor
{
    private static readonly TimeSpan HandoffLeaseDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HandoffHeartbeatInterval = TimeSpan.FromMinutes(1);
    private readonly IScanQueueService _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ScanJobProcessor> _logger;
    private readonly IHubContext<DownloadHub> _hubContext;
    private readonly IAppMetricsService _metrics;
    private readonly IFileSystemSemanticsResolver _semanticsResolver;
    private readonly IMoveScanHandoffStore? _moveScanHandoffStore;
    private readonly TimeProvider _timeProvider;
    private readonly IFilesystemMutationCoordinator _filesystemMutationCoordinator;
    private readonly IAudiobookOperationCoordinator _audiobookOperationCoordinator;
    private readonly IMoveQueueService _moveQueueService;
    private readonly IAudiobookUpdatePublisher? _audiobookUpdatePublisher;

    public ScanJobProcessor(
        IScanQueueService queue,
        IServiceScopeFactory scopeFactory,
        ILogger<ScanJobProcessor> logger,
        IHubContext<DownloadHub> hubContext,
        IAppMetricsService metrics,
        IFileSystemSemanticsResolver semanticsResolver,
        IFilesystemMutationCoordinator filesystemMutationCoordinator,
        IAudiobookOperationCoordinator audiobookOperationCoordinator,
        IMoveQueueService moveQueueService,
        IMoveScanHandoffStore? moveScanHandoffStore = null,
        TimeProvider? timeProvider = null,
        IAudiobookUpdatePublisher? audiobookUpdatePublisher = null)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _hubContext = hubContext;
        _metrics = metrics;
        _semanticsResolver = semanticsResolver;
        _moveScanHandoffStore = moveScanHandoffStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _filesystemMutationCoordinator = filesystemMutationCoordinator ?? throw new ArgumentNullException(nameof(filesystemMutationCoordinator));
        _audiobookOperationCoordinator = audiobookOperationCoordinator ?? throw new ArgumentNullException(nameof(audiobookOperationCoordinator));
        _moveQueueService = moveQueueService ?? throw new ArgumentNullException(nameof(moveQueueService));
        _audiobookUpdatePublisher = audiobookUpdatePublisher;
    }

    public async Task ProcessJobAsync(ScanJob job, CancellationToken stoppingToken)
    {
        if (job.MoveScanHandoffId.HasValue && _moveScanHandoffStore != null)
        {
            await ProcessMoveOwnedJobAsync(job, stoppingToken);
            return;
        }

        await ExecuteCoordinatedJobAsync(job, stoppingToken);
    }

    private async Task ExecuteCoordinatedJobAsync(
        ScanJob job,
        CancellationToken cancellationToken)
    {
        List<Func<CancellationToken, Task>> postCompletionEffects = [];
        void RegisterPostCompletionEffects(Func<CancellationToken, Task> effects) =>
            postCompletionEffects.Add(effects);

        await _filesystemMutationCoordinator.ExecuteExclusiveAsync(
            globalToken => _audiobookOperationCoordinator.ExecuteExclusiveAsync(
                job.AudiobookId,
                token => ProcessJobCoreAsync(job, RegisterPostCompletionEffects, token),
                globalToken),
            cancellationToken);

        foreach (var effect in postCompletionEffects)
        {
            await effect(cancellationToken);
        }
    }

    private async Task ProcessMoveOwnedJobAsync(
        ScanJob job,
        CancellationToken stoppingToken)
    {
        var initialRenewal = await RenewMoveScanLeaseAsync(job, stoppingToken);
        if (initialRenewal.Outcome != MoveScanLeaseRenewalOutcome.Renewed)
        {
            ApplyLeaseRenewalOutcome(job, initialRenewal);
            return;
        }

        using var leaseCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var leaseLost = 0;
        var heartbeatTask = MaintainMoveScanLeaseAsync(
            job,
            leaseCancellation,
            () => Interlocked.Exchange(ref leaseLost, 1));
        try
        {
            await ExecuteCoordinatedJobAsync(job, leaseCancellation.Token);
        }
        catch (OperationCanceledException) when (
            !stoppingToken.IsCancellationRequested
            && Volatile.Read(ref leaseLost) == 1)
        {
            ApplyTerminalStatus(
                job,
                new ScanTerminalDecision(
                    "Superseded",
                    "The durable move scan handoff was claimed by a newer attempt.",
                    MoveOwned: true));
            _logger.LogWarning(
                "Stopped stale move scan job {JobId} after its handoff lease was lost",
                job.Id);
        }
        finally
        {
            await leaseCancellation.CancelAsync();
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException) when (leaseCancellation.IsCancellationRequested)
            {
                _logger.LogDebug(
                    "Stopped move scan heartbeat for job {JobId}",
                    job.Id);
            }
        }
    }

    private async Task MaintainMoveScanLeaseAsync(
        ScanJob job,
        CancellationTokenSource leaseCancellation,
        Action markLeaseLost)
    {
        while (!leaseCancellation.IsCancellationRequested)
        {
            await Task.Delay(
                HandoffHeartbeatInterval,
                _timeProvider,
                leaseCancellation.Token);
            MoveScanLeaseRenewalResult renewal;
            try
            {
                renewal = await RenewMoveScanLeaseAsync(
                    job,
                    leaseCancellation.Token);
            }
            catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
            {
                _logger.LogWarning(
                    exception,
                    "Unable to prove ownership of move scan job {JobId}; canceling the attempt",
                    job.Id);
                markLeaseLost();
                await leaseCancellation.CancelAsync();
                return;
            }

            if (renewal.Outcome == MoveScanLeaseRenewalOutcome.Renewed)
            {
                continue;
            }

            if (renewal.Outcome == MoveScanLeaseRenewalOutcome.Superseded)
            {
                markLeaseLost();
                await leaseCancellation.CancelAsync();
            }
            return;
        }
    }

    private async Task<PathIdentitySnapshot> ValidateScanIdentityAsync(
        string path,
        PathIdentitySnapshot identity,
        CancellationToken cancellationToken)
    {
        try
        {
            identity.ValidateForPath(path);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException(
                $"The persisted scan filesystem identity is invalid: {exception.Message}",
                exception);
        }

        if (identity.RequestedMode == FileSystemCaseSensitivityMode.Auto)
        {
            var current = await _semanticsResolver.ResolveAsync(
                identity.BoundaryPath,
                FileSystemCaseSensitivityMode.Auto,
                cancellationToken);
            if (current.State != PathIdentityState.Valid
                || current.Semantics.Syntax != identity.Syntax
                || current.Semantics.CaseSensitivity != identity.CaseSensitivity)
            {
                throw new InvalidOperationException(
                    "The scan filesystem identity changed after the job was queued.");
            }
        }

        return identity;
    }

    private Task<MoveScanLeaseRenewalResult> RenewMoveScanLeaseAsync(
        ScanJob job,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        return _moveScanHandoffStore!.RenewAttemptLeaseAsync(
            job.MoveScanHandoffId!.Value,
            job.MoveScanAttemptGeneration,
            job.Id,
            now,
            now.Add(HandoffLeaseDuration),
            cancellationToken);
    }

    private void ApplyLeaseRenewalOutcome(
        ScanJob job,
        MoveScanLeaseRenewalResult renewal)
    {
        var decision = renewal.Outcome switch
        {
            MoveScanLeaseRenewalOutcome.Completed => new ScanTerminalDecision(
                "Completed",
                null,
                MoveOwned: true),
            MoveScanLeaseRenewalOutcome.Failed => new ScanTerminalDecision(
                "Failed",
                renewal.Error,
                MoveOwned: true),
            _ => new ScanTerminalDecision(
                "Superseded",
                "A newer move scan attempt owns the durable handoff.",
                MoveOwned: true)
        };
        ApplyTerminalStatus(job, decision);
    }
}
