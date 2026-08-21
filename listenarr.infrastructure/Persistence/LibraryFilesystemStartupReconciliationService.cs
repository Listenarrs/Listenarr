/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Persistence;

internal sealed class LibraryFilesystemStartupReconciliationService(
    IServiceScopeFactory scopeFactory,
    LibraryFilesystemReadiness readiness,
    ILogger<LibraryFilesystemStartupReconciliationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // BackgroundService.StartAsync observes ExecuteAsync until its first incomplete await.
        // Yield immediately so filesystem reconciliation can never become a host-start barrier.
        await Task.Yield();

        string? phase = null;
        AudiobookFileIdentityReconciliationResult? fileIdentityResult = null;
        try
        {
            phase = "FileRegistrationOwnerAdoption";
            readiness.MarkRunning(phase);
            await RunScopedAsync<IFileRegistrationRecoveryService>(
                static (service, token) => service.AdoptCommittedAnonymousAsync(token),
                stoppingToken);

            phase = "RootFolderObjectIdentities";
            readiness.MarkRunning(phase);
            await RunScopedAsync<IRootFolderObjectIdentityReconciler>(
                static (service, token) => service.ReconcileAsync(token),
                stoppingToken);

            phase = "RootFolderRelocations";
            readiness.MarkRunning(phase);
            await RunScopedAsync<IRootFolderRelocationService>(
                static (service, token) => service.ReconcileActiveAsync(token),
                stoppingToken);

            phase = "LibraryDirectoryOwnership";
            readiness.MarkRunning(phase);
            await RunScopedAsync<ILibraryDirectoryOwnershipReconciler>(
                static (service, token) => service.ReconcileAsync(token),
                stoppingToken);

            phase = "AudiobookDeletionRecovery";
            readiness.MarkRunning(phase);
            await RunScopedAsync<IAudiobookDeletionIntentReconciler>(
                static (service, token) => service.ReconcileAsync(token),
                stoppingToken);

            phase = "FileRegistrationRecovery";
            readiness.MarkRunning(phase);
            await RunScopedAsync<IFileRegistrationRecoveryService>(
                static (service, token) => service.ReconcileAsync(token),
                stoppingToken);

            phase = "CompatibilityFilePublicationRecovery";
            readiness.MarkRunning(phase);
            await RunScopedAsync<ICompatibilityFilePublicationRecoveryService>(
                static (service, token) => service.ReconcileAsync(token),
                stoppingToken);

            phase = "FileRenameRecovery";
            readiness.MarkRunning(phase);
            await RunScopedAsync<IFileRenameRecoveryReconciler>(
                static (service, token) => service.ReconcileAsync(token),
                stoppingToken);

            phase = "AudiobookFileIdentities";
            readiness.MarkRunning(phase);
            await RunScopedAsync<IAudiobookFileIdentityReconciler>(
                async (service, token) =>
                {
                    fileIdentityResult = await service.ReconcileAsync(token);
                },
                stoppingToken);

            readiness.MarkReady();
            logger.LogInformation(
                "Library filesystem startup reconciliation completed. Filesystem operations remain subject to per-root and per-object authorization. Audiobook file paths: {Valid} valid, {Conflicted} conflicted, {Unavailable} unavailable",
                fileIdentityResult?.Valid ?? 0,
                fileIdentityResult?.Conflicted ?? 0,
                fileIdentityResult?.Unavailable ?? 0);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Library filesystem startup reconciliation canceled during host shutdown");
        }
        catch (OperationCanceledException exception)
        {
            MarkFailed(exception, phase);
        }
        catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
        {
            MarkFailed(exception, phase);
        }
    }

    private void MarkFailed(Exception exception, string? phase)
    {
        var message =
            "Library filesystem initialization failed. Browsing remains available, but filesystem operations are disabled. Check the server logs.";
        readiness.MarkFailed(
            "filesystem_initialization_failed",
            message,
            phase);
        logger.LogError(
            exception,
            "Library filesystem startup reconciliation failed during phase {Phase}; filesystem mutations remain disabled",
            phase);
    }

    private async Task RunScopedAsync<TService>(
        Func<TService, CancellationToken, Task> action,
        CancellationToken cancellationToken)
        where TService : notnull
    {
        using var scope = scopeFactory.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TService>();
        await action(service, cancellationToken);
    }
}
