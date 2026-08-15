using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
    private const uint FileOpen = 1;
    private const uint DuplicateSameAccess = 0x00000002;

    internal static PinnedDirectoryAnchor OpenPinnedBoundary(string boundaryPath)
    {
        RequireFullyQualifiedPath(boundaryPath);
        ExclusiveDirectoryCreator.InvokeBeforeOpenParentHook(boundaryPath);
        var handle = OperatingSystem.IsWindows()
            ? OpenDirectoryWindows(boundaryPath, openReparsePoint: false)
            : OpenDirectoryUnix(boundaryPath, noFollow: false);
        var anchor = new PinnedDirectoryAnchor(
            handle,
            boundaryPath,
            followVisibleFinalLink: true);
        if (anchor.VisiblePathMatches())
        {
            return anchor;
        }

        anchor.Dispose();
        throw new InvalidOperationException(
            "The managed directory boundary changed while it was being pinned.");
    }

    internal static PinnedDirectoryAnchor OpenPinnedHierarchyNoFollow(
        string path,
        bool createMissing)
    {
        RequireFullyQualifiedPath(path);
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException(
                "A pinned filesystem hierarchy requires an absolute root.");
        }

        var current = OpenPinnedDirectoryNoFollow(root);
        try
        {
            var relative = Path.GetRelativePath(root, fullPath);
            if (relative == ".")
            {
                return current;
            }

            foreach (var segment in relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries))
            {
                PinnedDirectoryAnchor next;
                try
                {
                    next = current.OpenExistingChild(segment);
                }
                catch (Win32Exception exception) when (
                    createMissing && exception.NativeErrorCode is 2 or 3)
                {
                    using var creation = current.TryCreateChild(segment);
                    next = creation.Created
                        ? creation.OpenCreatedDirectoryAnchor()
                        : current.OpenExistingChild(segment);
                }

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

    private static void RequireFullyQualifiedPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException(
                "Pinned filesystem operations require a fully qualified native path.",
                nameof(path));
        }
    }

    internal PinnedDirectoryAnchor OpenCreatedDirectoryAnchor()
    {
        ThrowIfDisposed();
        if (!Created || _directoryHandle == null || _directoryHandle.IsInvalid)
        {
            throw new InvalidOperationException(
                "A created pinned directory is required to continue a hierarchy walk.");
        }

        return new PinnedDirectoryAnchor(
            DuplicateSafeHandle(_directoryHandle),
            FullPath,
            followVisibleFinalLink: false);
    }

    internal sealed partial class PinnedDirectoryAnchor : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly bool _followVisibleFinalLink;
        private bool _disposed;

        internal PinnedDirectoryAnchor(
            SafeFileHandle handle,
            string fullPath,
            bool followVisibleFinalLink)
        {
            _handle = handle;
            FullPath = fullPath;
            _followVisibleFinalLink = followVisibleFinalLink;
        }

        internal string FullPath { get; }

        internal bool FollowsVisibleFinalLink =>
            _followVisibleFinalLink;

        internal string GetDirectoryObjectIdentity()
        {
            ThrowIfDisposed();
            return PinnedDirectoryCreation.GetDirectoryObjectIdentity(_handle);
        }

        internal SafeFileHandle DuplicateHandleForOperation()
        {
            ThrowIfDisposed();
            return DuplicateSafeHandle(_handle);
        }

        internal void FlushDirectoryEntry()
        {
            ThrowIfDisposed();
            FlushDirectoryPathToDisk(
                _handle,
                FullPath,
                _followVisibleFinalLink);
        }

        internal bool IsOnSameVolume(PinnedDirectoryAnchor directory)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(directory);
            using var other = directory.DuplicateHandleForOperation();
            return HandlesAreOnSameVolume(_handle, other);
        }

        internal bool HasUnsupportedCrossVolumeMetadata()
        {
            ThrowIfDisposed();
            return PinnedDirectoryCreation.HasUnsupportedCrossVolumeMetadata(
                _handle,
                requireSingleLink: false);
        }

        internal PinnedDirectoryAnchor Duplicate()
        {
            ThrowIfDisposed();
            return new PinnedDirectoryAnchor(
                DuplicateSafeHandle(_handle),
                FullPath,
                _followVisibleFinalLink);
        }

        internal bool VisiblePathMatches() =>
            VisiblePathMatches(FullPath, _followVisibleFinalLink);

        internal bool VisiblePathMatches(
            string visiblePath,
            bool followVisibleFinalLink = false)
        {
            ThrowIfDisposed();
            ArgumentException.ThrowIfNullOrWhiteSpace(visiblePath);
            try
            {
                using var visible = OperatingSystem.IsWindows()
                    ? OpenDirectoryWindows(
                        visiblePath,
                        openReparsePoint: !followVisibleFinalLink)
                    : OpenDirectoryUnix(
                        visiblePath,
                        noFollow: !followVisibleFinalLink);
                return HandlesIdentifySameDirectory(_handle, visible);
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException or Win32Exception
                    or PlatformNotSupportedException)
            {
                return false;
            }
        }

        internal PinnedDirectoryAnchor OpenExistingChild(string childName)
        {
            ThrowIfDisposed();
            ValidateLeafName(childName);
            var childPath = Path.Join(FullPath, childName);
            ExclusiveDirectoryCreator.InvokeBeforeOpenParentHook(childPath);
            EnsureVisiblePathMatches();

            var childHandle = OperatingSystem.IsWindows()
                ? OpenRelativeDirectoryWindows(_handle, childName, childPath)
                : OpenDirectoryAtUnix(_handle, childName);
            var child = new PinnedDirectoryAnchor(
                childHandle,
                childPath,
                followVisibleFinalLink: false);
            if (child.VisiblePathMatches())
            {
                return child;
            }

            child.Dispose();
            throw new InvalidOperationException(
                "The visible directory hierarchy changed while opening an existing child.");
        }

        internal PinnedDirectoryCreation TryCreateChild(string childName)
            => TryCreateChild(childName, requireDirectoryDeleteAccess: false);

        internal PinnedDirectoryCreation TryCreateChildForPublication(string childName)
            => TryCreateChild(childName, requireDirectoryDeleteAccess: true);

        internal PinnedDirectoryCreation? TryOpenExistingChildForPublication(
            string childName)
        {
            try
            {
                return OpenExistingChildForPublication(childName);
            }
            catch (Win32Exception exception) when (
                exception.NativeErrorCode is 2 or 3)
            {
                return null;
            }
        }

        private PinnedDirectoryCreation TryCreateChild(
            string childName,
            bool requireDirectoryDeleteAccess)
        {
            ThrowIfDisposed();
            ValidateLeafName(childName);
            var childPath = Path.Join(FullPath, childName);
            ExclusiveDirectoryCreator.InvokeBeforeCreateHook(childPath);
            EnsureVisiblePathMatches();
            return TryCreateRelative(
                _handle,
                FullPath,
                childName,
                requireDirectoryDeleteAccess,
                _followVisibleFinalLink);
        }

        internal async Task CopyNewFileFromAsync(
            string sourcePath,
            string childName,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            ValidateLeafName(childName);
            EnsureVisiblePathMatches();

            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var fileHandle = OperatingSystem.IsWindows()
                ? CreateRelativeFileWindows(_handle, childName)
                : CreateRelativeFileUnix(_handle, childName);
            await using var destination = new FileStream(
                fileHandle,
                FileAccess.Write,
                bufferSize: 81920,
                isAsync: false);
            await source.CopyToAsync(destination, cancellationToken);
            await destination.FlushAsync(cancellationToken);
            destination.Flush(flushToDisk: true);
            EnsureVisiblePathMatches();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _handle.Dispose();
            _disposed = true;
        }

        private void EnsureVisiblePathMatches()
        {
            if (!VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "The visible directory hierarchy changed after its parent was pinned.");
            }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

}
