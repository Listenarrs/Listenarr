using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
    internal static PinnedDirectoryAnchor OpenPinnedDirectoryNoFollow(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        ExclusiveDirectoryCreator.InvokeBeforeOpenParentHook(fullPath);
        var handle = OperatingSystem.IsWindows()
            ? OpenDirectoryWindows(fullPath, openReparsePoint: true)
            : OpenDirectoryUnix(fullPath, noFollow: true);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                EnsureWindowsParentIsNotReparsePoint(handle, fullPath);
            }

            var anchor = new PinnedDirectoryAnchor(
                handle,
                fullPath,
                followVisibleFinalLink: false);
            if (anchor.VisiblePathMatches())
            {
                return anchor;
            }

            anchor.Dispose();
            throw new InvalidOperationException(
                "The directory changed while it was being pinned without following links.");
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal sealed partial class PinnedDirectoryAnchor
    {
        internal PinnedFileEntry OpenExistingFile(
            string fileName,
            bool requireDeleteAccess)
        {
            ThrowIfDisposed();
            ValidateLeafName(fileName);
            var fullPath = Path.Join(FullPath, fileName);
            ExclusiveDirectoryCreator.InvokeBeforeOpenParentHook(fullPath);
            EnsureVisiblePathMatches();
            var handle = OperatingSystem.IsWindows()
                ? OpenRelativeFileWindows(
                    _handle,
                    fileName,
                    fullPath,
                    requireDeleteAccess)
                : OpenRelativeFileUnix(_handle, fileName, fullPath);
            var entry = new PinnedFileEntry(
                DuplicateSafeHandle(_handle),
                handle,
                FullPath,
                fileName,
                _followVisibleFinalLink);
            if (entry.VisiblePathMatches())
            {
                return entry;
            }

            entry.Dispose();
            throw new InvalidOperationException(
                "The file changed while it was being opened beneath its pinned parent.");
        }

        internal PinnedFileEntry OpenExistingFileForStableRead(
            string fileName)
        {
            ThrowIfDisposed();
            ValidateLeafName(fileName);
            var fullPath = Path.Join(FullPath, fileName);
            ExclusiveDirectoryCreator.InvokeBeforeOpenParentHook(fullPath);
            EnsureVisiblePathMatches();
            var handle = OperatingSystem.IsWindows()
                ? OpenRelativeFileStableReadWindows(
                    _handle,
                    fileName,
                    fullPath)
                : OpenRelativeFileUnix(_handle, fileName, fullPath);
            var entry = new PinnedFileEntry(
                DuplicateSafeHandle(_handle),
                handle,
                FullPath,
                fileName,
                _followVisibleFinalLink);
            if (entry.VisiblePathMatches())
            {
                return entry;
            }

            entry.Dispose();
            throw new InvalidOperationException(
                "The file changed while it was being opened for stable metadata extraction.");
        }

        internal PinnedFileEntry OpenExistingFileForStableDelete(
            string fileName)
        {
            ThrowIfDisposed();
            ValidateLeafName(fileName);
            var fullPath = Path.Join(FullPath, fileName);
            ExclusiveDirectoryCreator.InvokeBeforeOpenParentHook(fullPath);
            EnsureVisiblePathMatches();
            var handle = OperatingSystem.IsWindows()
                ? OpenRelativeFileStableDeleteWindows(
                    _handle,
                    fileName,
                    fullPath)
                : OpenRelativeFileUnix(_handle, fileName, fullPath);
            var entry = new PinnedFileEntry(
                DuplicateSafeHandle(_handle),
                handle,
                FullPath,
                fileName,
                _followVisibleFinalLink);
            if (entry.VisiblePathMatches())
            {
                return entry;
            }

            entry.Dispose();
            throw new InvalidOperationException(
                "The file changed while it was being opened for stable retirement.");
        }

        internal PinnedFileEntry? TryOpenExistingFile(
            string fileName,
            bool requireDeleteAccess)
        {
            try
            {
                return OpenExistingFile(fileName, requireDeleteAccess);
            }
            catch (Win32Exception exception) when (
                exception.NativeErrorCode is 2 or 3 or 32)
            {
                return null;
            }
        }

        internal PinnedFileEntry CreateNewFile(
            string fileName,
            bool hiddenFile = false)
        {
            ThrowIfDisposed();
            ValidateLeafName(fileName);
            EnsureVisiblePathMatches();
            var handle = OperatingSystem.IsWindows()
                ? CreateRelativeFileWindows(_handle, fileName, hiddenFile)
                : CreateRelativeReadWriteFileUnix(_handle, fileName);
            var entry = new PinnedFileEntry(
                DuplicateSafeHandle(_handle),
                handle,
                FullPath,
                fileName,
                _followVisibleFinalLink);
            if (entry.VisiblePathMatches())
            {
                return entry;
            }

            entry.Dispose();
            throw new InvalidOperationException(
                "The newly created file changed beneath its pinned parent.");
        }
    }

    internal sealed partial class PinnedFileEntry : IDisposable
    {
        internal FileStream OpenReadStream(int bufferSize, bool asynchronous)
        {
            ThrowIfDisposed();
            var handle = OperatingSystem.IsWindows()
                ? OpenRelativeFileWindows(
                    _parentHandle,
                    _fileName,
                    FullPath,
                    requireDeleteAccess: false)
                : OpenRelativeFileUnix(_parentHandle, _fileName, FullPath);
            return OpenVerifiedIndependentStream(
                handle,
                FileAccess.Read,
                bufferSize,
                asynchronous);
        }

        internal FileStream OpenWriteStream(int bufferSize, bool asynchronous)
        {
            ThrowIfDisposed();
            var handle = OperatingSystem.IsWindows()
                ? OpenRelativeFileForWriteWindows(
                    _parentHandle,
                    _fileName,
                    FullPath)
                : OpenRelativeFileForWriteUnix(
                    _parentHandle,
                    _fileName,
                    FullPath);
            return OpenVerifiedIndependentStream(
                handle,
                FileAccess.Write,
                bufferSize,
                asynchronous);
        }

        internal bool VisiblePathMatches()
        {
            ThrowIfDisposed();
            try
            {
                using var visible = OperatingSystem.IsWindows()
                    ? OpenRelativeFileWindows(
                        _parentHandle,
                        _fileName,
                        FullPath,
                        requireDeleteAccess: false)
                    : OpenRelativeFileUnix(_parentHandle, _fileName, FullPath);
                return HandlesIdentifySameDirectory(_fileHandle, visible);
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException or Win32Exception
                    or PlatformNotSupportedException)
            {
                return false;
            }
        }

        internal bool IdentifiesSameEntry(PinnedFileEntry candidate)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(candidate);
            candidate.ThrowIfDisposed();
            return HandlesIdentifySameDirectory(_fileHandle, candidate._fileHandle);
        }

        internal string GetObjectIdentity()
        {
            ThrowIfDisposed();
            return GetDirectoryObjectIdentity(_fileHandle);
        }

        internal bool IsOnSameVolume(PinnedDirectoryAnchor directory)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(directory);
            using var directoryHandle = directory.DuplicateHandleForOperation();
            return HandlesAreOnSameVolume(_fileHandle, directoryHandle);
        }

        internal bool HasUnsupportedCrossVolumeMetadata()
        {
            ThrowIfDisposed();
            return PinnedDirectoryCreation.HasUnsupportedCrossVolumeMetadata(
                _fileHandle,
                requireSingleLink: true);
        }

        internal PinnedFileEntry CreateHardLinkTo(
            PinnedDirectoryAnchor destinationParent,
            string destinationName,
            Action? afterLinkCreatedForTest = null)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(destinationParent);
            ValidateLeafName(destinationName);
            if (!VisiblePathMatches() || !destinationParent.VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "A pinned hardlink endpoint changed before link creation.");
            }

            using var destinationHandle =
                destinationParent.DuplicateHandleForOperation();
            if (OperatingSystem.IsWindows())
            {
                CreateRelativeHardLinkWindows(
                    _fileHandle,
                    destinationHandle,
                    destinationName);
            }
            else if (LinkAt(
                    _parentHandle.DangerousGetHandle().ToInt32(),
                    _fileName,
                    destinationHandle.DangerousGetHandle().ToInt32(),
                    destinationName,
                    flags: 0) != 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not create a hardlink between pinned filesystem endpoints.");
            }

            afterLinkCreatedForTest?.Invoke();

            PinnedFileEntry? linked = null;
            try
            {
                linked = destinationParent.OpenExistingFile(
                    destinationName,
                    requireDeleteAccess: true);
                if (!linked.VisiblePathMatches() || !IdentifiesSameEntry(linked))
                {
                    throw new InvalidOperationException(
                        "The created hardlink does not identify the pinned source generation.");
                }

                return linked;
            }
            catch
            {
                if (linked != null
                    && linked.VisiblePathMatches()
                    && IdentifiesSameEntry(linked))
                {
                    linked.Delete(immediateWindows: true);
                }
                linked?.Dispose();
                throw;
            }
        }

        internal async Task<bool> MatchesAsync(
            long expectedLength,
            string? expectedSha256,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(expectedSha256))
            {
                return false;
            }

            await using var stream = OpenReadStream(
                bufferSize: 128 * 1024,
                asynchronous: false);
            if (stream.Length != expectedLength)
            {
                return false;
            }

            stream.Position = 0;
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            return string.Equals(
                Convert.ToHexString(hash),
                expectedSha256,
                StringComparison.Ordinal);
        }

        internal void MoveTo(
            PinnedDirectoryAnchor destinationParent,
            string destinationName)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(destinationParent);
            ValidateLeafName(destinationName);
            if (!VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "The source file changed before its pinned rename.");
            }
            if (!destinationParent.VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "The destination directory changed before its pinned rename.");
            }

            using var destinationHandle = destinationParent.DuplicateHandleForOperation();
            RenameRelativeEntry(
                _parentHandle,
                _fileHandle,
                _fileName,
                destinationHandle,
                destinationName);
            using var published = OperatingSystem.IsWindows()
                ? OpenRelativeFileWindows(
                    destinationHandle,
                    destinationName,
                    Path.Join(destinationParent.FullPath, destinationName),
                    requireDeleteAccess: false)
                : OpenRelativeFileUnix(
                    destinationHandle,
                    destinationName,
                    Path.Join(destinationParent.FullPath, destinationName));
            if (!HandlesIdentifySameDirectory(_fileHandle, published))
            {
                throw new InvalidOperationException(
                    "The published quarantine file does not identify the opened source file.");
            }

            var newParentHandle = DuplicateSafeHandle(destinationHandle);
            _parentHandle.Dispose();
            _parentHandle = newParentHandle;
            _parentPath = destinationParent.FullPath;
            _fileName = destinationName;
            _parentFollowsVisibleFinalLink =
                destinationParent.FollowsVisibleFinalLink;
        }

        internal void MoveWithinParent(string destinationName)
        {
            ThrowIfDisposed();
            ValidateLeafName(destinationName);
            if (!VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "The source file changed before its pinned publication.");
            }

            RenameRelativeEntry(
                _parentHandle,
                _fileHandle,
                _fileName,
                _parentHandle,
                destinationName);
            using var published = OperatingSystem.IsWindows()
                ? OpenRelativeFileWindows(
                    _parentHandle,
                    destinationName,
                    Path.Join(_parentPath, destinationName),
                    requireDeleteAccess: false)
                : OpenRelativeFileUnix(
                    _parentHandle,
                    destinationName,
                    Path.Join(_parentPath, destinationName));
            if (!HandlesIdentifySameDirectory(_fileHandle, published))
            {
                throw new InvalidOperationException(
                    "The published file does not identify the opened partial file.");
            }

            _fileName = destinationName;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _fileHandle.Dispose();
            _parentHandle.Dispose();
            _disposed = true;
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

    }
}
