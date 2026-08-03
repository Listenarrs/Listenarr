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

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class MoveCleanupBoundaryResolver(
    IFileSystemSemanticsResolver semanticsResolver) : IMoveCleanupBoundaryResolver
{
    public async Task<MoveCleanupBoundaryResolution> ResolveAsync(
        string source,
        string target,
        IReadOnlyCollection<RootFolder> configuredRoots,
        string? persistedBoundary = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentNullException.ThrowIfNull(configuredRoots);

        string sourceFullPath;
        string targetFullPath;
        try
        {
            sourceFullPath = FileSystemPathIdentity.ResolveNativeAbsolutePath(source);
            targetFullPath = FileSystemPathIdentity.ResolveNativeAbsolutePath(target);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or NotSupportedException or PathTooLongException)
        {
            return Unavailable($"Move paths could not be normalized: {exception.Message}");
        }

        var sourceResolution = await semanticsResolver.ResolveAsync(
            sourceFullPath,
            cancellationToken: cancellationToken);
        if (sourceResolution.State != PathIdentityState.Valid)
        {
            return Unavailable(
                sourceResolution.Reason ?? "Source filesystem identity is unavailable.");
        }

        var semantics = sourceResolution.Semantics;
        var sourceParent = Path.GetDirectoryName(sourceFullPath);
        if (string.IsNullOrWhiteSpace(sourceParent))
        {
            return Unavailable("The source path has no removable parent directory.");
        }

        var configuredBoundary = await FindDeepestConfiguredBoundaryAsync(
            sourceFullPath,
            configuredRoots,
            semantics,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(configuredBoundary.UnavailableReason))
        {
            return Unavailable(configuredBoundary.UnavailableReason);
        }

        if (!string.IsNullOrWhiteSpace(persistedBoundary))
        {
            var validatedPersistedBoundary = ValidatePersistedBoundary(
                sourceParent,
                persistedBoundary,
                semantics);
            if (!validatedPersistedBoundary.IsAvailable)
            {
                return configuredBoundary.Boundary != null
                    ? new MoveCleanupBoundaryResolution(
                        configuredBoundary.Boundary,
                        MoveCleanupBoundaryKind.ConfiguredRoot)
                    : validatedPersistedBoundary;
            }

            if (configuredBoundary.Boundary != null)
            {
                return SelectNarrowerBoundary(
                    validatedPersistedBoundary.Boundary!,
                    configuredBoundary.Boundary,
                    semantics);
            }

            return validatedPersistedBoundary;
        }

        if (configuredBoundary.Boundary != null)
        {
            return new MoveCleanupBoundaryResolution(
                configuredBoundary.Boundary,
                MoveCleanupBoundaryKind.ConfiguredRoot);
        }

        FileSystemSemanticsResolution targetResolution;
        try
        {
            targetResolution = await semanticsResolver.ResolveAsync(
                targetFullPath,
                cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or NotSupportedException or PathTooLongException)
        {
            targetResolution = new FileSystemSemanticsResolution(
                new FileSystemPathSemantics(
                    semantics.Syntax,
                    FileSystemCaseSensitivity.Unknown),
                PathIdentityState.Unavailable,
                targetFullPath,
                $"Target filesystem identity is unavailable: {exception.Message}");
        }

        if (targetResolution.State == PathIdentityState.Valid
            && targetResolution.Semantics.Syntax == semantics.Syntax)
        {
            var commonAncestor = FindDeepestCommonAncestor(
                sourceFullPath,
                targetFullPath,
                semantics,
                targetResolution.Semantics);
            if (commonAncestor != null)
            {
                return new MoveCleanupBoundaryResolution(
                    commonAncestor,
                    MoveCleanupBoundaryKind.CommonAncestor);
            }
        }

        var volumeAnchor = FindSourceVolumeAnchor(
            sourceFullPath,
            sourceParent,
            semantics);
        if (volumeAnchor != null)
        {
            return new MoveCleanupBoundaryResolution(
                volumeAnchor,
                MoveCleanupBoundaryKind.VolumeAnchor);
        }

        var targetReason = targetResolution.State == PathIdentityState.Valid
            && targetResolution.Semantics.Syntax == semantics.Syntax
                ? null
                : targetResolution.Reason
                    ?? "Target filesystem identity is unavailable or uses a different path syntax.";
        return Unavailable(
            targetReason == null
                ? "No configured source root, safe common ancestor, or source volume anchor could be established."
                : $"No configured source root or source volume anchor could be established, and a common ancestor could not be evaluated safely: {targetReason}");
    }

    private static MoveCleanupBoundaryResolution ValidatePersistedBoundary(
        string sourceParent,
        string persistedBoundary,
        FileSystemPathSemantics semantics)
    {
        try
        {
            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    persistedBoundary,
                    out var boundary,
                    out var boundaryReason,
                    semantics.Syntax))
            {
                return Unavailable(
                    $"The persisted source cleanup boundary is unavailable on this host: {boundaryReason}");
            }
            if (!FileSystemPathIdentity.IsSameOrInside(sourceParent, boundary, semantics))
            {
                return Unavailable(
                    "The persisted source cleanup boundary no longer contains the source path.");
            }

            return new MoveCleanupBoundaryResolution(
                boundary,
                MoveCleanupBoundaryKind.Persisted);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or NotSupportedException or PathTooLongException)
        {
            return Unavailable(
                $"The persisted source cleanup boundary is invalid: {exception.Message}");
        }
    }

    private async Task<ConfiguredBoundaryResolution> FindDeepestConfiguredBoundaryAsync(
        string source,
        IEnumerable<RootFolder> configuredRoots,
        FileSystemPathSemantics sourceSemantics,
        CancellationToken cancellationToken)
    {
        var candidates = new List<ConfiguredRootCandidate>();
        foreach (var root in configuredRoots)
        {
            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    root.Path,
                    out var canonicalRoot,
                    out _)
                || !FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                    canonicalRoot,
                    out var rootSyntax)
                || rootSyntax != sourceSemantics.Syntax)
            {
                continue;
            }

            var potentialSemantics = new FileSystemPathSemantics(
                rootSyntax,
                root.CaseSensitivityMode switch
                {
                    FileSystemCaseSensitivityMode.Sensitive => FileSystemCaseSensitivity.Sensitive,
                    FileSystemCaseSensitivityMode.Insensitive => FileSystemCaseSensitivity.Insensitive,
                    _ => FileSystemCaseSensitivity.Insensitive
                });
            int canonicalLength;
            try
            {
                if (!FileSystemPathIdentity.IsSameOrInside(
                        source,
                        canonicalRoot,
                        potentialSemantics))
                {
                    continue;
                }

                canonicalLength = canonicalRoot.Length;
            }
            catch (ArgumentException)
            {
                continue;
            }

            FileSystemSemanticsResolution rootResolution;
            try
            {
                rootResolution = await semanticsResolver.ResolveAsync(
                    canonicalRoot,
                    root.CaseSensitivityMode,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is
                ArgumentException or InvalidOperationException or NotSupportedException or PathTooLongException)
            {
                candidates.Add(new ConfiguredRootCandidate(
                    canonicalLength,
                    null,
                    $"Configured source root '{root.Path}' could contain the source, but its filesystem identity is unavailable: {exception.Message}"));
                continue;
            }

            if (rootResolution.State != PathIdentityState.Valid
                || rootResolution.Semantics.Syntax != rootSyntax)
            {
                candidates.Add(new ConfiguredRootCandidate(
                    canonicalLength,
                    null,
                    rootResolution.Reason
                        ?? $"Configured source root '{root.Path}' could contain the source, but its filesystem identity is unavailable."));
                continue;
            }

            try
            {
                if (!FileSystemPathIdentity.IsSameOrInside(
                        source,
                        canonicalRoot,
                        rootResolution.Semantics))
                {
                    // Auto mode can conservatively look like a match before probing and then
                    // prove sensitive. Such a root does not contain the source and is ignored.
                    continue;
                }

                if (!TryDerivePhysicalBoundary(
                        source,
                        canonicalRoot,
                        sourceSemantics,
                        rootResolution.Semantics,
                        out var physicalBoundary))
                {
                    candidates.Add(new ConfiguredRootCandidate(
                        canonicalLength,
                        null,
                        $"Configured source root '{root.Path}' matched the source logically, but its physical cleanup boundary could not be established safely."));
                    continue;
                }

                candidates.Add(new ConfiguredRootCandidate(
                    canonicalLength,
                    physicalBoundary,
                    null));
            }
            catch (Exception exception) when (exception is
                ArgumentException or InvalidOperationException or NotSupportedException or PathTooLongException)
            {
                candidates.Add(new ConfiguredRootCandidate(
                    canonicalLength,
                    null,
                    $"Configured source root '{root.Path}' matched the source, but its cleanup boundary is invalid: {exception.Message}"));
            }
        }

        if (candidates.Count == 0)
        {
            return new ConfiguredBoundaryResolution(null, null);
        }

        var deepestLength = candidates.Max(candidate => candidate.CanonicalLength);
        var deepestCandidates = candidates
            .Where(candidate => candidate.CanonicalLength == deepestLength)
            .ToList();
        var unavailable = deepestCandidates.FirstOrDefault(
            candidate => !string.IsNullOrWhiteSpace(candidate.UnavailableReason));
        if (unavailable != null)
        {
            return new ConfiguredBoundaryResolution(null, unavailable.UnavailableReason);
        }

        var boundaries = deepestCandidates
            .Select(candidate => candidate.Boundary)
            .Where(boundary => !string.IsNullOrWhiteSpace(boundary))
            .Cast<string>()
            .Distinct(sourceSemantics.Comparer)
            .ToList();
        return boundaries.Count switch
        {
            0 => new ConfiguredBoundaryResolution(
                null,
                "The most-specific configured source root has no safe physical cleanup boundary."),
            1 => new ConfiguredBoundaryResolution(boundaries[0], null),
            _ => new ConfiguredBoundaryResolution(
                null,
                "Multiple equally specific configured source roots resolved to different physical cleanup boundaries.")
        };
    }

    private static bool TryDerivePhysicalBoundary(
        string source,
        string configuredRoot,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics configuredRootSemantics,
        out string physicalBoundary)
    {
        physicalBoundary = string.Empty;
        if (!FileSystemPathIdentity.TryGetRelativePathWithinBase(
                configuredRoot,
                source,
                configuredRootSemantics,
                out var relativePath))
        {
            return false;
        }

        var separators = configuredRootSemantics.Syntax == FileSystemPathSyntax.Windows
            ? new[] { '\\', '/' }
            : new[] { '/' };
        var segmentCount = relativePath.Split(
            separators,
            StringSplitOptions.RemoveEmptyEntries).Length;
        var candidate = source;
        for (var index = 0; index < segmentCount; index++)
        {
            candidate = Path.GetDirectoryName(candidate);
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }
        }

        if (!FileSystemPathIdentity.AreEquivalent(
                candidate,
                configuredRoot,
                configuredRootSemantics)
            || !FileSystemPathIdentity.IsSameOrInside(
                source,
                candidate,
                sourceSemantics))
        {
            return false;
        }

        physicalBoundary = candidate;
        return true;
    }

}
