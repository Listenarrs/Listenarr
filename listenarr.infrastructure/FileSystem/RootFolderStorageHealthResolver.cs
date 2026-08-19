using System.Security.Cryptography;
using System.Text;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed class RootFolderStorageHealthResolver(
    IDirectoryObjectIdentityResolver identityResolver,
    IFileSystemSemanticsResolver? semanticsResolver = null,
    Func<string, bool?>? readOnlyFileSystemProbe = null)
    : IRootFolderStorageHealthResolver
{
    private const string ConfirmationTokenVersion = "root-storage-v1";
    private readonly IFileSystemSemanticsResolver _semanticsResolver =
        semanticsResolver ?? new FileSystemSemanticsResolver();
    private readonly Func<string, bool?> _readOnlyFileSystemProbe =
        readOnlyFileSystemProbe ?? ProbeReadOnlyFileSystem;

    public async Task<RootFolderStorageObservation> ResolveAsync(
        RootFolder root,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        cancellationToken.ThrowIfCancellationRequested();

        if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                root.Path,
                out var canonicalPath,
                out var pathReason))
        {
            var reason = FileSystemPathIdentity.TryDetectAbsoluteSyntax(root.Path, out _)
                && !FileSystemPathIdentity.TryDetectAbsoluteSyntaxForHost(root.Path, out _)
                    ? RootFolderStorageReason.ForeignPathSyntax
                    : RootFolderStorageReason.InvalidPath;
            return Unavailable(reason, pathReason);
        }

        var hasAuthorizedIdentity = root.DirectoryObjectIdentityVersion.HasValue
            && !string.IsNullOrWhiteSpace(root.DirectoryObjectIdentity);
        if (!hasAuthorizedIdentity)
        {
            var current = await identityResolver.ResolveAsync(canonicalPath, cancellationToken);
            if (!current.IsAvailable)
            {
                if (current.FailureKind == DirectoryObjectIdentityFailureKind.IdentityUnsupported)
                {
                    return await ValidateFilesystemSemanticsAsync(
                        root,
                        canonicalPath,
                        LimitedIdentityUnsupported(current.UnavailableReason),
                        cancellationToken);
                }

                return FromFailure(current);
            }

            return await ValidateFilesystemSemanticsAsync(
                root,
                canonicalPath,
                new RootFolderStorageObservation(
                    RootFolderStorageState.Unconfirmed,
                    RootFolderStorageReason.NoAuthorizedIdentity,
                    "Listenarr has not yet confirmed the physical directory currently at this path.",
                    CanConfirmCurrentFolder: true,
                    CanChangePath: true,
                    CanMutateFilesystem: false,
                    ConfirmationToken: CreateConfirmationToken(root, canonicalPath, current)),
                cancellationToken);
        }

        var expected = await identityResolver.ResolveExistingAsync(
            canonicalPath,
            root.DirectoryObjectIdentityVersion!.Value,
            root.DirectoryObjectIdentity!,
            cancellationToken);
        if (expected.IsAvailable)
        {
            return await ValidateFilesystemSemanticsAsync(
                root,
                canonicalPath,
                new RootFolderStorageObservation(
                    RootFolderStorageState.Healthy,
                    RootFolderStorageReason.None,
                    null,
                    CanConfirmCurrentFolder: false,
                    CanChangePath: true,
                    CanMutateFilesystem: true,
                    ConfirmationToken: null),
                cancellationToken);
        }

        if (expected.FailureKind == DirectoryObjectIdentityFailureKind.IdentityUnsupported)
        {
            var unsupportedCurrentGeneration = await identityResolver.ResolveAsync(
                canonicalPath,
                cancellationToken);
            if (unsupportedCurrentGeneration.IsAvailable)
            {
                return await ValidateFilesystemSemanticsAsync(
                    root,
                    canonicalPath,
                    UnsupportedPersistedIdentity(
                        root,
                        canonicalPath,
                        unsupportedCurrentGeneration,
                        expected.UnavailableReason),
                    cancellationToken);
            }
            if (unsupportedCurrentGeneration.FailureKind
                != DirectoryObjectIdentityFailureKind.IdentityUnsupported)
            {
                return FromFailure(unsupportedCurrentGeneration);
            }

            return await ValidateFilesystemSemanticsAsync(
                root,
                canonicalPath,
                LimitedIdentityUnsupported(
                    unsupportedCurrentGeneration.UnavailableReason
                        ?? expected.UnavailableReason),
                cancellationToken);
        }

        if (expected.FailureKind == DirectoryObjectIdentityFailureKind.LegacyWeakIdentity)
        {
            var legacyCurrentGeneration = await identityResolver.ResolveAsync(
                canonicalPath,
                cancellationToken);
            if (!legacyCurrentGeneration.IsAvailable)
            {
                return FromFailure(legacyCurrentGeneration);
            }

            return await ValidateFilesystemSemanticsAsync(
                root,
                canonicalPath,
                LimitedLegacyIdentity(
                    root,
                    canonicalPath,
                    legacyCurrentGeneration,
                    expected.UnavailableReason),
                cancellationToken);
        }

        if (expected.FailureKind != DirectoryObjectIdentityFailureKind.IdentityMismatch)
        {
            return FromFailure(expected);
        }

        // Resolve the currently visible generation separately so the confirmation token
        // is bound to the exact replacement generation the user is being asked to confirm.
        var currentGeneration = await identityResolver.ResolveAsync(canonicalPath, cancellationToken);
        if (!currentGeneration.IsAvailable)
        {
            return FromFailure(currentGeneration);
        }

        return await ValidateFilesystemSemanticsAsync(
            root,
            canonicalPath,
            new RootFolderStorageObservation(
                RootFolderStorageState.Changed,
                RootFolderStorageReason.IdentityMismatch,
                "The folder currently at this path is different from the folder Listenarr previously confirmed.",
                CanConfirmCurrentFolder: true,
                CanChangePath: true,
                CanMutateFilesystem: false,
                ConfirmationToken: CreateConfirmationToken(root, canonicalPath, currentGeneration)),
            cancellationToken);
    }

    private async Task<RootFolderStorageObservation> ValidateFilesystemSemanticsAsync(
        RootFolder root,
        string canonicalPath,
        RootFolderStorageObservation observation,
        CancellationToken cancellationToken)
    {
        var currentSemantics = await _semanticsResolver.ResolveAsync(
            canonicalPath,
            root.CaseSensitivityMode,
            cancellationToken);
        if (currentSemantics.State != PathIdentityState.Valid)
        {
            return SemanticsUnavailable(
                observation,
                RootFolderStorageReason.FilesystemSemanticsUnavailable,
                currentSemantics.Reason);
        }

        var persistedSemantics = RootFolderPathSemantics.ResolvePersisted(root);
        if (persistedSemantics == null
            || persistedSemantics.Value.DetectAmbiguousCaseMatches)
        {
            // A legacy or deliberately unconfirmed root has no prior filesystem
            // semantics authority to preserve. Explicit folder confirmation may
            // establish both its current semantics and physical generation.
            return observation.State == RootFolderStorageState.Unconfirmed
                ? observation
                : SemanticsUnavailable(
                    observation,
                    RootFolderStorageReason.FilesystemSemanticsUnavailable,
                    "The root folder has no persisted filesystem case-sensitivity authority.");
        }

        if (persistedSemantics.Value.Semantics.CaseSensitivity
            != currentSemantics.Semantics.CaseSensitivity)
        {
            return SemanticsUnavailable(
                observation,
                RootFolderStorageReason.FilesystemSemanticsChanged,
                $"Persisted case sensitivity is {persistedSemantics.Value.Semantics.CaseSensitivity}, but the live storage resolves as {currentSemantics.Semantics.CaseSensitivity}.");
        }

        if (observation.State != RootFolderStorageState.Healthy)
        {
            return observation;
        }

        var mutationCapability = ApplyMutationCapability(
            canonicalPath,
            observation,
            _readOnlyFileSystemProbe(canonicalPath));
        if (!mutationCapability.CanMutateFilesystem)
        {
            return mutationCapability;
        }

        if (root.CaseSensitivityMode == FileSystemCaseSensitivityMode.Auto
            && !currentSemantics.HasDurableMutationSemanticsAuthority)
        {
            return mutationCapability with
            {
                State = RootFolderStorageState.Limited,
                Reason = RootFolderStorageReason.MutationSemanticsUnproven,
                Message =
                    "Listenarr can read and scan this storage, but automatic case-sensitivity detection is not stable enough to authorize filesystem mutations. Select Sensitive or Insensitive explicitly to enable moves, deletes, and other writes.",
                CanConfirmCurrentFolder = false,
                CanMutateFilesystem = false,
                ConfirmationToken = null,
                Detail =
                    "Automatic case sensitivity was inferred from an existing directory entry rather than an authoritative filesystem capability."
            };
        }

        return mutationCapability;
    }

    private static RootFolderStorageObservation ApplyMutationCapability(
        string canonicalPath,
        RootFolderStorageObservation observation,
        bool? isReadOnly)
    {
        if (isReadOnly == false)
        {
            return observation;
        }

        return observation with
        {
            State = RootFolderStorageState.Limited,
            Reason = isReadOnly == true
                ? RootFolderStorageReason.ReadOnlyFilesystem
                : RootFolderStorageReason.MutationCapabilityUnavailable,
            Message = isReadOnly == true
                ? "This storage is mounted read-only. Listenarr can read and scan it, but filesystem mutations are disabled."
                : "Listenarr can read and scan this storage, but it cannot verify that filesystem mutations are available safely.",
            CanConfirmCurrentFolder = false,
            CanMutateFilesystem = false,
            ConfirmationToken = null,
            Detail = isReadOnly == true
                ? "The filesystem reports the ST_RDONLY mount flag."
                : $"The mount access mode could not be determined for {LogRedaction.SanitizeFilePath(canonicalPath)}."
        };
    }

    private static bool? ProbeReadOnlyFileSystem(string canonicalPath)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            using var boundary = PinnedDirectoryCreation.OpenPinnedBoundary(canonicalPath);
            if (!boundary.VisiblePathMatches())
            {
                return null;
            }

            return boundary.IsLinuxFileSystemReadOnly();
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception
                or InvalidOperationException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static RootFolderStorageObservation SemanticsUnavailable(
        RootFolderStorageObservation observation,
        RootFolderStorageReason reason,
        string? detail)
    {
        if (observation.State == RootFolderStorageState.Changed)
        {
            return observation with
            {
                Reason = reason,
                Message = reason == RootFolderStorageReason.FilesystemSemanticsChanged
                    ? "The folder at this location changed and now uses different case-sensitivity rules. Review the root folder settings before using it for filesystem operations."
                    : "The folder at this location changed and Listenarr cannot verify its path rules safely. Review the root folder settings.",
                CanConfirmCurrentFolder = false,
                CanMutateFilesystem = false,
                ConfirmationToken = null,
                Detail = detail
            };
        }

        return Unavailable(reason, detail);
    }

    internal static string CreateConfirmationToken(
        RootFolder root,
        string canonicalPath,
        DirectoryObjectIdentityResolution observedIdentity)
    {
        if (!observedIdentity.IsAvailable)
        {
            throw new InvalidOperationException(
                "A confirmation token requires an available observed directory identity.");
        }

        var material = FormattableString.Invariant(
            $"{ConfirmationTokenVersion}|{root.Id}|{canonicalPath}|{root.DirectoryObjectIdentityVersion?.ToString() ?? "-"}|{root.DirectoryObjectIdentity ?? "-"}|{observedIdentity.Version}|{observedIdentity.Value}");
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant();
    }

    private static RootFolderStorageObservation LimitedIdentityUnsupported(
        string? detail) =>
        new(
            RootFolderStorageState.Limited,
            RootFolderStorageReason.IdentityUnsupported,
            "This storage can be read and scanned, but it does not expose the durable file identity required for crash-safe moves and deletions.",
            CanConfirmCurrentFolder: false,
            CanChangePath: true,
            CanMutateFilesystem: false,
            ConfirmationToken: null,
            Detail: detail);

    private static RootFolderStorageObservation UnsupportedPersistedIdentity(
        RootFolder root,
        string canonicalPath,
        DirectoryObjectIdentityResolution currentGeneration,
        string? detail) =>
        new(
            RootFolderStorageState.Unconfirmed,
            RootFolderStorageReason.IdentityUnsupported,
            "This folder's saved physical identity version is no longer supported. Review and confirm the current folder before scanning or changing files.",
            CanConfirmCurrentFolder: true,
            CanChangePath: true,
            CanMutateFilesystem: false,
            ConfirmationToken: CreateConfirmationToken(
                root,
                canonicalPath,
                currentGeneration),
            Detail: detail);

    private static RootFolderStorageObservation LimitedLegacyIdentity(
        RootFolder root,
        string canonicalPath,
        DirectoryObjectIdentityResolution currentGeneration,
        string? detail) =>
        new(
            RootFolderStorageState.Limited,
            RootFolderStorageReason.IdentityUnsupported,
            "This folder uses a legacy Linux identity that is no longer strong enough for safe moves and deletions. Listenarr can still read and scan it; review and confirm the current folder to upgrade its identity.",
            CanConfirmCurrentFolder: true,
            CanChangePath: true,
            CanMutateFilesystem: false,
            ConfirmationToken: CreateConfirmationToken(
                root,
                canonicalPath,
                currentGeneration),
            Detail: detail);

    private static RootFolderStorageObservation FromFailure(
        DirectoryObjectIdentityResolution resolution)
    {
        return resolution.FailureKind switch
        {
            DirectoryObjectIdentityFailureKind.Missing => new RootFolderStorageObservation(
                RootFolderStorageState.Missing,
                RootFolderStorageReason.PathMissing,
                "This folder is not currently available.",
                CanConfirmCurrentFolder: false,
                CanChangePath: true,
                CanMutateFilesystem: false,
                ConfirmationToken: null),
            DirectoryObjectIdentityFailureKind.ForeignPathSyntax =>
                Unavailable(RootFolderStorageReason.ForeignPathSyntax, resolution.UnavailableReason),
            DirectoryObjectIdentityFailureKind.AccessDenied =>
                Unavailable(RootFolderStorageReason.AccessDenied, resolution.UnavailableReason),
            DirectoryObjectIdentityFailureKind.IdentityUnsupported =>
                Unavailable(RootFolderStorageReason.IdentityUnsupported, resolution.UnavailableReason),
            DirectoryObjectIdentityFailureKind.LegacyWeakIdentity =>
                Unavailable(RootFolderStorageReason.IdentityUnsupported, resolution.UnavailableReason),
            DirectoryObjectIdentityFailureKind.IdentityUnstable =>
                Unavailable(RootFolderStorageReason.IdentityUnstable, resolution.UnavailableReason),
            DirectoryObjectIdentityFailureKind.InvalidPath =>
                Unavailable(RootFolderStorageReason.InvalidPath, resolution.UnavailableReason),
            _ => Unavailable(RootFolderStorageReason.Unknown, resolution.UnavailableReason)
        };
    }

    private static RootFolderStorageObservation Unavailable(
        RootFolderStorageReason reason,
        string? detail) =>
        new(
            RootFolderStorageState.Unavailable,
            reason,
            reason switch
            {
                RootFolderStorageReason.ForeignPathSyntax =>
                    "This configured path belongs to a different operating system and cannot be used on this host.",
                RootFolderStorageReason.AccessDenied =>
                    "Listenarr cannot access this folder. Check the storage permissions and mount settings.",
                RootFolderStorageReason.IdentityUnsupported =>
                    "This storage location does not expose the directory identity Listenarr requires for safe filesystem operations.",
                RootFolderStorageReason.IdentityUnstable =>
                    "This folder changed while Listenarr was checking it. Refresh the storage state and try again.",
                RootFolderStorageReason.FilesystemSemanticsUnavailable =>
                    "Listenarr cannot determine this storage location's path rules safely. Review the root folder case-sensitivity setting.",
                RootFolderStorageReason.FilesystemSemanticsChanged =>
                    "This storage location now uses different case-sensitivity rules. Review the root folder settings before using it for filesystem operations.",
                RootFolderStorageReason.InvalidPath =>
                    "The configured storage path is invalid on this host.",
                _ => "Listenarr cannot verify this storage location."
            },
            CanConfirmCurrentFolder: false,
            CanChangePath: true,
            CanMutateFilesystem: false,
            ConfirmationToken: null,
            Detail: detail);
}
