using System.ComponentModel;

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
            var visibility = anchor.ProbeVisiblePathMatch();
            if (visibility == RegistrationPublicationMatchOutcome.Match)
            {
                return anchor;
            }

            anchor.Dispose();
            if (visibility == RegistrationPublicationMatchOutcome.Unavailable)
            {
                throw new IOException(
                    "The directory is temporarily unavailable while it is being pinned without following links.");
            }
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
            var visibility = entry.ProbeVisiblePathMatch();
            if (visibility == RegistrationPublicationMatchOutcome.Match)
            {
                return entry;
            }

            entry.Dispose();
            if (visibility == RegistrationPublicationMatchOutcome.Unavailable)
            {
                throw new IOException(
                    "The file is temporarily unavailable while it is being opened beneath its pinned parent.");
            }
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
            var visibility = entry.ProbeVisiblePathMatch();
            if (visibility == RegistrationPublicationMatchOutcome.Match)
            {
                return entry;
            }

            entry.Dispose();
            if (visibility == RegistrationPublicationMatchOutcome.Unavailable)
            {
                throw new IOException(
                    "The file is temporarily unavailable while it is being opened for stable metadata extraction.");
            }
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
            var visibility = entry.ProbeVisiblePathMatch();
            if (visibility == RegistrationPublicationMatchOutcome.Match)
            {
                return entry;
            }

            entry.Dispose();
            if (visibility == RegistrationPublicationMatchOutcome.Unavailable)
            {
                throw new IOException(
                    "The file is temporarily unavailable while it is being opened for stable retirement.");
            }
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
                exception.NativeErrorCode is 2 or 3)
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
            var visibility = entry.ProbeVisiblePathMatch();
            if (visibility == RegistrationPublicationMatchOutcome.Match)
            {
                return entry;
            }

            entry.Dispose();
            if (visibility == RegistrationPublicationMatchOutcome.Unavailable)
            {
                throw new IOException(
                    "The newly created file is temporarily unavailable beneath its pinned parent.");
            }
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

        internal bool VisiblePathMatches() =>
            ProbeVisiblePathMatch() == RegistrationPublicationMatchOutcome.Match;

        internal RegistrationPublicationMatchOutcome ProbeVisiblePathMatch()
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
                return HandlesIdentifySameDirectory(_fileHandle, visible)
                    ? RegistrationPublicationMatchOutcome.Match
                    : RegistrationPublicationMatchOutcome.Mismatch;
            }
            catch (FileNotFoundException)
            {
                return RegistrationPublicationMatchOutcome.Mismatch;
            }
            catch (DirectoryNotFoundException)
            {
                return RegistrationPublicationMatchOutcome.Mismatch;
            }
            catch (Win32Exception exception) when (
                OperatingSystem.IsWindows()
                    ? exception.NativeErrorCode is 2 or 3
                    : exception.NativeErrorCode == 2)
            {
                return RegistrationPublicationMatchOutcome.Mismatch;
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException or Win32Exception
                    or PlatformNotSupportedException)
            {
                return RegistrationPublicationMatchOutcome.Unavailable;
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

        internal IReadOnlyList<string> GetObjectIdentityCandidates()
        {
            ThrowIfDisposed();
            return OperatingSystem.IsLinux()
                ? GetLinuxObjectIdentityCandidates(_fileHandle)
                : [GetDirectoryObjectIdentity(_fileHandle)];
        }

        internal bool MatchesObjectIdentity(string expectedIdentity)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(expectedIdentity);
            var candidates = GetObjectIdentityCandidates();
            return candidates.Contains(expectedIdentity, StringComparer.Ordinal)
                || (OperatingSystem.IsLinux()
                    && candidates.Any(candidate =>
                        PinnedDirectoryCreation.ArePersistedObjectIdentitiesDurablyEquivalent(
                            expectedIdentity,
                            candidate)));
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
