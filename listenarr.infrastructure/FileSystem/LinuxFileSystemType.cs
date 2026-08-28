using System.Runtime.InteropServices;

namespace Listenarr.Infrastructure.FileSystem;

internal static class LinuxFileSystemType
{
    internal const long FuseSuperMagic = 0x65735546L;

    private const int StatFsBufferBytes = 256;

    internal static long? TryGet(int descriptor)
    {
        var buffer = Marshal.AllocHGlobal(StatFsBufferBytes);
        try
        {
            if (FStatFs(descriptor, buffer) != 0)
            {
                return null;
            }

            // Linux statfs begins with a native long f_type. The remaining
            // fields are architecture-specific and intentionally not mirrored.
            return IntPtr.Size == sizeof(long)
                ? Marshal.ReadInt64(buffer)
                : Marshal.ReadInt32(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("libc", EntryPoint = "fstatfs", SetLastError = true)]
    private static extern int FStatFs(int descriptor, IntPtr buffer);
}
