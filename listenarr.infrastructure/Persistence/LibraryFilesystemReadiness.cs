/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Application.Common.Exceptions;

namespace Listenarr.Infrastructure.Persistence;

internal sealed class LibraryFilesystemReadiness :
    ILibraryFilesystemReadiness,
    ILibraryFilesystemMutationGate
{
    private readonly object _sync = new();
    private readonly TaskCompletionSource _ready = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _settled = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private LibraryFilesystemReadinessSnapshot _snapshot = new(
        LibraryFilesystemInitializationStatus.Pending);

    public LibraryFilesystemReadinessSnapshot Current => Volatile.Read(ref _snapshot);

    public Task WaitUntilReadyAsync(CancellationToken cancellationToken = default) =>
        _ready.Task.WaitAsync(cancellationToken);

    public void EnsureMetadataRepairReady()
    {
        var snapshot = Current;
        if (snapshot.IsReady)
        {
            return;
        }

        if (snapshot.Status == LibraryFilesystemInitializationStatus.Failed)
        {
            throw new ApplicationUnavailableException(
                "metadata_repair_initialization_failed",
                "Library recovery did not complete safely. Resolve the startup recovery failure and restart Listenarr before repairing root-folder metadata.");
        }

        throw new ApplicationUnavailableException(
            "metadata_repair_initializing",
            "Library recovery is still running. Wait for startup reconciliation to finish before repairing root-folder metadata.");
    }

    internal Task WaitUntilSettledAsync(CancellationToken cancellationToken = default) =>
        _settled.Task.WaitAsync(cancellationToken);

    public void EnsureReady()
    {
        var snapshot = Current;
        if (snapshot.IsReady)
        {
            return;
        }

        if (snapshot.Status == LibraryFilesystemInitializationStatus.Failed)
        {
            throw new ApplicationUnavailableException(
                "filesystem_initialization_failed",
                snapshot.ErrorMessage
                    ?? "Library filesystem initialization did not complete. Filesystem operations are unavailable.");
        }

        throw new ApplicationUnavailableException(
            "filesystem_initializing",
            "Library filesystem initialization is still in progress. Filesystem operations will be available when initialization completes.");
    }

    internal void MarkRunning(string phase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        lock (_sync)
        {
            if (_snapshot.Status is LibraryFilesystemInitializationStatus.Ready
                or LibraryFilesystemInitializationStatus.Failed)
            {
                return;
            }

            Volatile.Write(
                ref _snapshot,
                new LibraryFilesystemReadinessSnapshot(
                    LibraryFilesystemInitializationStatus.Running,
                    phase));
        }
    }

    internal void MarkReady()
    {
        lock (_sync)
        {
            if (_snapshot.Status == LibraryFilesystemInitializationStatus.Failed)
            {
                return;
            }

            Volatile.Write(
                ref _snapshot,
                new LibraryFilesystemReadinessSnapshot(
                    LibraryFilesystemInitializationStatus.Ready));
            _ready.TrySetResult();
            _settled.TrySetResult();
        }
    }

    internal void MarkFailed(string errorCode, string errorMessage, string? phase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);
        lock (_sync)
        {
            if (_snapshot.Status == LibraryFilesystemInitializationStatus.Ready)
            {
                return;
            }

            Volatile.Write(
                ref _snapshot,
                new LibraryFilesystemReadinessSnapshot(
                    LibraryFilesystemInitializationStatus.Failed,
                    phase,
                    errorCode,
                    errorMessage));
            _settled.TrySetResult();
        }
    }
}
