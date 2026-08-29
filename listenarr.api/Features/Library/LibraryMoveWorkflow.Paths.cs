using Listenarr.Application.Common.Exceptions;
using Listenarr.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library;

public sealed partial class LibraryMoveWorkflow
{
    private string? TryNormalizeMoveRoot(string? path, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
            path,
            out var normalizedPath,
            out var validationReason))
        {
            return normalizedPath;
        }

        _logger.LogWarning(
            "Skipping invalid move boundary from {Description}: {Reason}",
            description,
            validationReason);
        return null;
    }

    private async Task<bool> AddAllowedMoveRootAsync(
        List<MoveRootBoundary> allowedRoots,
        string? normalizedRoot,
        FileSystemCaseSensitivityMode caseSensitivityMode,
        IDirectoryObjectIdentityResolver directoryIdentityResolver,
        CancellationToken cancellationToken,
        int? expectedDirectoryIdentityVersion = null,
        string? expectedDirectoryIdentity = null,
        string? directoryIdentityUnavailableReason = null,
        PersistedRootFolderPathSemantics? persistedSemantics = null,
        bool isManagedRoot = false,
        int? managedRootFolderId = null)
    {
        if (string.IsNullOrEmpty(normalizedRoot))
        {
            return false;
        }

        var resolution = await _semanticsResolver.ResolveAsync(
            normalizedRoot,
            caseSensitivityMode,
            cancellationToken);
        FileSystemPathSemantics? semantics;
        if (isManagedRoot)
        {
            if (resolution.State != PathIdentityState.Valid
                || !persistedSemantics.HasValue
                || persistedSemantics.Value.DetectAmbiguousCaseMatches
                || resolution.Semantics.Syntax != persistedSemantics.Value.Semantics.Syntax
                || resolution.Semantics.CaseSensitivity
                    != persistedSemantics.Value.Semantics.CaseSensitivity)
            {
                _logger.LogWarning(
                    "Skipping managed move boundary {Root}: live filesystem semantics do not match its persisted root semantics.",
                    LogRedaction.SanitizeFilePath(normalizedRoot));
                return false;
            }

            semantics = resolution.Semantics;
        }
        else
        {
            semantics = resolution.State == PathIdentityState.Valid
                ? resolution.Semantics
                : persistedSemantics?.Semantics;
            if (!semantics.HasValue)
            {
                _logger.LogWarning(
                    "Skipping move boundary {Root}: {Reason}",
                    LogRedaction.SanitizeFilePath(normalizedRoot),
                    resolution.Reason ?? "filesystem identity unavailable");
                return false;
            }
        }

        var hasPersistedDirectoryIdentity =
            expectedDirectoryIdentityVersion.HasValue
            && !string.IsNullOrWhiteSpace(expectedDirectoryIdentity);
        DirectoryObjectIdentityResolution directoryIdentity;
        if (isManagedRoot && hasPersistedDirectoryIdentity)
        {
            // The authorized generation plus a live pinned comparison is the authority.
            // A persisted unavailable reason is only an observation from an earlier point
            // in time and must not keep blocking a root after the same generation returns.
            var current = await directoryIdentityResolver.ResolveExistingAsync(
                normalizedRoot,
                expectedDirectoryIdentityVersion!.Value,
                expectedDirectoryIdentity!,
                cancellationToken);
            directoryIdentity = current.IsAvailable
                && current.Version == expectedDirectoryIdentityVersion
                && string.Equals(
                    current.Value,
                    expectedDirectoryIdentity,
                    StringComparison.Ordinal)
                    ? current
                    : DirectoryObjectIdentityResolution.Unavailable(
                        current.UnavailableReason
                            ?? "The configured root no longer identifies its authorized physical generation.");
        }
        else if (isManagedRoot)
        {
            directoryIdentity = DirectoryObjectIdentityResolution.Unavailable(
                directoryIdentityUnavailableReason
                    ?? "The configured root has not confirmed its physical storage folder.");
        }
        else
        {
            directoryIdentity = await directoryIdentityResolver.ResolveAsync(
                normalizedRoot,
                cancellationToken);
        }

        var existingIndex = allowedRoots.FindIndex(root => FileSystemPathIdentity.AreEquivalent(
            root.Path,
            normalizedRoot,
            semantics.Value));
        if (existingIndex >= 0)
        {
            if (isManagedRoot
                || (caseSensitivityMode != FileSystemCaseSensitivityMode.Auto
                    && allowedRoots[existingIndex].CaseSensitivityMode
                        == FileSystemCaseSensitivityMode.Auto))
            {
                allowedRoots[existingIndex] = new MoveRootBoundary(
                    normalizedRoot,
                    semantics.Value,
                    caseSensitivityMode,
                    directoryIdentity,
                    isManagedRoot,
                    managedRootFolderId);
            }

            return true;
        }

        allowedRoots.Add(new MoveRootBoundary(
            normalizedRoot,
            semantics.Value,
            caseSensitivityMode,
            directoryIdentity,
            isManagedRoot,
            managedRootFolderId));
        return true;
    }

    private string? TryFindNearestExistingDirectory(string path)
    {
        try
        {
            var current = Path.GetFullPath(path);
            while (!string.IsNullOrWhiteSpace(current))
            {
                if (_fileSystem.DirectoryExists(current))
                {
                    return current;
                }

                current = _fileSystem.GetParentDirectory(current);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogDebug(ex, "Unable to resolve nearest existing move destination directory.");
        }

        return null;
    }

    private static bool UnavailableManagedRootOutranksTargetBoundary(
        string path,
        MoveRootBoundary targetBoundary,
        IReadOnlyCollection<UnavailableManagedMoveRoot> unavailableRoots)
    {
        if (!FileSystemPathIdentity.TryDetectAbsoluteSyntaxForHost(
                path,
                out var pathSyntax))
        {
            return unavailableRoots.Count > 0;
        }

        var targetLength = FileSystemPathIdentity.Canonicalize(
            targetBoundary.Path,
            targetBoundary.Semantics.Syntax).Length;
        return UnavailableManagedRootOutranksBoundary(
            path,
            pathSyntax,
            targetLength,
            unavailableRoots);
    }

    private static bool UnavailableManagedRootOutranksSourceBoundary(
        string path,
        PathIdentitySnapshot sourceIdentity,
        string? configuredManagedSourceRoot,
        IReadOnlyCollection<UnavailableManagedMoveRoot> unavailableRoots) =>
        UnavailableManagedRootOutranksBoundary(
            path,
            sourceIdentity.Syntax,
            configuredManagedSourceRoot?.Length ?? -1,
            unavailableRoots);

    private static bool UnavailableManagedRootOutranksBoundary(
        string path,
        FileSystemPathSyntax pathSyntax,
        int validBoundaryLength,
        IReadOnlyCollection<UnavailableManagedMoveRoot> unavailableRoots)
    {
        foreach (var unavailable in unavailableRoots)
        {
            var boundary = unavailable.CanonicalPath ?? unavailable.Root.Path;
            if (boundary.Length < validBoundaryLength)
            {
                continue;
            }
            if (FileSystemPathIdentity.StoredBoundaryMayContainPath(
                    boundary,
                    path,
                    pathSyntax,
                    unavailable.Root.CaseSensitivityMode))
            {
                return true;
            }
        }

        return false;
    }

    private static MoveRootBoundary? FindAllowedMoveRoot(
        string path,
        IReadOnlyCollection<MoveRootBoundary> allowedRoots) =>
        allowedRoots
            .Where(root => FileSystemPathIdentity.IsSameOrInside(
                path,
                root.Path,
                root.Semantics))
            .OrderByDescending(root => FileSystemPathIdentity.Canonicalize(
                root.Path,
                root.Semantics.Syntax).Length)
            // If OutputPath aliases a configured RootFolder, the persisted managed-root
            // generation is the stronger authority and must win an equal-depth tie.
            .ThenByDescending(root => root.IsManagedRoot)
            .FirstOrDefault();

    private string? FindConfiguredManagedSourceRoot(
        string source,
        PathIdentitySnapshot sourceIdentity,
        IReadOnlyCollection<RootFolder> rootFolders)
    {
        var candidates = new List<string>();
        foreach (var rootFolder in rootFolders)
        {
            var rootPath = TryNormalizeMoveRoot(
                rootFolder.Path,
                $"root folder {rootFolder.Id}");
            if (rootPath == null)
            {
                continue;
            }

            var persistedSemantics = RootFolderPathSemantics.ResolvePersisted(rootFolder);
            var semanticsCandidates = persistedSemantics.HasValue
                ? new[] { persistedSemantics.Value.Semantics, sourceIdentity.Semantics }
                : new[] { sourceIdentity.Semantics };
            var containsSource = false;
            foreach (var semantics in semanticsCandidates)
            {
                if (semantics.Syntax != sourceIdentity.Syntax)
                {
                    continue;
                }

                try
                {
                    if (FileSystemPathIdentity.IsSameOrInside(
                            source,
                            rootPath,
                            semantics))
                    {
                        containsSource = true;
                        break;
                    }
                }
                catch (Exception exception) when (exception is
                    ArgumentException or InvalidOperationException
                        or NotSupportedException or PathTooLongException
                        or System.Security.SecurityException)
                {
                    // An unsafe comparison cannot grant source mutation authority.
                }
            }

            if (containsSource)
            {
                candidates.Add(rootPath);
            }
        }

        return candidates
            .OrderByDescending(path => path.Length)
            .FirstOrDefault();
    }

    private static MoveRootBoundary? FindExactManagedMoveRoot(
        string configuredRoot,
        IReadOnlyCollection<MoveRootBoundary> allowedRoots) =>
        allowedRoots
            .Where(root => root.IsManagedRoot)
            .FirstOrDefault(root =>
            {
                try
                {
                    return FileSystemPathIdentity.AreEquivalent(
                        configuredRoot,
                        root.Path,
                        root.Semantics);
                }
                catch (Exception exception) when (exception is
                    ArgumentException or InvalidOperationException
                        or NotSupportedException or PathTooLongException
                        or System.Security.SecurityException)
                {
                    return false;
                }
            });

    private static bool SourceStateMatches(
        string currentPath,
        string expectedPath,
        FileSystemPathSemantics semantics)
    {
        try
        {
            return FileSystemPathIdentity.AreEquivalent(
                currentPath,
                expectedPath,
                semantics);
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool AreSameMoveEndpoint(
        string source,
        PathIdentitySnapshot sourceIdentity,
        string target,
        PathIdentitySnapshot targetIdentity) =>
        FileSystemPathIdentity.AreEquivalentEndpoints(
            source,
            sourceIdentity,
            target,
            targetIdentity);

    private sealed record UnavailableManagedMoveRoot(
        RootFolder Root,
        string? CanonicalPath);

    private sealed record MoveRootBoundary(
        string Path,
        FileSystemPathSemantics Semantics,
        FileSystemCaseSensitivityMode CaseSensitivityMode,
        DirectoryObjectIdentityResolution DirectoryIdentity,
        bool IsManagedRoot,
        int? ManagedRootFolderId);

    private static BadRequestObjectResult ValidationResult(
        string code,
        string message,
        string? field = null,
        string? resolvedDestination = null) =>
        new(new
        {
            code,
            field,
            message,
            resolvedDestination
        });

    private static BadRequestObjectResult DestinationValidationResult(
        string code,
        string message,
        string? resolvedDestination = null) =>
        ValidationResult(
            code,
            message,
            "destinationPath",
            resolvedDestination);

    private static IActionResult ToApplicationExceptionResult(ListenarrApplicationException exception) =>
        exception switch
        {
            ApplicationNotFoundException => new NotFoundObjectResult(new { message = exception.SafeDetail, code = exception.Code }),
            ApplicationConflictException => new ConflictObjectResult(new { message = exception.SafeDetail, code = exception.Code }),
            ApplicationValidationException when exception.Code.StartsWith(
                "source_",
                StringComparison.Ordinal) => ValidationResult(
                    exception.Code,
                    exception.SafeDetail,
                    "sourcePath"),
            ApplicationValidationException when exception.Code.StartsWith(
                "destination_",
                StringComparison.Ordinal) || exception.Code == "identical_move_endpoint" =>
                DestinationValidationResult(
                    exception.Code,
                    exception.SafeDetail),
            ApplicationValidationException => ValidationResult(
                exception.Code,
                exception.SafeDetail),
            _ => new ObjectResult(new { message = exception.SafeDetail, code = exception.Code })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            }
        };
}
