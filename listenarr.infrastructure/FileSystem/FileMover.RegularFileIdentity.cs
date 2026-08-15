using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private readonly record struct RegularFileIdentity(
        byte Platform,
        ulong DeviceOrVolume,
        ulong DeviceDetail,
        ulong FileIdLow,
        ulong FileIdHigh);

    private static bool TryGetRegularFileIdentity(
        string path,
        out RegularFileIdentity identity)
    {
        identity = default;
        try
        {
            if (!File.Exists(path) || Directory.Exists(path))
            {
                return false;
            }

            using var handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.None);
            return TryGetRegularFileIdentity(handle, out identity);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or Win32Exception
                or PlatformNotSupportedException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool MatchesHardlinkSourceIdentity(
        PinnedDirectoryCreation.PinnedFileEntry entry,
        string persistedPhysicalObjectIdentity)
    {
        if (!OperatingSystem.IsWindows())
        {
            return string.Equals(
                entry.GetObjectIdentity(),
                persistedPhysicalObjectIdentity,
                StringComparison.Ordinal);
        }

        var parts = persistedPhysicalObjectIdentity.Split(':');
        if (parts.Length != 5
            || !string.Equals(parts[0], "windows", StringComparison.Ordinal)
            || !ulong.TryParse(
                parts[1],
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out var volume)
            || !ulong.TryParse(
                parts[2],
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out var fileIdLow)
            || !ulong.TryParse(
                parts[3],
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out var fileIdHigh))
        {
            return false;
        }

        using var handle = entry.DuplicateHandleForOperation();
        return TryGetRegularFileIdentity(handle, out var identity)
            && identity.Platform == 1
            && identity.DeviceOrVolume == volume
            && identity.FileIdLow == fileIdLow
            && identity.FileIdHigh == fileIdHigh;
    }

    private static bool TryGetRegularFileIdentity(
        SafeFileHandle handle,
        out RegularFileIdentity identity)
    {
        identity = default;
        if (OperatingSystem.IsWindows())
        {
            return TryGetWindowsRegularFileIdentity(handle, out identity);
        }
        if (OperatingSystem.IsLinux())
        {
            return TryGetUnixRegularFileIdentity(handle, linux: true, out identity);
        }
        if (OperatingSystem.IsMacOS())
        {
            return TryGetUnixRegularFileIdentity(handle, linux: false, out identity);
        }

        return false;
    }

    private static bool TryGetWindowsRegularFileIdentity(
        SafeFileHandle handle,
        out RegularFileIdentity identity)
    {
        identity = default;
        if (!GetFileInformationByHandleExForRegularFile(
                handle,
                RegularFileInformationClass.FileIdInfo,
                out var information,
                (uint)Marshal.SizeOf<RegularFileIdInformation>()))
        {
            return false;
        }

        identity = new RegularFileIdentity(
            Platform: 1,
            information.VolumeSerialNumber,
            DeviceDetail: 0,
            information.FileId.LowPart,
            information.FileId.HighPart);
        return true;
    }

    private static bool TryGetUnixRegularFileIdentity(
        SafeFileHandle handle,
        bool linux,
        out RegularFileIdentity identity)
    {
        identity = default;
        if (!Environment.Is64BitProcess)
        {
            return false;
        }

        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            for (var offset = 0; offset < 256; offset += sizeof(long))
            {
                Marshal.WriteInt64(buffer, offset, 0);
            }

            if (FStatRegularFile(handle.DangerousGetHandle().ToInt32(), buffer) != 0)
            {
                return false;
            }

            var device = linux
                ? unchecked((ulong)Marshal.ReadInt64(buffer, 0))
                : unchecked((uint)Marshal.ReadInt32(buffer, 0));
            var inode = unchecked((ulong)Marshal.ReadInt64(buffer, 8));
            identity = new RegularFileIdentity(
                Platform: linux ? (byte)2 : (byte)3,
                device,
                DeviceDetail: 0,
                inode,
                FileIdHigh: 0);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private enum RegularFileInformationClass
    {
        FileIdInfo = 18
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RegularFileId128
    {
        public ulong LowPart;
        public ulong HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RegularFileIdInformation
    {
        public ulong VolumeSerialNumber;
        public RegularFileId128 FileId;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleExForRegularFile(
        SafeFileHandle fileHandle,
        RegularFileInformationClass fileInformationClass,
        out RegularFileIdInformation fileInformation,
        uint bufferSize);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStatRegularFile(int fileDescriptor, IntPtr buffer);
}
