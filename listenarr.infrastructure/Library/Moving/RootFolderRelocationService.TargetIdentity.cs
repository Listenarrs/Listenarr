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

    private static async Task RequireTargetDirectoryGenerationAsync(
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

        try
        {
            using var target = PinnedDirectoryCreation.OpenPinnedBoundary(
                canonicalTargetPath);
            await ManagedDirectoryEnrollment.RequireMatchingEnrollmentAsync(
                target,
                expectedVersion,
                expectedValue,
                unavailableReason,
                cancellationToken);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or InvalidOperationException or NotSupportedException
                or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(
                "The relocation target no longer identifies its authorized physical directory generation.",
                exception);
        }
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

    private async Task<DirectoryObjectIdentityResolution>
        ResolveOrCreateRelocationTargetIdentityAsync(
            string targetPath,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parentPath = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException(
                "The relocation target has no parent directory.");
        var childName = Path.GetFileName(targetPath);
        using var creation = PinnedDirectoryCreation.TryCreate(
            parentPath,
            childName);
        if (creation.Created)
        {
            using var anchor = creation.OpenCreatedDirectoryAnchor();
            if (!creation.VisiblePathMatches()
                || !anchor.VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "The relocation target changed while its physical identity was reserved.");
            }

            var nativeIdentity = anchor.GetDirectoryObjectIdentity();
            return await ManagedDirectoryEnrollment.ResolveAsync(
                anchor,
                nativeIdentity,
                enrollIfMissing: true,
                cancellationToken);
        }

        try
        {
            using var existing = PinnedDirectoryCreation.OpenPinnedBoundary(
                targetPath);
            var nativeIdentity = existing.GetDirectoryObjectIdentity();
            return await ManagedDirectoryEnrollment.ResolveAsync(
                existing,
                nativeIdentity,
                enrollIfMissing: true,
                cancellationToken);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or InvalidOperationException or NotSupportedException
                or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(
                "The relocation target could not be reserved with a stable physical directory identity.",
                exception);
        }
    }

    private Task<DirectoryObjectIdentityResolution>
        ResolveOrEnrollDirectoryObjectIdentityAsync(
            string path,
            CancellationToken cancellationToken) =>
        ResolveDirectoryObjectIdentityAsync(
            path,
            enrollIfMissing: true,
            cancellationToken);

    private Task<DirectoryObjectIdentityResolution>
        ResolveExistingDirectoryObjectIdentityAsync(
            string path,
            CancellationToken cancellationToken) =>
        ResolveDirectoryObjectIdentityAsync(
            path,
            enrollIfMissing: false,
            cancellationToken);

    private async Task<DirectoryObjectIdentityResolution>
        ResolveDirectoryObjectIdentityAsync(
            string path,
            bool enrollIfMissing,
            CancellationToken cancellationToken)
    {
        if (_directoryObjectIdentityResolver != null)
        {
            return enrollIfMissing
                ? await _directoryObjectIdentityResolver.ResolveAsync(
                    path,
                    cancellationToken)
                : await _directoryObjectIdentityResolver.ResolveExistingAsync(
                    path,
                    cancellationToken);
        }

        try
        {
            using var anchor = PinnedDirectoryCreation.OpenPinnedBoundary(path);
            var nativeIdentity = anchor.GetDirectoryObjectIdentity();
            return await ManagedDirectoryEnrollment.ResolveAsync(
                anchor,
                nativeIdentity,
                enrollIfMissing,
                cancellationToken);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or InvalidOperationException or NotSupportedException
                or System.ComponentModel.Win32Exception)
        {
            return DirectoryObjectIdentityResolution.Unavailable(
                exception.Message);
        }
    }
}
