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
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or InvalidOperationException or NotSupportedException
                or System.ComponentModel.Win32Exception)
        {
            target?.Dispose();
            throw new InvalidOperationException(
                "The relocation target no longer identifies its authorized physical directory generation.",
                exception);
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
            || !ManagedDirectoryIdentity.MatchesNativeIdentity(
                expectedVersion,
                expectedValue,
                target.GetDirectoryObjectIdentity())
            || !target.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The managed directory no longer identifies its authorized physical generation.");
        }
    }

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
            var nativeIdentity = anchor.GetDirectoryObjectIdentity();
            if (expectedVersion.HasValue && expectedValue != null)
            {
                return Task.FromResult(
                    ManagedDirectoryIdentity.MatchesNativeIdentity(
                        expectedVersion,
                        expectedValue,
                        nativeIdentity)
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
                    exception.Message));
        }
    }

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
