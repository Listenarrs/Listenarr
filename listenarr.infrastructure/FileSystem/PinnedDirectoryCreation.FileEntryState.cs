using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace Listenarr.Infrastructure.FileSystem;

internal enum PinnedFileOpenOutcome
{
    Opened,
    NotFound,
    Unavailable
}

internal sealed partial class PinnedDirectoryCreation
{
    internal sealed partial class PinnedFileEntry
    {
        private SafeFileHandle _parentHandle;
        private SafeFileHandle _fileHandle;
        private string _parentPath;
        private string _fileName;
        private bool _parentFollowsVisibleFinalLink;
        private bool _disposed;

        internal PinnedFileEntry(
            SafeFileHandle parentHandle,
            SafeFileHandle fileHandle,
            string parentPath,
            string fileName,
            bool parentFollowsVisibleFinalLink)
        {
            _parentHandle = parentHandle;
            _fileHandle = fileHandle;
            _parentPath = parentPath;
            _fileName = fileName;
            _parentFollowsVisibleFinalLink =
                parentFollowsVisibleFinalLink;
        }

        internal string FullPath => Path.Join(_parentPath, _fileName);

        internal string FileName => _fileName;

        internal SafeFileHandle DuplicateHandleForOperation()
        {
            ThrowIfDisposed();
            return DuplicateSafeHandle(_fileHandle);
        }

        internal PinnedFileEntry OpenStableRegistrationCopy()
        {
            ThrowIfDisposed();
            if (!VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "The file generation changed before a stable registration lease could be acquired.");
            }

            var expectedIdentity = GetObjectIdentity();
            if (OperatingSystem.IsWindows())
            {
                _fileHandle.Dispose();
                _fileHandle = OpenRelativeFileStableReadWindows(
                    _parentHandle,
                    _fileName,
                    FullPath);
                if (!string.Equals(
                        GetObjectIdentity(),
                        expectedIdentity,
                        StringComparison.Ordinal)
                    || !VisiblePathMatches())
                {
                    throw new InvalidOperationException(
                        "The published file changed while its stable registration handle was acquired.");
                }
            }

            SafeFileHandle? fileHandle = null;
            SafeFileHandle? parentHandle = null;
            PinnedFileEntry? copy = null;
            try
            {
                fileHandle = DuplicateSafeHandle(_fileHandle);
                parentHandle = DuplicateSafeHandle(_parentHandle);
                copy = new PinnedFileEntry(
                    parentHandle,
                    fileHandle,
                    _parentPath,
                    _fileName,
                    _parentFollowsVisibleFinalLink);
                parentHandle = null;
                fileHandle = null;

                if (!copy.VisiblePathMatches()
                    || !IdentifiesSameEntry(copy)
                    || !string.Equals(
                        copy.GetObjectIdentity(),
                        expectedIdentity,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The stable registration lease did not capture the published file generation.");
                }

                return copy;
            }
            catch
            {
                copy?.Dispose();
                parentHandle?.Dispose();
                fileHandle?.Dispose();
                throw;
            }
        }
    }

    internal sealed partial class PinnedDirectoryAnchor
    {
        internal PinnedFileOpenOutcome TryOpenExistingFileWithOutcome(
            string fileName,
            bool requireDeleteAccess,
            out PinnedFileEntry? entry) =>
            TryOpenExistingFileWithOutcome(
                fileName,
                stableDelete: false,
                requireDeleteAccess,
                out entry);

        internal PinnedFileOpenOutcome TryOpenExistingFileForStableDeleteWithOutcome(
            string fileName,
            out PinnedFileEntry? entry) =>
            TryOpenExistingFileWithOutcome(
                fileName,
                stableDelete: true,
                requireDeleteAccess: true,
                out entry);

        private PinnedFileOpenOutcome TryOpenExistingFileWithOutcome(
            string fileName,
            bool stableDelete,
            bool requireDeleteAccess,
            out PinnedFileEntry? entry)
        {
            entry = null;
            try
            {
                entry = stableDelete
                    ? OpenExistingFileForStableDelete(fileName)
                    : OpenExistingFile(fileName, requireDeleteAccess);
                return PinnedFileOpenOutcome.Opened;
            }
            catch (Win32Exception exception) when (
                exception.NativeErrorCode is 2 or 3)
            {
                return PinnedFileOpenOutcome.NotFound;
            }
            catch (Win32Exception exception) when (
                exception.NativeErrorCode is 5 or 13 or 32)
            {
                return PinnedFileOpenOutcome.Unavailable;
            }
            catch (UnauthorizedAccessException)
            {
                return PinnedFileOpenOutcome.Unavailable;
            }
            catch (IOException)
            {
                return PinnedFileOpenOutcome.Unavailable;
            }
        }
    }
}
