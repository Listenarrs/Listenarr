using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
    private static LinuxFileIdentity GetLinuxIdentity(SafeFileHandle handle)
    {
        if (TryGetLinuxProcFdIdentity(handle, out var procIdentity))
        {
            return procIdentity;
        }

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

    private static bool TryGetLinuxProcFdIdentity(
        SafeFileHandle handle,
        out LinuxFileIdentity identity)
    {
        identity = default;
        var descriptor = handle.DangerousGetHandle().ToInt32();
        var fdInfoPath = FormattableString.Invariant(
            $"/proc/{Environment.ProcessId}/fdinfo/{descriptor}");
        try
        {
            ulong? mountId = null;
            ulong? inode = null;
            foreach (var line in File.ReadLines(fdInfoPath))
            {
                if (line.StartsWith("mnt_id:", StringComparison.Ordinal)
                    && ulong.TryParse(
                        line.AsSpan("mnt_id:".Length).Trim(),
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var parsedMountId))
                {
                    mountId = parsedMountId;
                }
                else if (line.StartsWith("ino:", StringComparison.Ordinal)
                    && ulong.TryParse(
                        line.AsSpan("ino:".Length).Trim(),
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var parsedInode))
                {
                    inode = parsedInode;
                }
            }

            if (!mountId.HasValue || !inode.HasValue)
            {
                return false;
            }

            // Mount ID + inode are sufficient for operation-local handle/path
            // equality while the pinned handle remains open. Durable mutation
            // authority is captured separately and never derives from this tuple.
            identity = new LinuxFileIdentity(0, 0, inode.Value, mountId.Value);
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or FormatException or OverflowException)
        {
            return false;
        }
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
