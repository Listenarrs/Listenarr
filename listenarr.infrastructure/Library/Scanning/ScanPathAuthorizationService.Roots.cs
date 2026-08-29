using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Scanning;

internal sealed partial class ScanPathAuthorizationService
{
    private async Task<AuthorizedRootSet> LoadAuthorizedRootsAsync(
        CancellationToken cancellationToken)
    {
        var configuredRoots = await rootFolderService.GetAllAsync();
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
        // RootFolders are the authoritative managed-storage boundaries. OutputPath is
        // retained only as a legacy fallback for databases that have not configured
        // any root folders yet; otherwise a stale OutputPath must not grant independent
        // scan authority outside managed storage.
        if (configuredRoots.Count == 0)
        {
            var settings = await configurationService.GetApplicationSettingsAsync();
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
        }

        var roots = new List<AuthorizedRoot>();
        var unavailableRoots = new List<UnavailableAuthorizedRoot>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetStoredFullPath(candidate.Path, out var fullPath))
            {
                LogUnavailableCandidate(
                    candidate,
                    "Ignoring invalid configured scan root {Path}");
                if (candidate.RequiresEnrollment)
                {
                    unavailableRoots.Add(new UnavailableAuthorizedRoot(
                        candidate.Path,
                        candidate.RequestedMode));
                }
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
                if (candidate.RequiresEnrollment)
                {
                    unavailableRoots.Add(new UnavailableAuthorizedRoot(
                        fullPath,
                        candidate.RequestedMode));
                }
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
                unavailableRoots.Add(new UnavailableAuthorizedRoot(
                    fullPath,
                    candidate.RequestedMode));
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

        return new AuthorizedRootSet(roots, unavailableRoots);
    }
}
