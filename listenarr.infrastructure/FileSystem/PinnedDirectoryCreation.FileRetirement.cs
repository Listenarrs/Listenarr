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

            using var parent = new PinnedDirectoryAnchor(
                DuplicateSafeHandle(_parentHandle),
                _parentPath,
                _parentFollowsVisibleFinalLink);
            if (!parent.VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "The pinned file parent changed before deletion.");
            }

            PinnedFilesystemMutationHooks.InvokeBeforeUnixFileDeleteRevalidation(
                FullPath);
            using var visible = OpenRelativeFileUnix(
                _parentHandle,
                _fileName,
                FullPath);
            if (!HandlesIdentifySameDirectory(_fileHandle, visible)
                || !parent.VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "The pinned file changed before handle-relative deletion.");
            }

            if (UnlinkAt(
                    _parentHandle.DangerousGetHandle().ToInt32(),
                    _fileName,
                    flags: 0) != 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not remove the verified pinned file entry.");
            }
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

    // Attempts an immediate POSIX unlink of an empty directory handle. Returns false when the
    // underlying filesystem does not support FileDispositionInfoEx POSIX semantics — SMB reports
    // ERROR_INVALID_FUNCTION (1), ERROR_NOT_SUPPORTED (50) or ERROR_INVALID_PARAMETER (87) — so
    // the caller can degrade to delete-on-close. Any other failure is a genuine error and throws.
    private static bool TryDeleteOpenedDirectoryImmediatelyWindows(SafeFileHandle directoryHandle)
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
                    directoryHandle,
                    FileInformationClass.FileDispositionInfoEx,
                    deleteInformation,
                    sizeof(int)))
            {
                return true;
            }

            var error = Marshal.GetLastWin32Error();
            if (error is 1 or 50 or 87)
            {
                return false;
            }

            throw new Win32Exception(
                error,
                "Could not immediately retire the verified pinned directory handle.");
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
