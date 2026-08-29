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
        if (!string.IsNullOrWhiteSpace(unavailableReason)
            || !target.MatchesManagedDirectoryIdentity(
                expectedVersion,
                expectedValue))
        {
            throw new InvalidOperationException(
                "The managed directory no longer identifies its authorized physical generation.");
        }

        var visibility = target.ProbeVisiblePathMatch();
        if (visibility == RegistrationPublicationMatchOutcome.Unavailable)
        {
            throw new IOException(
                "The managed directory is temporarily unavailable while its authorized physical generation is being verified.");
        }
        if (visibility != RegistrationPublicationMatchOutcome.Match)
        {
            throw new InvalidOperationException(
                "The managed directory no longer identifies its authorized physical generation.");
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

    private Task<DirectoryObjectIdentityResolution>
        ResolveOrEnrollDirectoryObjectIdentityAsync(
            string path,
            CancellationToken cancellationToken)
    {
        if (_directoryObjectIdentityResolver != null)
        {
            return _directoryObjectIdentityResolver.ResolveAsync(
                path,
                cancellationToken);
        }

        return ResolveMarkerlessDirectoryObjectIdentityAsync(
            path,
            expectedVersion: null,
            expectedValue: null,
            cancellationToken);
    }

    private Task<DirectoryObjectIdentityResolution>
        ResolveExistingDirectoryObjectIdentityAsync(
            string path,
            int expectedVersion,
            string expectedValue,
            CancellationToken cancellationToken)
    {
        if (_directoryObjectIdentityResolver != null)
        {
            return _directoryObjectIdentityResolver.ResolveExistingAsync(
                path,
                expectedVersion,
                expectedValue,
                cancellationToken);
        }

        return ResolveMarkerlessDirectoryObjectIdentityAsync(
            path,
            expectedVersion,
            expectedValue,
            cancellationToken);
    }

    private static Task<DirectoryObjectIdentityResolution>
        ResolveMarkerlessDirectoryObjectIdentityAsync(
            string path,
            int? expectedVersion,
            string? expectedValue,
            CancellationToken cancellationToken)
    {
        try
        {
            using var anchor = PinnedDirectoryCreation.OpenPinnedBoundary(path);
            cancellationToken.ThrowIfCancellationRequested();
            if (expectedVersion.HasValue && expectedValue != null)
            {
                return Task.FromResult(
                    anchor.MatchesManagedDirectoryIdentity(
                        expectedVersion,
                        expectedValue)
                        ? new DirectoryObjectIdentityResolution(
                            expectedVersion,
                            expectedValue,
                            null)
                        : DirectoryObjectIdentityResolution.Unavailable(
                            "The live directory no longer matches its persisted physical identity."));
            }

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
