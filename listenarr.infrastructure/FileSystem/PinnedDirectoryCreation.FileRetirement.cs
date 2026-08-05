using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
    internal sealed partial class PinnedFileEntry
    {
        internal void Delete(bool immediateWindows = false)
        {
            ThrowIfDisposed();
            if (!VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "The pinned file changed before deletion.");
            }

            if (OperatingSystem.IsWindows())
            {
                if (immediateWindows)
                {
                    DeleteOpenedFileImmediatelyWindows(_fileHandle);
                }
                else
                {
                    DeleteOpenedFileWindows(_fileHandle);
                }
                return;
            }

            var retirementDirectoryName = $".listenarr-retire-{Guid.NewGuid():N}.state";
            using var parent = new PinnedDirectoryAnchor(
                DuplicateSafeHandle(_parentHandle),
                _parentPath,
                _parentFollowsVisibleFinalLink);
            using var retirementDirectory = parent.TryCreateChild(retirementDirectoryName);
            if (!retirementDirectory.Created
                || !retirementDirectory.VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "Could not create an exclusive private retirement directory.");
            }

            retirementDirectory.RestrictToCurrentUser();

            {
                using var retirementAnchor = retirementDirectory.OpenCreatedDirectoryAnchor();
                const string retirementName = "entry.claim";
                MoveTo(retirementAnchor, retirementName);
                using var claimedEntry = retirementAnchor.OpenExistingFile(
                    retirementName,
                    requireDeleteAccess: false);
                claimedEntry.DeleteFromPrivateDirectoryUnix();
                if (Directory.EnumerateFileSystemEntries(retirementDirectory.FullPath).Any())
                {
                    throw new InvalidOperationException(
                        "The private retirement directory contains unexpected entries.");
                }
            }

            retirementDirectory.DeleteCreatedEmptyDirectoryUnix();
        }

        private void DeleteFromPrivateDirectoryUnix()
        {
            ThrowIfDisposed();
            if (OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException(
                    "Private-directory unlink is only used on Unix-like platforms.");
            }
            if (!VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "The private claimed file changed before retirement.");
            }
            if (UnlinkAt(
                    _parentHandle.DangerousGetHandle().ToInt32(),
                    _fileName,
                    flags: 0) != 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not remove the verified private file claim.");
            }
        }
    }

    private void DeleteCreatedEmptyDirectoryUnix()
    {
        ThrowIfDisposed();
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Private-directory retirement is only used on Unix-like platforms.");
        }
        if (!Created || _directoryHandle == null || _directoryHandle.IsInvalid)
        {
            throw new InvalidOperationException(
                "A pinned created directory is required for retirement.");
        }
        if (!VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The private retirement directory changed before deletion.");
        }

        var retirementName = $".listenarr-retired-{Guid.NewGuid():N}.state";
        RenameRelativeEntry(
            _parentHandle,
            _directoryHandle,
            _childName,
            _parentHandle,
            retirementName);
        using var reopened = OpenDirectoryAtUnix(_parentHandle, retirementName);
        if (!HandlesIdentifySameDirectory(_directoryHandle, reopened))
        {
            throw new InvalidOperationException(
                "The renamed retirement directory no longer identifies the pinned directory.");
        }

        var flags = OperatingSystem.IsMacOS() ? AtRemovedirMac : AtRemovedirLinux;
        if (UnlinkAt(
                _parentHandle.DangerousGetHandle().ToInt32(),
                retirementName,
                flags) != 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not delete the verified private retirement directory.");
        }
    }

    private static void DeleteOpenedFileWindows(SafeFileHandle fileHandle)
    {
        var deleteInformation = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(deleteInformation, 1);
            if (!SetFileInformationByHandle(
                    fileHandle,
                    FileInformationClass.FileDispositionInfo,
                    deleteInformation,
                    sizeof(int)))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not delete the verified pinned file handle.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(deleteInformation);
        }
    }

    private static void DeleteOpenedFileImmediatelyWindows(
        SafeFileHandle fileHandle,
        bool allowLegacyFallback = true)
    {
        const int fileDispositionDelete = 0x1;
        const int fileDispositionPosixSemantics = 0x2;
        const int fileDispositionIgnoreReadonlyAttribute = 0x10;
        var deleteInformation = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(
                deleteInformation,
                fileDispositionDelete
                | fileDispositionPosixSemantics
                | fileDispositionIgnoreReadonlyAttribute);
            if (SetFileInformationByHandle(
                    fileHandle,
                    FileInformationClass.FileDispositionInfoEx,
                    deleteInformation,
                    sizeof(int)))
            {
                return;
            }

            var error = Marshal.GetLastWin32Error();
            if (allowLegacyFallback && error is 1 or 50 or 87)
            {
                DeleteOpenedFileWindows(fileHandle);
                return;
            }

            throw new Win32Exception(
                error,
                "Could not immediately retire the verified pinned file handle.");
        }
        finally
        {
            Marshal.FreeHGlobal(deleteInformation);
        }
    }
}
