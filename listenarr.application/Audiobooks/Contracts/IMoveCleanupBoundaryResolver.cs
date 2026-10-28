/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Application.Audiobooks.Contracts;

public enum MoveCleanupBoundaryKind
{
    Persisted,
    ConfiguredRoot,
    CommonAncestor,
    VolumeAnchor,
    Unavailable
}

public sealed record MoveCleanupBoundaryResolution(
    string? Boundary,
    MoveCleanupBoundaryKind Kind,
    string? Reason = null)
{
    public bool IsAvailable => !string.IsNullOrWhiteSpace(Boundary);
}

public interface IMoveCleanupBoundaryResolver
{
    Task<MoveCleanupBoundaryResolution> ResolveAsync(
        string source,
        string target,
        IReadOnlyCollection<RootFolder> configuredRoots,
        string? persistedBoundary = null,
        CancellationToken cancellationToken = default);
}
