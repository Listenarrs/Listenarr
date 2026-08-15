/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using System.Data.Common;
using Listenarr.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories;

public partial class AudiobookRepository
{
    public Task<bool> RewritePathReferencesAsync(
        int audiobookId,
        string? sourceBasePath,
        string targetBasePath,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        CancellationToken ct = default,
        FileSystemCaseSensitivityMode targetCaseSensitivityMode = FileSystemCaseSensitivityMode.Auto) =>
        RewritePathReferencesCoreAsync(
            audiobookId,
            sourceBasePath,
            targetBasePath,
            sourceSemantics,
            targetSemantics,
            targetCaseSensitivityMode,
            targetPhysicalObjectIdentities: null,
            targetPhysicalIdentityObservedAtUtc: null,
            ct);

    public Task<bool> RewriteMovedPathReferencesAsync(
        int audiobookId,
        string? sourceBasePath,
        string targetBasePath,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        IReadOnlyDictionary<string, string> targetPhysicalObjectIdentities,
        DateTime targetPhysicalIdentityObservedAtUtc,
        CancellationToken ct = default,
        FileSystemCaseSensitivityMode targetCaseSensitivityMode = FileSystemCaseSensitivityMode.Auto)
    {
        ArgumentNullException.ThrowIfNull(targetPhysicalObjectIdentities);
        return RewritePathReferencesCoreAsync(
            audiobookId,
            sourceBasePath,
            targetBasePath,
            sourceSemantics,
            targetSemantics,
            targetCaseSensitivityMode,
            targetPhysicalObjectIdentities,
            targetPhysicalIdentityObservedAtUtc,
            ct);
    }

    private async Task<bool> RewritePathReferencesCoreAsync(
        int audiobookId,
        string? sourceBasePath,
        string targetBasePath,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        FileSystemCaseSensitivityMode targetCaseSensitivityMode,
        IReadOnlyDictionary<string, string>? targetPhysicalObjectIdentities,
        DateTime? targetPhysicalIdentityObservedAtUtc,
        CancellationToken ct)
    {
        try
        {
            var audiobook = await _db.Audiobooks
                .Include(candidate => candidate.Files)
                .SingleOrDefaultAsync(candidate => candidate.Id == audiobookId, ct);
            if (audiobook == null)
            {
                return false;
            }

            AudiobookPathReferenceRewriter.Rewrite(
                audiobook,
                sourceBasePath,
                targetBasePath,
                sourceSemantics,
                targetSemantics,
                targetCaseSensitivityMode,
                targetPhysicalObjectIdentities,
                targetPhysicalIdentityObservedAtUtc);
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            throw new PersistenceException(
                "Failed to persist moved audiobook path references.",
                exception);
        }
    }
}
