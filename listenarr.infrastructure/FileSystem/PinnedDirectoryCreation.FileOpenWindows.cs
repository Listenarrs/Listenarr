using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
    private static SafeFileHandle OpenRelativeFileWindows(
        SafeFileHandle parentHandle,
        string fileName,
        string fullPath,
        bool requireDeleteAccess)
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
            unicodeStringPointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(unicodeString, unicodeStringPointer, fDeleteOld: false);
            var attributes = new ObjectAttributes
            {
                Length = (uint)Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parentHandle.DangerousGetHandle(),
                ObjectName = unicodeStringPointer
            };
            var desiredAccess = GenericRead | Synchronize
                | (requireDeleteAccess ? DeleteAccess : 0u);
            var status = NtCreateFile(
                out var rawHandle,
                desiredAccess,
                ref attributes,
                out _,
                IntPtr.Zero,
                fileAttributes: 0,
                FileShareAll,
                FileOpen,
                FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint,
                IntPtr.Zero,
                0);
            if (status < 0)
            {
                throw CreateNtOpenException(status, fullPath);
            }

            var handle = new SafeFileHandle(rawHandle, ownsHandle: true);
            try
            {
                EnsureFileHandleIsNotReparsePoint(handle, fullPath);
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
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

    private static SafeFileHandle OpenRelativeFileStableReadWindows(
        SafeFileHandle parentHandle,
        string fileName,
        string fullPath)
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
            unicodeStringPointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(unicodeString, unicodeStringPointer, fDeleteOld: false);
            var attributes = new ObjectAttributes
            {
                Length = (uint)Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parentHandle.DangerousGetHandle(),
                ObjectName = unicodeStringPointer
            };
            var status = NtCreateFile(
                out var rawHandle,
                GenericRead | Synchronize,
                ref attributes,
                out _,
                IntPtr.Zero,
                fileAttributes: 0,
                FileShareRead,
                FileOpen,
                FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint,
                IntPtr.Zero,
                0);
            if (status < 0)
            {
                throw CreateNtOpenException(status, fullPath);
            }

            var handle = new SafeFileHandle(rawHandle, ownsHandle: true);
            try
            {
                EnsureFileHandleIsNotReparsePoint(handle, fullPath);
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
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

    private static SafeFileHandle OpenRelativeFileVerificationLeaseWindows(
        SafeFileHandle parentHandle,
        string fileName,
        string fullPath)
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
            unicodeStringPointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(unicodeString, unicodeStringPointer, fDeleteOld: false);
            var attributes = new ObjectAttributes
            {
                Length = (uint)Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parentHandle.DangerousGetHandle(),
                ObjectName = unicodeStringPointer
            };
            var status = NtCreateFile(
                out var rawHandle,
                GenericRead | Synchronize,
                ref attributes,
                out _,
                IntPtr.Zero,
                fileAttributes: 0,
                FileShareRead | FileShareDelete,
                FileOpen,
                FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint,
                IntPtr.Zero,
                0);
            if (status < 0)
            {
                throw CreateNtOpenException(status, fullPath);
            }

            var handle = new SafeFileHandle(rawHandle, ownsHandle: true);
            try
            {
                EnsureFileHandleIsNotReparsePoint(handle, fullPath);
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
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

    private static SafeFileHandle OpenRelativeFileStableDeleteWindows(
        SafeFileHandle parentHandle,
        string fileName,
        string fullPath)
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
            unicodeStringPointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(unicodeString, unicodeStringPointer, fDeleteOld: false);
            var attributes = new ObjectAttributes
            {
                Length = (uint)Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parentHandle.DangerousGetHandle(),
                ObjectName = unicodeStringPointer
            };
            var status = NtCreateFile(
                out var rawHandle,
                GenericRead | DeleteAccess | Synchronize,
                ref attributes,
                out _,
                IntPtr.Zero,
                fileAttributes: 0,
                FileShareRead,
                FileOpen,
                FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint,
                IntPtr.Zero,
                0);
            if (status < 0)
            {
                throw CreateNtOpenException(status, fullPath);
            }

            var handle = new SafeFileHandle(rawHandle, ownsHandle: true);
            try
            {
                EnsureFileHandleIsNotReparsePoint(handle, fullPath);
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
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

    private static SafeFileHandle OpenRelativeFileForWriteWindows(
        SafeFileHandle parentHandle,
        string fileName,
        string fullPath)
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
            unicodeStringPointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(unicodeString, unicodeStringPointer, fDeleteOld: false);
            var attributes = new ObjectAttributes
            {
                Length = (uint)Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parentHandle.DangerousGetHandle(),
                ObjectName = unicodeStringPointer
            };
            var status = NtCreateFile(
                out var rawHandle,
                GenericWrite | FileReadAttributes | Synchronize,
                ref attributes,
                out _,
                IntPtr.Zero,
                fileAttributes: 0,
                FileShareAll,
                FileOpen,
                FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint,
                IntPtr.Zero,
                0);
            if (status < 0)
            {
                throw CreateNtOpenException(status, fullPath);
            }

            var handle = new SafeFileHandle(rawHandle, ownsHandle: true);
            try
            {
                EnsureFileHandleIsNotReparsePoint(handle, fullPath);
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
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
}
