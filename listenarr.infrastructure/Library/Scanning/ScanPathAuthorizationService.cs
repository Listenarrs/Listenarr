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
                root.CaseSensitivityMode,
                RequiresEnrollment: true,
                RootFolderPathSemantics.ResolvePersisted(root),
                root.DirectoryObjectIdentityVersion,
                root.DirectoryObjectIdentity,
                root.DirectoryObjectIdentityUnavailableReason))
            .ToList();
        if (!string.IsNullOrWhiteSpace(settings?.OutputPath))
        {
            candidates.Add(new RootCandidate(
                settings.OutputPath,
                FileSystemCaseSensitivityMode.Auto,
                RequiresEnrollment: false,
                PersistedSemantics: null,
                DirectoryObjectIdentityVersion: null,
                DirectoryObjectIdentity: null,
                DirectoryObjectIdentityUnavailableReason: null));
        }

        var roots = new List<AuthorizedRoot>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetStoredFullPath(candidate.Path, out var fullPath))
            {
                LogUnavailableCandidate(
                    candidate,
                    "Ignoring invalid configured scan root {Path}");
                continue;
            }

            var resolution = await semanticsResolver.ResolveAsync(
                fullPath,
                candidate.RequestedMode,
                cancellationToken);
            if (resolution.State != PathIdentityState.Valid)
            {
                LogUnavailableCandidate(
                    candidate,
                    "Ignoring configured scan root {Path}: {Reason}",
                    resolution.Reason);
                continue;
            }
            if (candidate.RequiresEnrollment
                && (!candidate.PersistedSemantics.HasValue
                    || candidate.PersistedSemantics.Value.DetectAmbiguousCaseMatches
                    || candidate.PersistedSemantics.Value.Semantics.Syntax
                        != resolution.Semantics.Syntax
                    || candidate.PersistedSemantics.Value.Semantics.CaseSensitivity
                        != resolution.Semantics.CaseSensitivity))
            {
                LogUnavailableCandidate(
                    candidate,
                    "Ignoring configured scan root {Path}: live filesystem semantics do not match its persisted root semantics.");
                continue;
            }

            var canonical = FileSystemPathIdentity.Canonicalize(
                fullPath,
                resolution.Semantics.Syntax);
            if (IsFilesystemRoot(canonical, resolution.Semantics))
            {
                LogUnavailableCandidate(
                    candidate,
                    "Ignoring unsafe filesystem-root scan boundary {Path}");
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
                candidate.RequestedMode,
                candidate.RequiresEnrollment,
                candidate.DirectoryObjectIdentityVersion,
                candidate.DirectoryObjectIdentity,
                candidate.DirectoryObjectIdentityUnavailableReason));
        }

        return roots;
    }

    private void LogUnavailableCandidate(
        RootCandidate candidate,
        string message,
        string? reason = null)
    {
        var sanitizedPath = LogRedaction.SanitizeFilePath(candidate.Path);
        if (candidate.RequiresEnrollment)
        {
            if (reason == null)
            {
                logger.LogWarning(message, sanitizedPath);
            }
            else
            {
                logger.LogWarning(message, sanitizedPath, reason);
            }
            return;
        }

        if (reason == null)
        {
            logger.LogDebug(message, sanitizedPath);
        }
        else
        {
            logger.LogDebug(message, sanitizedPath, reason);
        }
    }

    private static async Task<PhysicalIdentityCapture> TryCapturePhysicalIdentityAsync(
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
            var boundaryIdentity = boundary.GetDirectoryObjectIdentity();
            if (authorizedRoot.RequiresEnrollment
                && !ManagedDirectoryIdentity.MatchesNativeIdentity(
                    authorizedRoot.DirectoryObjectIdentityVersion,
                    authorizedRoot.DirectoryObjectIdentity,
                    boundaryIdentity))
            {
                throw new InvalidOperationException(
                    "The configured scan root no longer identifies its authorized physical generation.");
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

            return PhysicalIdentityCapture.Captured(
                new ScanPathPhysicalIdentity(
                    boundaryIdentity,
                    scanRoot.GetDirectoryObjectIdentity()));
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
        FileSystemCaseSensitivityMode RequestedMode,
        bool RequiresEnrollment,
        PersistedRootFolderPathSemantics? PersistedSemantics,
        int? DirectoryObjectIdentityVersion,
        string? DirectoryObjectIdentity,
        string? DirectoryObjectIdentityUnavailableReason);

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
