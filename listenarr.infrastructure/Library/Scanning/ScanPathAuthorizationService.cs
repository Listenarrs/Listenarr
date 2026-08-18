using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Scanning;

internal sealed partial class ScanPathAuthorizationService(
    IConfigurationService configurationService,
    IRootFolderService rootFolderService,
    IFileSystemSemanticsResolver semanticsResolver,
    IDirectoryObjectIdentityResolver directoryObjectIdentityResolver,
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
                "A configured root that may contain the scan path has unavailable or ambiguous filesystem identity.");
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
        var physicalCapture = await TryCapturePhysicalIdentityAsync(
            boundary,
            fullPath,
            cancellationToken);
        if (!physicalCapture.Success)
        {
            return ScanPathAuthorizationResult.Rejected(
                ScanPathAuthorizationFailure.IdentityUnavailable,
                physicalCapture.Error
                    ?? "The scan path physical identity could not be established safely.");
        }

        return ScanPathAuthorizationResult.Authorized(
            fullPath,
            identity,
            physicalCapture.Identity);
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

    private async Task<PhysicalIdentityCapture> TryCapturePhysicalIdentityAsync(
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

            var limitedBoundary = false;
            DirectoryObjectIdentityResolution? verifiedBoundaryIdentity = null;
            if (authorizedRoot.RequiresEnrollment
                && authorizedRoot.DirectoryObjectIdentityVersion.HasValue
                && !string.IsNullOrWhiteSpace(authorizedRoot.DirectoryObjectIdentity))
            {
                var enrolled = await directoryObjectIdentityResolver.ResolveExistingAsync(
                    canonicalBoundary,
                    authorizedRoot.DirectoryObjectIdentityVersion.Value,
                    authorizedRoot.DirectoryObjectIdentity,
                    cancellationToken);
                if (enrolled.IsAvailable)
                {
                    verifiedBoundaryIdentity = enrolled;
                }
                else
                {
                    var liveBoundary = await directoryObjectIdentityResolver.ResolveAsync(
                        canonicalBoundary,
                        cancellationToken);
                    if (enrolled.FailureKind
                        == DirectoryObjectIdentityFailureKind.LegacyWeakIdentity)
                    {
                        if (!liveBoundary.IsAvailable)
                        {
                            return PhysicalIdentityCapture.Failed(
                                liveBoundary.UnavailableReason
                                    ?? enrolled.UnavailableReason
                                    ?? "The configured scan root physical identity cannot be verified.");
                        }

                        verifiedBoundaryIdentity = liveBoundary;
                        limitedBoundary = true;
                    }
                    else if (enrolled.FailureKind
                        == DirectoryObjectIdentityFailureKind.IdentityUnsupported)
                    {
                        // Distinguish an unsupported historical identity version from a
                        // live filesystem that genuinely lacks durable generation support.
                        if (liveBoundary.IsAvailable
                            || liveBoundary.FailureKind
                                != DirectoryObjectIdentityFailureKind.IdentityUnsupported)
                        {
                            return PhysicalIdentityCapture.Failed(
                                enrolled.UnavailableReason
                                    ?? "The configured scan root physical identity cannot be verified.");
                        }

                        limitedBoundary = true;
                    }
                    else
                    {
                        return PhysicalIdentityCapture.Failed(
                            enrolled.UnavailableReason
                                ?? "The configured scan root no longer identifies its enrolled physical generation.");
                    }
                }
            }
            else if (authorizedRoot.RequiresEnrollment)
            {
                var liveBoundary = await directoryObjectIdentityResolver.ResolveAsync(
                    canonicalBoundary,
                    cancellationToken);
                if (liveBoundary.IsAvailable)
                {
                    return PhysicalIdentityCapture.Failed(
                        "The configured scan root has not been enrolled with its available physical generation.");
                }
                if (liveBoundary.FailureKind
                    != DirectoryObjectIdentityFailureKind.IdentityUnsupported)
                {
                    return PhysicalIdentityCapture.Failed(
                        liveBoundary.UnavailableReason
                            ?? "The configured scan root physical identity is unavailable.");
                }

                limitedBoundary = true;
            }

            var scanRootResolution = await directoryObjectIdentityResolver.ResolveAsync(
                canonicalScanPath,
                cancellationToken);
            if (!scanRootResolution.IsAvailable
                && scanRootResolution.FailureKind
                    != DirectoryObjectIdentityFailureKind.IdentityUnsupported)
            {
                return PhysicalIdentityCapture.Failed(
                    scanRootResolution.UnavailableReason
                        ?? "The scan root physical identity is unavailable.");
            }
            var limitedScan = limitedBoundary || !scanRootResolution.IsAvailable;

            using var boundary = PinnedDirectoryCreation.OpenPinnedBoundary(
                canonicalBoundary);
            cancellationToken.ThrowIfCancellationRequested();
            if (!boundary.VisiblePathMatches()
                || (verifiedBoundaryIdentity?.IsAvailable == true
                    && !boundary.MatchesManagedDirectoryIdentity(
                        verifiedBoundaryIdentity.Version,
                        verifiedBoundaryIdentity.Value)))
            {
                return PhysicalIdentityCapture.Failed(
                    "The configured scan boundary changed after its enrolled physical identity was verified.");
            }

            using var scanRoot = OpenRelativeScanRoot(
                boundary,
                canonicalBoundary,
                canonicalScanPath);
            if (!boundary.VisiblePathMatches()
                || !scanRoot.VisiblePathMatches())
            {
                return PhysicalIdentityCapture.Failed(
                    "The configured scan boundary changed while its physical identity was being captured.");
            }

            if (limitedScan)
            {
                // Operation-local pinned path authority never authorizes destructive
                // reconciliation or filesystem mutation.
                return PhysicalIdentityCapture.Captured(
                    ScanPathPhysicalIdentity.PinnedPathOnly());
            }

            var boundaryIdentity = boundary.GetDirectoryObjectIdentity();
            var scanRootIdentity = scanRoot.GetDirectoryObjectIdentity();
            if (!boundary.VisiblePathMatches()
                || !scanRoot.VisiblePathMatches())
            {
                return PhysicalIdentityCapture.Failed(
                    "The configured scan boundary changed while its physical identity was being captured.");
            }

            return PhysicalIdentityCapture.Captured(
                new ScanPathPhysicalIdentity(
                    boundaryIdentity,
                    scanRootIdentity));
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            return PhysicalIdentityCapture.Failed(exception switch
            {
                DirectoryNotFoundException =>
                    "The scan path no longer exists beneath its configured root.",
                _ when authorizedRoot.RequiresEnrollment =>
                    "The configured scan root no longer identifies its enrolled physical generation.",
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
        PersistedRootFolderPathSemantics? PersistedSemantics,
        int? DirectoryObjectIdentityVersion,
        string? DirectoryObjectIdentity,
        string? DirectoryObjectIdentityUnavailableReason);

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
        bool RequiresEnrollment,
        int? DirectoryObjectIdentityVersion,
        string? DirectoryObjectIdentity,
        string? DirectoryObjectIdentityUnavailableReason);

    private sealed record PhysicalIdentityCapture(
        bool Success,
        ScanPathPhysicalIdentity Identity,
        string? Error)
    {
        public static PhysicalIdentityCapture Captured(
            ScanPathPhysicalIdentity identity) =>
            new(true, identity, null);

        public static PhysicalIdentityCapture Failed(string error) =>
            new(false, default, error);
    }
}
