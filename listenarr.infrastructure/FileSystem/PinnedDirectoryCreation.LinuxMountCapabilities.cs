using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
    private const ulong LinuxStatVfsReadOnly = 1;

    internal sealed partial class PinnedDirectoryAnchor
    {
        internal bool IsLinuxFileSystemReadOnly()
        {
            ThrowIfDisposed();
            if (!OperatingSystem.IsLinux())
            {
                return false;
            }
            if (IntPtr.Size != sizeof(long))
            {
                throw new PlatformNotSupportedException(
                    "Linux filesystem mount capability probing requires a 64-bit process.");
            }

            if (FStatVfs(
                    _handle.DangerousGetHandle().ToInt32(),
                    out var information) != 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return (information.Flags & LinuxStatVfsReadOnly) != 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStatVfs
    {
        public ulong BlockSize;
        public ulong FragmentSize;
        public ulong Blocks;
        public ulong BlocksFree;
        public ulong BlocksAvailable;
        public ulong Files;
        public ulong FilesFree;
        public ulong FilesAvailable;
        public ulong FileSystemId;
        public ulong Flags;
        public ulong NameMax;
        public uint FileSystemType;
        public int Spare0;
        public int Spare1;
        public int Spare2;
        public int Spare3;
        public int Spare4;
    }

    [DllImport("libc", EntryPoint = "fstatvfs", SetLastError = true)]
    private static extern int FStatVfs(
        int fileDescriptor,
        out LinuxStatVfs information);
}
