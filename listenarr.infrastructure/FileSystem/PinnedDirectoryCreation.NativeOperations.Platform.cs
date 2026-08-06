using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
    private static LinuxFileIdentity GetLinuxIdentity(SafeFileHandle handle)
    {
        if (Statx(
                handle.DangerousGetHandle().ToInt32(),
                string.Empty,
                0x1000,
                0x00000100 | 0x00001000,
                out var information) != 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return new LinuxFileIdentity(
            information.DeviceMajor,
            information.DeviceMinor,
            information.Inode,
            information.MountId);
    }

    private static string GetMacHandlePath(SafeFileHandle handle)
    {
        var buffer = Marshal.AllocHGlobal(4096);
        try
        {
            if (FcntlGetPath(
                    handle.DangerousGetHandle().ToInt32(),
                    50,
                    buffer) != 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return Marshal.PtrToStringUTF8(buffer)
                ?? throw new InvalidOperationException(
                    "Could not resolve a pinned macOS directory path.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int CreateRelativeWindows(
        SafeFileHandle rootHandle,
        string name,
        bool directory,
        bool hiddenFile,
        bool requireDirectoryDeleteAccess,
        out IntPtr rawHandle)
    {
        var nameBuffer = Marshal.StringToHGlobalUni(name);
        var unicodeStringPointer = IntPtr.Zero;
        try
        {
            var unicodeString = new UnicodeString
            {
                Length = checked((ushort)(name.Length * sizeof(char))),
                MaximumLength = checked((ushort)((name.Length + 1) * sizeof(char))),
                Buffer = nameBuffer
            };
            unicodeStringPointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(unicodeString, unicodeStringPointer, fDeleteOld: false);
            var attributes = new ObjectAttributes
            {
                Length = (uint)Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = rootHandle.DangerousGetHandle(),
                ObjectName = unicodeStringPointer
            };
            var desiredAccess = directory
                ? FileListDirectory | FileReadAttributes | Synchronize
                    | (requireDirectoryDeleteAccess ? DeleteAccess : 0u)
                : GenericRead | GenericWrite | FileReadAttributes
                    | FileWriteAttributes | DeleteAccess | Synchronize;
            var createOptions = (directory ? FileDirectoryFile : FileNonDirectoryFile)
                | FileSynchronousIoNonAlert
                | FileOpenReparsePoint;
            return NtCreateFile(
                out rawHandle,
                desiredAccess,
                ref attributes,
                out _,
                IntPtr.Zero,
                directory
                    ? FileAttributeDirectory
                    : hiddenFile ? FileAttributeHidden : 0u,
                FileShareAll,
                FileCreate,
                createOptions,
                IntPtr.Zero,
                0);
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

    private static Win32Exception CreateNtException(
        int status,
        string parent,
        string child)
    {
        var error = RtlNtStatusToDosError(status);
        return new Win32Exception(
            unchecked((int)error),
            $"Could not create '{child}' relative to '{parent}'.");
    }

    private static int GetUnixDirectoryFlags(bool noFollow)
    {
        var flags = OperatingSystem.IsMacOS()
            ? 0x100000 | 0x1000000
            : 0x10000 | 0x80000;
        if (noFollow)
        {
            flags |= OperatingSystem.IsMacOS() ? 0x100 : 0x20000;
        }
        return flags;
    }

    private static int GetUnixWriteFlags() => OperatingSystem.IsMacOS()
        ? 0x1 | 0x200 | 0x800 | 0x100 | 0x1000000
        : 0x1 | 0x40 | 0x80 | 0x20000 | 0x80000;

    private static void RemoveDirectoryAtUnix(
        SafeFileHandle parentHandle,
        string childName)
    {
        var flags = OperatingSystem.IsMacOS() ? AtRemovedirMac : AtRemovedirLinux;
        if (UnlinkAt(
                parentHandle.DangerousGetHandle().ToInt32(),
                childName,
                flags) != 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not remove a pinned temporary directory.");
        }
    }

    private static void TryRemoveDirectoryAtUnix(
        SafeFileHandle parentHandle,
        string childName)
    {
        try
        {
            RemoveDirectoryAtUnix(parentHandle, childName);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == UnixNoEntry)
        {
        }
        catch (Win32Exception)
        {
            // A changed temporary path is preserved rather than recursively removed.
        }
    }

    private static void ValidateLeafName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name is "." or ".."
            || Path.IsPathRooted(name)
            || name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new ArgumentException(
                "A pinned filesystem operation requires one non-navigation path segment.",
                nameof(name));
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
