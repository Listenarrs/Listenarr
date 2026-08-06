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

    private async Task AddAllowedMoveRootAsync(
        List<MoveRootBoundary> allowedRoots,
        string? normalizedRoot,
        FileSystemCaseSensitivityMode caseSensitivityMode,
        IDirectoryObjectIdentityResolver directoryIdentityResolver,
        CancellationToken cancellationToken,
        int? expectedDirectoryIdentityVersion = null,
        string? expectedDirectoryIdentity = null,
        string? directoryIdentityUnavailableReason = null,
        FileSystemPathSemantics? persistedSemantics = null)
    {
        if (string.IsNullOrEmpty(normalizedRoot))
        {
            return;
        }

        var resolution = await _semanticsResolver.ResolveAsync(
            normalizedRoot,
            caseSensitivityMode,
            cancellationToken);
        var semantics = resolution.State == PathIdentityState.Valid
            ? resolution.Semantics
            : persistedSemantics;
        if (!semantics.HasValue)
        {
            _logger.LogWarning(
                "Skipping move boundary {Root}: {Reason}",
                LogRedaction.SanitizeFilePath(normalizedRoot),
                resolution.Reason ?? "filesystem identity unavailable");
            return;
        }

        var hasPersistedDirectoryIdentity =
            expectedDirectoryIdentityVersion.HasValue
            || !string.IsNullOrWhiteSpace(expectedDirectoryIdentity)
            || !string.IsNullOrWhiteSpace(directoryIdentityUnavailableReason);
        DirectoryObjectIdentityResolution directoryIdentity;
        if (hasPersistedDirectoryIdentity)
        {
            var current = expectedDirectoryIdentityVersion.HasValue
                && !string.IsNullOrWhiteSpace(expectedDirectoryIdentity)
                    ? await directoryIdentityResolver.ResolveExistingAsync(
                        normalizedRoot,
                        expectedDirectoryIdentityVersion.Value,
                        expectedDirectoryIdentity,
                        cancellationToken)
                    : DirectoryObjectIdentityResolution.Unavailable(
                        directoryIdentityUnavailableReason
                            ?? "The configured root has incomplete persisted physical identity.");
            directoryIdentity = current.IsAvailable
                && current.Version == expectedDirectoryIdentityVersion
                && string.Equals(
                    current.Value,
                    expectedDirectoryIdentity,
                    StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace(directoryIdentityUnavailableReason)
                    ? current
                    : DirectoryObjectIdentityResolution.Unavailable(
                        current.UnavailableReason
                            ?? directoryIdentityUnavailableReason
                            ?? "The configured root no longer identifies its enrolled physical generation.");
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
            if (hasPersistedDirectoryIdentity
                || (caseSensitivityMode != FileSystemCaseSensitivityMode.Auto
                    && allowedRoots[existingIndex].CaseSensitivityMode
                        == FileSystemCaseSensitivityMode.Auto))
            {
                allowedRoots[existingIndex] = new MoveRootBoundary(
                    normalizedRoot,
                    semantics.Value,
                    caseSensitivityMode,
                    directoryIdentity,
                    hasPersistedDirectoryIdentity);
            }

            return;
        }

        allowedRoots.Add(new MoveRootBoundary(
            normalizedRoot,
            semantics.Value,
            caseSensitivityMode,
            directoryIdentity,
            hasPersistedDirectoryIdentity));
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

    private sealed record MoveRootBoundary(
        string Path,
        FileSystemPathSemantics Semantics,
        FileSystemCaseSensitivityMode CaseSensitivityMode,
        DirectoryObjectIdentityResolution DirectoryIdentity,
        bool IsManagedRoot);

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
