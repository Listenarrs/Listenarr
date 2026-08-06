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

    private static Task RequireTargetDirectoryGenerationAsync(
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

            return Task.CompletedTask;
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

    private Task<DirectoryObjectIdentityResolution>
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

            return Task.FromResult(CreateMarkerlessIdentity(anchor));
        }

        try
        {
            using var existing = PinnedDirectoryCreation.OpenPinnedBoundary(
                targetPath);
            return Task.FromResult(CreateMarkerlessIdentity(existing));
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
