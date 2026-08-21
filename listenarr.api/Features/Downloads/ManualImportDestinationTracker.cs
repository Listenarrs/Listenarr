/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Domain.Common;

namespace Listenarr.Api.Features.Downloads;

public sealed class ManualImportDestinationTracker(
    IFileSystem fileSystem,
    IFilePublicationSourceCapability filePublicationSourceCapability)
{
    private readonly Dictionary<string, HashSet<string>> _usedDestinationsByBoundary = new(StringComparer.Ordinal);

    public int Count => _usedDestinationsByBoundary.Values.Sum(set => set.Count);

    public Task<ManualImportDestinationReservation> PlanUniqueAsync(
        string desiredDestination,
        FileSystemSemanticsResolution destinationResolution,
        CancellationToken cancellationToken = default) =>
        PlanAsync(
            sourceProof: null,
            desiredDestination,
            destinationResolution,
            allowExistingEquivalent: false,
            cancellationToken);

    public Task<ManualImportDestinationReservation> PlanIdempotentOrUniqueAsync(
        FilePublicationSourceProof sourceProof,
        string desiredDestination,
        FileSystemSemanticsResolution destinationResolution,
        CancellationToken cancellationToken = default)
    {
        sourceProof.Validate();
        return PlanAsync(
            sourceProof,
            desiredDestination,
            destinationResolution,
            allowExistingEquivalent: true,
            cancellationToken);
    }

    public void Commit(ManualImportDestinationReservation reservation)
    {
        if (!_usedDestinationsByBoundary.TryGetValue(reservation.BoundaryKey, out var usedDestinations))
        {
            throw new InvalidOperationException("Destination reservation boundary was not planned.");
        }

        usedDestinations.Add(reservation.Path);
    }

    public void CommitRecovered(
        string destinationPath,
        FileSystemSemanticsResolution destinationResolution)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (destinationResolution.State != PathIdentityState.Valid
            || string.IsNullOrWhiteSpace(destinationResolution.BoundaryPath))
        {
            throw new InvalidOperationException(
                destinationResolution.Reason
                    ?? "Recovered destination filesystem identity is unavailable.");
        }
        if (!FileSystemPathIdentity.IsSameOrInside(
                destinationPath,
                destinationResolution.BoundaryPath,
                destinationResolution.Semantics))
        {
            throw new InvalidOperationException(
                "Recovered destination escaped its authorized filesystem boundary.");
        }

        var boundaryKey = FileSystemPathIdentity.CreateKey(
            "manual-import-boundary",
            destinationResolution.BoundaryPath,
            destinationResolution.Semantics);
        if (!_usedDestinationsByBoundary.TryGetValue(boundaryKey, out var usedDestinations))
        {
            usedDestinations = new HashSet<string>(destinationResolution.Semantics.Comparer);
            _usedDestinationsByBoundary[boundaryKey] = usedDestinations;
        }

        usedDestinations.Add(destinationPath);
    }

    private async Task<ManualImportDestinationReservation> PlanAsync(
        FilePublicationSourceProof? sourceProof,
        string desiredDestination,
        FileSystemSemanticsResolution destinationResolution,
        bool allowExistingEquivalent,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(desiredDestination))
        {
            throw new ArgumentException("Destination path is required.", nameof(desiredDestination));
        }

        if (destinationResolution.State != PathIdentityState.Valid
            || string.IsNullOrWhiteSpace(destinationResolution.BoundaryPath))
        {
            throw new InvalidOperationException(
                destinationResolution.Reason
                    ?? "Destination filesystem identity is unavailable.");
        }
        if (!FileSystemPathIdentity.IsSameOrInside(
                desiredDestination,
                destinationResolution.BoundaryPath,
                destinationResolution.Semantics))
        {
            throw new InvalidOperationException(
                "Destination reservation escaped its authorized filesystem boundary.");
        }

        var boundaryKey = FileSystemPathIdentity.CreateKey(
            "manual-import-boundary",
            destinationResolution.BoundaryPath,
            destinationResolution.Semantics);
        if (!_usedDestinationsByBoundary.TryGetValue(boundaryKey, out var usedDestinations))
        {
            usedDestinations = new HashSet<string>(destinationResolution.Semantics.Comparer);
            _usedDestinationsByBoundary[boundaryKey] = usedDestinations;
        }

        if (allowExistingEquivalent
            && sourceProof.HasValue
            && !usedDestinations.Contains(desiredDestination)
            && await ExistingMatchesSourceProofAsync(
                desiredDestination,
                sourceProof.Value,
                cancellationToken))
        {
            return new ManualImportDestinationReservation(
                desiredDestination,
                boundaryKey,
                ReusesExistingFile: true);
        }

        // Use the destination volume's case rules for both in-memory batch collisions
        // and pre-existing path checks so macOS/Linux mounted case-insensitive volumes
        // do not accept two case-only variants in the same successful import batch.
        var uniqueDestination = FileUtils.GetUniqueDestinationPath(
            desiredDestination,
            fileSystem.FileExists,
            usedDestinations);
        return new ManualImportDestinationReservation(uniqueDestination, boundaryKey);
    }

    private async Task<bool> ExistingMatchesSourceProofAsync(
        string destination,
        FilePublicationSourceProof sourceProof,
        CancellationToken cancellationToken)
    {
        if (!fileSystem.FileExists(destination))
        {
            return false;
        }

        var destinationCapability = await filePublicationSourceCapability.CheckAsync(
            destination,
            cancellationToken);
        if (!destinationCapability.IsSupported
            || !destinationCapability.SourceProof.HasValue)
        {
            return false;
        }

        var destinationProof = destinationCapability.SourceProof.Value;
        return destinationProof.Length == sourceProof.Length
            && string.Equals(
                destinationProof.Sha256,
                sourceProof.Sha256,
                StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record ManualImportDestinationReservation(
    string Path,
    string BoundaryKey,
    bool ReusesExistingFile = false);
