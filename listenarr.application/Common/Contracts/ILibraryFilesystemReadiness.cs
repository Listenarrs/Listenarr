/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Application.Common.Contracts;

public enum LibraryFilesystemInitializationStatus
{
    Pending,
    Running,
    Ready,
    Failed
}

public sealed record LibraryFilesystemReadinessSnapshot(
    LibraryFilesystemInitializationStatus Status,
    string? Phase = null,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public bool IsReady => Status == LibraryFilesystemInitializationStatus.Ready;
}

public interface ILibraryFilesystemReadiness
{
    LibraryFilesystemReadinessSnapshot Current { get; }

    Task WaitUntilReadyAsync(CancellationToken cancellationToken = default);

    void EnsureMetadataRepairReady();
}

public interface ILibraryFilesystemMutationGate
{
    void EnsureReady();
}
