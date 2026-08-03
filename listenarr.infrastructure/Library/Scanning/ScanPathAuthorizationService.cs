using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Scanning;

internal sealed class ScanPathAuthorizationService(
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

        IReadOnlyList<AuthorizedRoot> roots;
        try
        {
            roots = await LoadAuthorizedRootsAsync(cancellationToken);
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

        if (roots.Count == 0)
        {
            return ScanPathAuthorizationResult.Rejected(
                ScanPathAuthorizationFailure.NoConfiguredRoots,
                "No configured scan roots are available.");
        }

        var boundary = roots
            .Where(root => FileSystemPathIdentity.IsSameOrInside(
                fullPath,
                root.Path,
                root.Semantics))
            .OrderByDescending(root => root.Path.Length)
            .FirstOrDefault();
        if (boundary == null)
        {
            return ScanPathAuthorizationResult.Rejected(
                ScanPathAuthorizationFailure.OutsideConfiguredRoots,
                "The scan path is not within a configured root folder.");
        }

        var identity = PathIdentitySnapshot.FromResolution(
            boundary.Semantics,
            boundary.RequestedMode,
            boundary.Path,
            fullPath);
        if (!TryCapturePhysicalIdentity(
                boundary.Path,
                fullPath,
                boundary.Semantics,
                out var physicalIdentity,
                out var physicalError))
        {
            return ScanPathAuthorizationResult.Rejected(
                ScanPathAuthorizationFailure.IdentityUnavailable,
                physicalError
                    ?? "The scan path physical identity could not be established safely.");
        }

        return ScanPathAuthorizationResult.Authorized(
            fullPath,
            identity,
            physicalIdentity);
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

        ApplicationSettings? settings;
        try
        {
            settings = await configurationService.GetApplicationSettingsAsync();
        }
        catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
        {
            logger.LogWarning(
                exception,
                "Unable to load the configured output path for a default scan");
            return ScanPathAuthorizationResult.Rejected(
                ScanPathAuthorizationFailure.ConfigurationUnavailable,
                "The configured output path could not be loaded safely.");
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

    private async Task<IReadOnlyList<AuthorizedRoot>> LoadAuthorizedRootsAsync(
        CancellationToken cancellationToken)
    {
        var configuredRoots = await rootFolderService.GetAllAsync();
        var settings = await configurationService.GetApplicationSettingsAsync();
        var candidates = configuredRoots
            .Select(root => new RootCandidate(
                root.Path,
                root.CaseSensitivityMode))
            .ToList();
        if (!string.IsNullOrWhiteSpace(settings?.OutputPath))
        {
            candidates.Add(new RootCandidate(
                settings.OutputPath,
                FileSystemCaseSensitivityMode.Auto));
        }

        var roots = new List<AuthorizedRoot>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetStoredFullPath(candidate.Path, out var fullPath))
            {
                logger.LogWarning(
                    "Ignoring invalid configured scan root {Path}",
                    LogRedaction.SanitizeFilePath(candidate.Path));
                continue;
            }

            var resolution = await semanticsResolver.ResolveAsync(
                fullPath,
                candidate.RequestedMode,
                cancellationToken);
            if (resolution.State != PathIdentityState.Valid)
            {
                logger.LogWarning(
                    "Ignoring configured scan root {Path}: {Reason}",
                    LogRedaction.SanitizeFilePath(candidate.Path),
                    resolution.Reason);
                continue;
            }

            var canonical = FileSystemPathIdentity.Canonicalize(
                fullPath,
                resolution.Semantics.Syntax);
            if (IsFilesystemRoot(canonical, resolution.Semantics))
            {
                logger.LogWarning(
                    "Ignoring unsafe filesystem-root scan boundary {Path}",
                    LogRedaction.SanitizeFilePath(candidate.Path));
                continue;
            }

            var duplicate = roots.FirstOrDefault(existing =>
                existing.Semantics.Syntax == resolution.Semantics.Syntax
                && FileSystemPathIdentity.AreEquivalent(
                    existing.Path,
                    canonical,
                    existing.Semantics)
                && FileSystemPathIdentity.AreEquivalent(
                    existing.Path,
                    canonical,
                    resolution.Semantics));
            if (duplicate != null)
            {
                if (duplicate.Semantics.CaseSensitivity
                    != resolution.Semantics.CaseSensitivity)
                {
                    throw new InvalidOperationException(
                        $"Configured scan root '{fullPath}' has conflicting filesystem semantics.");
                }

                continue;
            }

            roots.Add(new AuthorizedRoot(
                canonical,
                resolution.Semantics,
                candidate.RequestedMode));
        }

        return roots;
    }

    private static bool TryCapturePhysicalIdentity(
        string boundaryPath,
        string scanPath,
        FileSystemPathSemantics semantics,
        out ScanPathPhysicalIdentity identity,
        out string? error)
    {
        identity = default;
        error = null;
        try
        {
            var canonicalBoundary = FileSystemPathIdentity.Canonicalize(
                boundaryPath,
                semantics.Syntax);
            var canonicalScanPath = FileSystemPathIdentity.Canonicalize(
                scanPath,
                semantics.Syntax);
            using var boundary = PinnedDirectoryCreation.OpenPinnedBoundary(
                canonicalBoundary);
            var boundaryIdentity = boundary.GetDirectoryObjectIdentity();
            using var scanRoot = OpenRelativeScanRoot(
                boundary,
                canonicalBoundary,
                canonicalScanPath);
            if (!boundary.VisiblePathMatches()
                || !scanRoot.VisiblePathMatches())
            {
                error = "The configured scan boundary changed while its physical identity was being captured.";
                return false;
            }

            identity = new ScanPathPhysicalIdentity(
                boundaryIdentity,
                scanRoot.GetDirectoryObjectIdentity());
            return true;
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            error = exception switch
            {
                DirectoryNotFoundException =>
                    "The scan path no longer exists beneath its configured root.",
                _ =>
                    "The scan path contains a linked, replaced, or unavailable directory component."
            };
            return false;
        }
    }

    private static PinnedDirectoryCreation.PinnedDirectoryAnchor
        OpenRelativeScanRoot(
            PinnedDirectoryCreation.PinnedDirectoryAnchor boundary,
            string boundaryPath,
            string scanPath)
    {
        var current = boundary.Duplicate();
        try
        {
            var relative = Path.GetRelativePath(boundaryPath, scanPath);
            if (relative == ".")
            {
                return current;
            }

            foreach (var segment in relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment is "." or "..")
                {
                    throw new InvalidOperationException(
                        "The scan path contains navigation segments outside its configured root.");
                }

                var next = current.OpenExistingChild(segment);
                current.Dispose();
                current = next;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
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
        FileSystemCaseSensitivityMode RequestedMode);

    private sealed record AuthorizedRoot(
        string Path,
        FileSystemPathSemantics Semantics,
        FileSystemCaseSensitivityMode RequestedMode);
}
