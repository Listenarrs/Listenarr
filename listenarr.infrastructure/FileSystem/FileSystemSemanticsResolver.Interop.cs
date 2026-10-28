using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Listenarr.Infrastructure.FileSystem;

public sealed partial class FileSystemSemanticsResolver
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileCaseSensitiveInformation
    {
        public uint Flags;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileWindows(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        out FileCaseSensitiveInformation fileInformation,
        uint bufferSize);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenUnix(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int IoctlUnix64(
        int descriptor,
        ulong request,
        out long flags);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int IoctlUnix32(
        int descriptor,
        ulong request,
        out int flags);

    [DllImport("libc", EntryPoint = "fstatfs", SetLastError = true)]
    private static extern int FStatFsUnix(int descriptor, IntPtr buffer);

    [DllImport("libc", EntryPoint = "pathconf", SetLastError = true)]
    private static extern long PathConfUnix(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int name);

    private static long? TryGetLinuxFileSystemType(int descriptor)
    {
        var buffer = Marshal.AllocHGlobal(LinuxStatFsBufferBytes);
        try
        {
            if (FStatFsUnix(descriptor, buffer) != 0)
            {
                return null;
            }

            return IntPtr.Size == sizeof(long)
                ? Marshal.ReadInt64(buffer)
                : Marshal.ReadInt32(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
