/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.SystemDiagnostics.Diagnostics;

public sealed class SystemReadinessService(
    IDbContextFactory<ListenArrDbContext> dbFactory,
    ILibraryFilesystemReadiness filesystemReadiness,
    ILogger<SystemReadinessService> logger) : ISystemReadinessService
{
    public async Task<SystemReadiness> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            if (!await db.Database.CanConnectAsync(cancellationToken))
            {
                return CreateReadiness(
                    false,
                    "not_ready",
                    false,
                    false,
                    "database_unavailable");
            }

            var pendingMigrations = await db.Database.GetPendingMigrationsAsync(cancellationToken);
            var migrationsCurrent = !pendingMigrations.Any();
            return migrationsCurrent
                ? CreateReadiness(true, "ready", true, true)
                : CreateReadiness(false, "not_ready", true, false, "pending_migrations");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            logger.LogError(exception, "Readiness database check failed");
            return CreateReadiness(false, "not_ready", false, false, "database_check_failed");
        }
    }

    private SystemReadiness CreateReadiness(
        bool isReady,
        string status,
        bool databaseConnected,
        bool migrationsCurrent,
        string? errorCode = null)
    {
        var filesystem = filesystemReadiness.Current;
        return new SystemReadiness(
            isReady,
            status,
            databaseConnected,
            migrationsCurrent,
            errorCode,
            filesystem.IsReady,
            filesystem.Status.ToString(),
            filesystem.Phase,
            filesystem.ErrorCode,
            filesystem.ErrorMessage);
    }
}
