using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
    private const uint FileOpenIf = 3;
    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private const int LinuxWouldBlock = 11;
    private const int MacWouldBlock = 35;

    internal void RestrictToCurrentUser()
    {
        ThrowIfDisposed();
        if (!Created || _directoryHandle == null || _directoryHandle.IsInvalid)
        {
            throw new InvalidOperationException(
                "A created pinned directory is required to restrict permissions.");
        }

        if (OperatingSystem.IsWindows())
        {
            if (!VisiblePathMatches())
            {
                throw new IOException(
                    "The pinned directory identity changed while permissions were restricted.");
            }
            return;
        }

        using var handle = DuplicateSafeHandle(_directoryHandle);
        RestrictDirectoryHandleToCurrentUser(handle);
        if (!VisiblePathMatches())
        {
            throw new IOException(
                "The pinned directory identity changed while permissions were restricted.");
        }
    }

    internal sealed partial class PinnedDirectoryAnchor
    {
        internal void RestrictToCurrentUser()
        {
            ThrowIfDisposed();
            if (OperatingSystem.IsWindows())
            {
                EnsureVisiblePathMatches();
                return;
            }

            using var handle = DuplicateHandleForOperation();
            RestrictDirectoryHandleToCurrentUser(handle);
            if (!VisiblePathMatches())
            {
                throw new IOException(
                    "The pinned lock directory permissions or identity changed.");
            }
        }

        internal async Task<FileStream> OpenOrCreateExclusiveLockFileAsync(
            string fileName,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            ValidateLeafName(fileName);
            for (var attempt = 0; attempt < 300; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureVisiblePathMatches();
                SafeFileHandle? handle = null;
                try
                {
                    handle = OperatingSystem.IsWindows()
                        ? TryOpenOrCreateExclusiveLockFileWindows(
                            _handle,
                            fileName)
                        : TryOpenOrCreateExclusiveLockFileUnix(
                            _handle,
                            fileName);
                    if (handle == null)
                    {
                        if (attempt == 299)
                        {
                            break;
                        }

                        await Task.Delay(100, cancellationToken);
                        continue;
                    }

                    if (!VisiblePathMatches())
                    {
                        throw new IOException(
                            "The pinned lock directory changed while a stripe lock was acquired.");
                    }

                    return new FileStream(
                        handle,
                        FileAccess.ReadWrite,
                        bufferSize: 1,
                        isAsync: false);
                }
                catch
                {
                    handle?.Dispose();
                    throw;
                }
            }

            throw new IOException(
                "Timed out acquiring a file-move stripe lock.");
        }
    }

    private static SafeFileHandle? TryOpenOrCreateExclusiveLockFileWindows(
        SafeFileHandle directoryHandle,
        string fileName)
    {
        var nameBuffer = Marshal.StringToHGlobalUni(fileName);
        var unicodeStringPointer = IntPtr.Zero;
        try
        {
            var unicodeString = new UnicodeString
            {
                Length = checked((ushort)(fileName.Length * sizeof(char))),
                MaximumLength = checked((ushort)((fileName.Length + 1) * sizeof(char))),
                Buffer = nameBuffer
            };
            unicodeStringPointer = Marshal.AllocHGlobal(
                Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(
                unicodeString,
                unicodeStringPointer,
                fDeleteOld: false);
            var attributes = new ObjectAttributes
            {
                Length = (uint)Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = directoryHandle.DangerousGetHandle(),
                ObjectName = unicodeStringPointer
            };
            var status = NtCreateFile(
                out var rawHandle,
                GenericRead | GenericWrite | Synchronize,
                ref attributes,
                out _,
                IntPtr.Zero,
                fileAttributes: 0,
                shareAccess: 0,
                FileOpenIf,
                FileNonDirectoryFile
                    | FileSynchronousIoNonAlert
                    | FileOpenReparsePoint,
                IntPtr.Zero,
                0);
            if (status >= 0)
            {
                var handle = new SafeFileHandle(
                    rawHandle,
                    ownsHandle: true);
                try
                {
                    EnsureFileHandleIsNotReparsePoint(
                        handle,
                        fileName);
                    return handle;
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }

            var error = unchecked((int)RtlNtStatusToDosError(status));
            if (error is 32 or 33)
            {
                return null;
            }

            throw new Win32Exception(
                error,
                "Could not open a pinned file-move stripe lock.");
        }
        finally
        {
            if (unicodeStringPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(unicodeStringPointer);
            }
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static void RestrictDirectoryHandleToCurrentUser(
        SafeFileHandle handle)
    {
        var privateMode =
            System.IO.UnixFileMode.UserRead
            | System.IO.UnixFileMode.UserWrite
            | System.IO.UnixFileMode.UserExecute;
        File.SetUnixFileMode(handle, privateMode);
        if (File.GetUnixFileMode(handle) != privateMode)
        {
            throw new IOException(
                "The pinned directory permissions could not be restricted to the current user.");
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static SafeFileHandle? TryOpenOrCreateExclusiveLockFileUnix(
        SafeFileHandle directoryHandle,
        string fileName)
    {
        var flags = OperatingSystem.IsMacOS()
            ? 0x2 | 0x200 | 0x100 | 0x1000000
            : 0x2 | 0x40 | 0x20000 | 0x80000;
        var fileDescriptor = OpenAt(
            directoryHandle.DangerousGetHandle().ToInt32(),
            fileName,
            flags,
            UnixFileMode);
        if (fileDescriptor < 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not open a pinned file-move stripe lock.");
        }

        var handle = new SafeFileHandle(
            new IntPtr(fileDescriptor),
            ownsHandle: true);
        try
        {
            File.SetUnixFileMode(
                handle,
                System.IO.UnixFileMode.UserRead
                    | System.IO.UnixFileMode.UserWrite);
            if (Flock(
                    fileDescriptor,
                    LockExclusive | LockNonBlocking) == 0)
            {
                return handle;
            }

            var error = Marshal.GetLastWin32Error();
            if (error == LinuxWouldBlock
                || error == MacWouldBlock)
            {
                handle.Dispose();
                return null;
            }

            throw new Win32Exception(
                error,
                "Could not acquire a pinned file-move stripe lock.");
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int fileDescriptor, int operation);
}
