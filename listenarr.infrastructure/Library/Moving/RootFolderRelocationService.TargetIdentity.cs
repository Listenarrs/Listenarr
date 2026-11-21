using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private static void RejectTargetNavigationSegments(string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        var root = Path.GetPathRoot(targetPath);
        var relativePath = string.IsNullOrEmpty(root)
            ? targetPath
            : targetPath[root.Length..];
        var segments = relativePath.Split(
            ['/', '\\'],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == "."))
        {
            throw new ArgumentException(
                "Root folder target path cannot contain current directory segments.",
                nameof(targetPath));
        }

        if (segments.Any(segment => segment == ".."))
        {
            throw new ArgumentException(
                "Root folder target path cannot contain parent traversal segments.",
                nameof(targetPath));
        }
    }

    private static PinnedDirectoryCreation.PinnedDirectoryAnchor
        PinTargetDirectoryGeneration(
            string targetPath,
            int? expectedVersion,
            string? expectedValue,
            string? unavailableReason,
            CancellationToken cancellationToken)
    {
        if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                targetPath,
                out var canonicalTargetPath,
                out var pathReason))
        {
            throw new InvalidOperationException(pathReason);
        }

        PinnedDirectoryCreation.PinnedDirectoryAnchor? target = null;
        try
        {
            target = PinnedDirectoryCreation.OpenPinnedBoundary(
                canonicalTargetPath);
            RevalidatePinnedTargetDirectoryGeneration(
                target,
                expectedVersion,
                expectedValue,
                unavailableReason,
                cancellationToken);
            return target;
        }
        catch
        {
            target?.Dispose();
            throw;
        }
    }

    private static void RevalidatePinnedTargetDirectoryGeneration(
        PinnedDirectoryCreation.PinnedDirectoryAnchor target,
        int? expectedVersion,
        string? expectedValue,
        string? unavailableReason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Persisted directory identity is diagnostic only. Relocation finalization
        // is authorized by the directory that is pinned and visible now.
        var visibility = target.ProbeVisiblePathMatch();
        if (visibility == RegistrationPublicationMatchOutcome.Unavailable)
        {
            throw new IOException(
                "The managed directory is temporarily unavailable while its current path is being verified.");
        }
        if (visibility != RegistrationPublicationMatchOutcome.Match)
        {
            throw new InvalidOperationException(
                "The managed directory changed while its current path was being verified.");
        }
    }

    private static void RevalidatePinnedTargetDirectoryGeneration(
        PinnedDirectoryCreation.PinnedDirectoryAnchor target,
        RootFolderRelocation relocation,
        CancellationToken cancellationToken) =>
        RevalidatePinnedTargetDirectoryGeneration(
            target,
            relocation.TargetDirectoryObjectIdentityVersion,
            relocation.TargetDirectoryObjectIdentity,
            relocation.TargetDirectoryObjectIdentityUnavailableReason,
            cancellationToken);

    private static Task RequireTargetDirectoryGenerationAsync(
        string targetPath,
        int? expectedVersion,
        string? expectedValue,
        string? unavailableReason,
        CancellationToken cancellationToken)
    {
        using var target = PinTargetDirectoryGeneration(
            targetPath,
            expectedVersion,
            expectedValue,
            unavailableReason,
            cancellationToken);
        return Task.CompletedTask;
    }

    private static Task RequireTargetDirectoryGenerationAsync(
        string targetPath,
        DirectoryObjectIdentityResolution expectedIdentity,
        CancellationToken cancellationToken) =>
        RequireTargetDirectoryGenerationAsync(
            targetPath,
            expectedIdentity.Version,
            expectedIdentity.Value,
            expectedIdentity.UnavailableReason,
            cancellationToken);

    private static void ApplyRootDirectoryObjectIdentity(
        RootFolder root,
        DirectoryObjectIdentityResolution identity)
    {
        root.DirectoryObjectIdentityVersion = identity.Version;
        root.DirectoryObjectIdentity = identity.Value;
        root.DirectoryObjectIdentityUnavailableReason = identity.UnavailableReason;
    }

    private static TargetIdentityEnrollmentState
        GetTargetIdentityEnrollmentState(
            DirectoryObjectIdentityResolution identity) =>
        identity.IsAvailable
            ? TargetIdentityEnrollmentState.Authorized
            : identity.FailureKind is
                DirectoryObjectIdentityFailureKind.IdentityUnsupported
                    or DirectoryObjectIdentityFailureKind.LegacyWeakIdentity
                ? TargetIdentityEnrollmentState.NotRequired
                : TargetIdentityEnrollmentState.Unavailable;

    private async Task<DirectoryObjectIdentityResolution>
        ResolveOrEnrollDirectoryObjectIdentityAsync(
            string path,
            CancellationToken cancellationToken)
    {
        DirectoryObjectIdentityResolution diagnostic;
        try
        {
            diagnostic = _directoryObjectIdentityResolver != null
                ? await _directoryObjectIdentityResolver.ResolveAsync(path, cancellationToken)
                : await ResolveMarkerlessDirectoryObjectIdentityAsync(path, cancellationToken);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or InvalidOperationException or NotSupportedException
                or System.ComponentModel.Win32Exception)
        {
            diagnostic = DirectoryObjectIdentityResolution.Unavailable(
                exception.Message,
                DirectoryObjectIdentityFailureKind.IdentityUnsupported);
        }

        try
        {
            // Optional diagnostic capture cannot determine target accessibility.
            // Observe the current path independently; later publication/finalization
            // acquires and retains its own live pin across the actual mutation.
            using var current = PinTargetDirectoryGeneration(
                path, null, null, null, cancellationToken);
            return diagnostic.IsAvailable
                ? diagnostic
                : DirectoryObjectIdentityResolution.Unavailable(
                    diagnostic.UnavailableReason ?? "Directory identity diagnostics are unavailable.",
                    diagnostic.FailureKind == DirectoryObjectIdentityFailureKind.LegacyWeakIdentity
                        ? DirectoryObjectIdentityFailureKind.LegacyWeakIdentity
                        : DirectoryObjectIdentityFailureKind.IdentityUnsupported);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or InvalidOperationException or NotSupportedException
                or System.ComponentModel.Win32Exception)
        {
            return DirectoryObjectIdentityResolution.Unavailable(
                exception.Message,
                ClassifyMarkerlessDirectoryIdentityFailure(exception));
        }
    }

    private static Task<DirectoryObjectIdentityResolution>
        ResolveMarkerlessDirectoryObjectIdentityAsync(
            string path,
            CancellationToken cancellationToken)
    {
        try
        {
            using var anchor = PinnedDirectoryCreation.OpenPinnedBoundary(path);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateMarkerlessIdentity(anchor));
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or InvalidOperationException or NotSupportedException
                or System.ComponentModel.Win32Exception)
        {
            return Task.FromResult(
                DirectoryObjectIdentityResolution.Unavailable(
                    exception.Message,
                    ClassifyMarkerlessDirectoryIdentityFailure(exception)));
        }
    }

    private static DirectoryObjectIdentityFailureKind
        ClassifyMarkerlessDirectoryIdentityFailure(Exception exception) =>
        exception switch
        {
            DirectoryNotFoundException or FileNotFoundException =>
                DirectoryObjectIdentityFailureKind.Missing,
            UnauthorizedAccessException =>
                DirectoryObjectIdentityFailureKind.AccessDenied,
            System.ComponentModel.Win32Exception native when OperatingSystem.IsWindows()
                && native.NativeErrorCode is 2 or 3 =>
                DirectoryObjectIdentityFailureKind.Missing,
            System.ComponentModel.Win32Exception native when !OperatingSystem.IsWindows()
                && native.NativeErrorCode == 2 =>
                DirectoryObjectIdentityFailureKind.Missing,
            System.ComponentModel.Win32Exception native when OperatingSystem.IsWindows()
                && native.NativeErrorCode == 5 =>
                DirectoryObjectIdentityFailureKind.AccessDenied,
            System.ComponentModel.Win32Exception native when !OperatingSystem.IsWindows()
                && native.NativeErrorCode is 1 or 13 =>
                DirectoryObjectIdentityFailureKind.AccessDenied,
            PlatformNotSupportedException =>
                DirectoryObjectIdentityFailureKind.IdentityUnsupported,
            InvalidOperationException =>
                DirectoryObjectIdentityFailureKind.IdentityUnstable,
            _ => DirectoryObjectIdentityFailureKind.Unknown
        };

    private static DirectoryObjectIdentityResolution CreateMarkerlessIdentity(
        PinnedDirectoryCreation.PinnedDirectoryAnchor anchor)
    {
        var nativeIdentity = anchor.GetDirectoryObjectIdentity();
        return new DirectoryObjectIdentityResolution(
            ManagedDirectoryIdentity.CurrentVersion,
            ManagedDirectoryIdentity.CreateMarkerless(nativeIdentity),
            null);
    }
}
