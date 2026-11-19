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
    private const string ConfirmationTokenVersion = "root-storage-v2";
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

        // Root availability is a current-capability decision. Persisted directory
        // identities are intentionally not compared here: mount-local device numbers,
        // inode generations, or file handles are diagnostics, not cross-session authority.
        DirectoryObjectIdentityResolution current;
        try
        {
            current = await identityResolver.ResolveAsync(canonicalPath, cancellationToken);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidOperationException
                or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            current = DirectoryObjectIdentityResolution.Unavailable(
                exception.Message, DirectoryObjectIdentityFailureKind.Unknown);
        }
        if (!current.IsAvailable
            && current.FailureKind is not (
                DirectoryObjectIdentityFailureKind.IdentityUnsupported
                or DirectoryObjectIdentityFailureKind.LegacyWeakIdentity))
        {
            // Failure to capture an optional diagnostic is distinct from failure to
            // access the root. Independently prove the currently configured path;
            // mutations still acquire their own live leases at execution time.
            var accessFailure = ProbeCurrentRootAccess(canonicalPath, cancellationToken);
            if (accessFailure != null)
            {
                return accessFailure;
            }
        }

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
                ConfirmationToken: null,
                Detail: current.IsAvailable ? null : current.UnavailableReason),
            cancellationToken);
    }

    private static RootFolderStorageObservation? ProbeCurrentRootAccess(
        string canonicalPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var current = PinnedDirectoryCreation.OpenPinnedBoundary(canonicalPath);
            return current.VisiblePathMatches()
                ? null
                : Unavailable(RootFolderStorageReason.IdentityUnstable,
                    "The configured directory changed while its current path was being checked.");
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidOperationException
                or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            if (FileSystemSafety.IsProvenMissingPathException(exception))
            {
                return FromFailure(DirectoryObjectIdentityResolution.Unavailable(
                    exception.Message, DirectoryObjectIdentityFailureKind.Missing));
            }
            var reason = exception is UnauthorizedAccessException
                    || exception is System.ComponentModel.Win32Exception native
                        && (OperatingSystem.IsWindows()
                            ? native.NativeErrorCode == 5
                            : native.NativeErrorCode is 1 or 13)
                    ? RootFolderStorageReason.AccessDenied
                    : RootFolderStorageReason.Unknown;
            return Unavailable(reason, exception.Message);
        }
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
            return observation with
            {
                State = RootFolderStorageState.Unconfirmed,
                Reason = RootFolderStorageReason.NoAuthorizedIdentity,
                Message =
                    "Listenarr has not yet confirmed the filesystem path and case-sensitivity rules for this root.",
                CanConfirmCurrentFolder = true,
                CanMutateFilesystem = false,
                CanPublishNewFiles = false,
                CanRetireSource = false,
                CanRetireAfterVerifiedCopy = false,
                ConfirmationToken = CreateConfirmationToken(
                    root,
                    canonicalPath,
                    currentSemantics.Semantics),
                Detail =
                    "The root folder has no persisted filesystem case-sensitivity authority."
            };
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
            var limitedReadOnly = _readOnlyFileSystemProbe(canonicalPath);
            return ApplyMutationCapability(
                canonicalPath,
                observation,
                limitedReadOnly);
        }

        var mutationCapability = ApplyMutationCapability(
            canonicalPath,
            observation,
            _readOnlyFileSystemProbe(canonicalPath));
        if (!mutationCapability.CanMutateFilesystem)
        {
            return mutationCapability;
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
            var supportsVerifiedCompatibilityCleanup =
                observation.CanMutateFilesystem
                || (observation.State == RootFolderStorageState.Limited
                    && observation.Reason == RootFolderStorageReason.IdentityUnsupported
                    && !observation.CanConfirmCurrentFolder);
            return observation with
            {
                CanPublishNewFiles = supportsVerifiedCompatibilityCleanup,
                CanRetireSource = observation.CanMutateFilesystem,
                CanRetireAfterVerifiedCopy = supportsVerifiedCompatibilityCleanup
            };
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
            CanPublishNewFiles = false,
            CanRetireSource = false,
            CanRetireAfterVerifiedCopy = false,
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
                    ? "The filesystem case-sensitivity rules at this path changed. Review the root folder settings before using it for filesystem operations."
                    : "Listenarr cannot verify the filesystem path rules at this location safely. Review the root folder settings.",
                CanConfirmCurrentFolder = false,
                CanMutateFilesystem = false,
                CanPublishNewFiles = false,
                CanRetireSource = false,
                CanRetireAfterVerifiedCopy = false,
                ConfirmationToken = null,
                Detail = detail
            };
        }

        return Unavailable(reason, detail);
    }

    internal static string CreateConfirmationToken(
        RootFolder root,
        string canonicalPath,
        FileSystemPathSemantics observedSemantics)
    {
        var material = FormattableString.Invariant(
            $"{ConfirmationTokenVersion}|{root.Id}|{canonicalPath}|{root.StorageContractRevision}|{root.CaseSensitivityMode}|{observedSemantics.Syntax}|{observedSemantics.CaseSensitivity}");
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant();
    }

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
                    "Listenarr could not establish the current storage capabilities needed for this operation.",
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
