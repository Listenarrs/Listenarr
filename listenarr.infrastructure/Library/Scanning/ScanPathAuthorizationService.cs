using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Scanning;

internal sealed partial class ScanPathAuthorizationService(
    IConfigurationService configurationService,
    IRootFolderService rootFolderService,
    IFileSystemSemanticsResolver semanticsResolver,
    ILogger<ScanPathAuthorizationService> logger) : IScanPathAuthorizationService
{
    public async Task<ScanPathAuthorizationResult> AuthorizeAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetFullPath(path, out var fullPath))
        {
            return ScanPathAuthorizationResult.Rejected(
                ScanPathAuthorizationFailure.InvalidPath,
                "The scan path is invalid.");
        }

        AuthorizedRootSet rootSet;
        try
        {
            rootSet = await LoadAuthorizedRootsAsync(cancellationToken);
        }
        catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
        {
            logger.LogWarning(
                exception,
                "Unable to load configured scan roots while authorizing {Path}",
                LogRedaction.SanitizeFilePath(path));
            return ScanPathAuthorizationResult.Rejected(
                ScanPathAuthorizationFailure.ConfigurationUnavailable,
                "Configured scan roots could not be loaded safely.");
        }

        if (!FileSystemPathIdentity.TryDetectAbsoluteSyntaxForHost(
                fullPath,
                out var pathSyntax))
        {
            return ScanPathAuthorizationResult.Rejected(
                ScanPathAuthorizationFailure.InvalidPath,
                "The scan path does not have a valid host filesystem identity.");
        }

        var boundary = rootSet.Roots
            .Where(root => FileSystemPathIdentity.IsSameOrInside(
                fullPath,
                root.Path,
                root.Semantics))
            .OrderByDescending(root => root.Path.Length)
            .FirstOrDefault();
        var unavailableRootLength = rootSet.UnavailableRoots
            .Where(root => FileSystemPathIdentity.StoredBoundaryMayContainPath(
                root.Path,
                fullPath,
                pathSyntax,
                root.RequestedMode))
            .Select(root => root.Path.Length)
            .DefaultIfEmpty(-1)
            .Max();
        var boundaryLength = boundary?.Path.Length ?? -1;
        if (unavailableRootLength >= boundaryLength
            && unavailableRootLength >= 0)
        {
            return ScanPathAuthorizationResult.Rejected(
                ScanPathAuthorizationFailure.ConfigurationUnavailable,
                "A configured root that may contain the scan path has unavailable or ambiguous path/case semantics.");
        }
        if (boundary == null)
        {
            if (rootSet.Roots.Count == 0)
            {
                return ScanPathAuthorizationResult.Rejected(
                    ScanPathAuthorizationFailure.NoConfiguredRoots,
                    "No configured scan roots are available.");
            }

            return ScanPathAuthorizationResult.Rejected(
                ScanPathAuthorizationFailure.OutsideConfiguredRoots,
                "The scan path is not within a configured root folder.");
        }

        var identity = PathIdentitySnapshot.FromResolution(
            boundary.Semantics,
            boundary.RequestedMode,
            boundary.Path,
            fullPath);
        var pinValidation = TryValidatePinnedScanPath(
            boundary,
            fullPath,
            cancellationToken);
        if (!pinValidation.Success)
        {
            return ScanPathAuthorizationResult.Rejected(
                ScanPathAuthorizationFailure.IdentityUnavailable,
                pinValidation.Error
                    ?? "The scan path could not be pinned and revalidated safely for this operation.");
        }

        // Compatibility shape only: scan authority is path/case identity plus
        // this live no-follow validation. No physical object token crosses the
        // authorization boundary.
        return ScanPathAuthorizationResult.Authorized(
            fullPath,
            identity,
            ScanPathPhysicalIdentity.PinnedPathOnly());
    }

    public async Task<ScanPathAuthorizationResult> ResolveDefaultAsync(
        string? preferredPath,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath))
        {
            if (!TryGetStoredFullPath(preferredPath, out var storedPreferredPath))
            {
                return ScanPathAuthorizationResult.Rejected(
                    ScanPathAuthorizationFailure.InvalidPath,
                    "The persisted scan path is unavailable on this host.");
            }

            return await AuthorizeAsync(storedPreferredPath, cancellationToken);
        }

        RootFolder? defaultRoot;
        try
        {
            defaultRoot = await rootFolderService.GetDefaultAsync();
        }
        catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
        {
            logger.LogWarning(
                exception,
                "Unable to load the configured default root for a default scan");
            return ScanPathAuthorizationResult.Rejected(
                ScanPathAuthorizationFailure.ConfigurationUnavailable,
                "The configured default scan root could not be loaded safely.");
        }

        if (defaultRoot != null)
        {
            if (!TryGetStoredFullPath(defaultRoot.Path, out var storedDefaultRoot))
            {
                return ScanPathAuthorizationResult.Rejected(
                    ScanPathAuthorizationFailure.InvalidPath,
                    "The configured default root is unavailable on this host.");
            }

            return await AuthorizeAsync(storedDefaultRoot, cancellationToken);
        }

        ApplicationSettings? settings;
        try
        {
            settings = await configurationService.GetApplicationSettingsAsync();
        }
        catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
        {
            logger.LogWarning(
                exception,
                "Unable to load the legacy configured output path for a default scan");
            return ScanPathAuthorizationResult.Rejected(
                ScanPathAuthorizationFailure.ConfigurationUnavailable,
                "The legacy configured output path could not be loaded safely.");
        }

        if (string.IsNullOrWhiteSpace(settings?.OutputPath))
        {
            return ScanPathAuthorizationResult.Rejected(
                ScanPathAuthorizationFailure.NoConfiguredRoots,
                "No default scan path is configured.");
        }

        if (!TryGetStoredFullPath(settings.OutputPath, out var storedOutputPath))
        {
            return ScanPathAuthorizationResult.Rejected(
                ScanPathAuthorizationFailure.InvalidPath,
                "The configured output path is unavailable on this host.");
        }

        return await AuthorizeAsync(storedOutputPath, cancellationToken);
    }

    private static PinValidation TryValidatePinnedScanPath(
        AuthorizedRoot authorizedRoot,
        string scanPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var canonicalBoundary = FileSystemPathIdentity.Canonicalize(
                authorizedRoot.Path,
                authorizedRoot.Semantics.Syntax);
            var canonicalScanPath = FileSystemPathIdentity.Canonicalize(
                scanPath,
                authorizedRoot.Semantics.Syntax);

            using var boundary = PinnedDirectoryCreation.OpenPinnedBoundary(
                canonicalBoundary);
            cancellationToken.ThrowIfCancellationRequested();
            if (!boundary.VisiblePathMatches())
            {
                return PinValidation.Failed(
                    "The configured scan boundary changed while it was being pinned.");
            }

            using var scanRoot = OpenRelativeScanRoot(
                boundary,
                canonicalBoundary,
                canonicalScanPath);
            if (!boundary.VisiblePathMatches()
                || !scanRoot.VisiblePathMatches())
            {
                return PinValidation.Failed(
                    "The scan path changed while its current directory handles were being verified.");
            }

            return PinValidation.Valid();
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            return PinValidation.Failed(exception switch
            {
                DirectoryNotFoundException =>
                    "The scan path no longer exists beneath its configured root.",
                _ =>
                    "The scan path contains a linked, replaced, or unavailable directory component."
            });
        }
    }

    private static bool IsFilesystemRoot(
        string path,
        FileSystemPathSemantics semantics)
    {
        var root = Path.GetPathRoot(path);
        return !string.IsNullOrWhiteSpace(root)
            && FileSystemPathIdentity.AreEquivalent(path, root, semantics);
    }

    private static bool TryGetFullPath(
        string? path,
        out string fullPath)
    {
        fullPath = string.Empty;
        return !string.IsNullOrWhiteSpace(path)
            && FileSystemPathIdentity.TryCanonicalizeStoredAbsolutePathForHost(
                path,
                out fullPath,
                out _);
    }

    private static bool TryGetStoredFullPath(
        string? path,
        out string fullPath)
    {
        fullPath = string.Empty;
        return !string.IsNullOrWhiteSpace(path)
            && FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                path,
                out fullPath,
                out _);
    }

    private sealed record RootCandidate(
        string Path,
        FileSystemCaseSensitivityMode RequestedMode,
        bool RequiresEnrollment,
        PersistedRootFolderPathSemantics? PersistedSemantics);

    private sealed record AuthorizedRootSet(
        IReadOnlyList<AuthorizedRoot> Roots,
        IReadOnlyList<UnavailableAuthorizedRoot> UnavailableRoots);

    private sealed record UnavailableAuthorizedRoot(
        string Path,
        FileSystemCaseSensitivityMode RequestedMode);

    private sealed record AuthorizedRoot(
        string Path,
        FileSystemPathSemantics Semantics,
        FileSystemCaseSensitivityMode RequestedMode,
        bool RequiresEnrollment);

    private sealed record PinValidation(
        bool Success,
        string? Error)
    {
        public static PinValidation Valid() => new(true, null);

        public static PinValidation Failed(string error) =>
            new(false, error);
    }
}
