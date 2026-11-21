using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Listenarr.Infrastructure.FileSystem;

internal static class HardlinkIdentityCapability
{
    internal static bool CanVerify(string source)
    {
        if (!OperatingSystem.IsLinux()) return true;
        try
        {
            using var parent = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                Path.GetDirectoryName(Path.GetFullPath(source))!, createMissing: false);
            using var entry = parent.OpenExistingFile(Path.GetFileName(source), requireDeleteAccess: false);
            using var handle = entry.DuplicateHandleForOperation();
            return CanVerify(handle);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    internal static bool CanVerify(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsLinux()) return true;
        // fstatfs writes a platform-dependent struct. Only its first native-long
        // field (filesystem type) is needed; reserve the same generous buffer as
        // the semantics resolver rather than relying on managed struct packing.
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            if (FStatFs(handle.DangerousGetHandle().ToInt32(), buffer) != 0) return false;
            var type = IntPtr.Size == sizeof(long) ? Marshal.ReadInt64(buffer) : Marshal.ReadInt32(buffer);
            return SupportsFileSystemType(unchecked((uint)type));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static bool SupportsFileSystemType(uint type) =>
        // CIFS can allocate distinct client inode numbers for two names of one
        // server object. Neither SMB version nor serverino proves link equality.
        // FUSE (0x65735546) covers userspace mounts such as rclone, sshfs or s3fs:
        // many reject link() outright and none guarantees a stable link identity.
        type is not (0xff534d42 or 0xfe534d42 or 0x65735546);

    [DllImport("libc", EntryPoint = "fstatfs", SetLastError = true)]
    private static extern int FStatFs(int descriptor, IntPtr buffer);
}
