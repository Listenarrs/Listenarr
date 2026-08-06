using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
    private static SafeFileHandle OpenVisibleDirectory(string path) =>
        OperatingSystem.IsWindows()
            ? OpenDirectoryWindows(path, openReparsePoint: true)
            : OpenDirectoryUnix(path, noFollow: true);

    private static void EnsureWindowsParentIsNotReparsePoint(
        SafeFileHandle handle,
        string path)
    {
        if (!GetFileAttributeTagInformationByHandleEx(
                handle,
                FileInformationClass.FileAttributeTagInfo,
                out var information,
                (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not inspect directory '{path}'.");
        }

        if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "A pinned directory parent cannot be a symbolic link or reparse point.");
        }
    }

    private static SafeFileHandle OpenDirectoryWindows(
        string path,
        bool openReparsePoint)
    {
        var flags = FileFlagBackupSemantics
            | (openReparsePoint ? FileFlagOpenReparsePoint : 0u);
        var handle = CreateFileWindows(
            path,
            FileListDirectory | FileReadAttributes | Synchronize,
            FileShareAll,
            IntPtr.Zero,
            OpenExisting,
            flags,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error, $"Could not open directory '{path}'.");
    }

    private static SafeFileHandle OpenDirectoryUnix(
        string path,
        bool noFollow)
    {
        var fd = OpenUnix(path, GetUnixDirectoryFlags(noFollow));
        if (fd >= 0)
        {
            return new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
        }

        throw new Win32Exception(
            Marshal.GetLastWin32Error(),
            $"Could not open directory '{path}'.");
    }

    private static SafeFileHandle OpenDirectoryAtUnix(
        SafeFileHandle parentHandle,
        string childName)
    {
        var fd = OpenAt(
            parentHandle.DangerousGetHandle().ToInt32(),
            childName,
            GetUnixDirectoryFlags(noFollow: true),
            mode: 0);
        if (fd >= 0)
        {
            return new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
        }

        throw new Win32Exception(
            Marshal.GetLastWin32Error(),
            "Could not open a newly created pinned directory.");
    }

    private static bool HandlesIdentifySameDirectory(
        SafeFileHandle expected,
        SafeFileHandle candidate)
    {
        if (OperatingSystem.IsWindows())
        {
            return GetWindowsIdentity(expected).Equals(GetWindowsIdentity(candidate));
        }
        if (OperatingSystem.IsLinux())
        {
            return GetLinuxIdentity(expected).Equals(GetLinuxIdentity(candidate));
        }
        if (OperatingSystem.IsMacOS())
        {
            return string.Equals(
                GetMacHandlePath(expected),
                GetMacHandlePath(candidate),
                StringComparison.Ordinal);
        }

        throw new PlatformNotSupportedException(
            "Pinned directory identity is supported only on Windows, Linux, and macOS.");
    }

    private static void FlushHandleToDisk(
        SafeFileHandle handle,
        string description)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!FlushFileBuffers(handle))
            {
                throw new PlatformNotSupportedException(
                    $"The filesystem could not durably flush {description} (Windows error {Marshal.GetLastWin32Error()}).");
            }
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            const int fullFileSystemSync = 51;
            if (FcntlGetPath(
                    handle.DangerousGetHandle().ToInt32(),
                    fullFileSystemSync,
                    IntPtr.Zero) != 0)
            {
                throw new PlatformNotSupportedException(
                    $"The filesystem could not provide a full durable flush for {description} (errno {Marshal.GetLastWin32Error()}).");
            }
            return;
        }
        if (OperatingSystem.IsLinux())
        {
            while (FSync(handle.DangerousGetHandle().ToInt32()) != 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 4)
                {
                    continue;
                }
                throw new PlatformNotSupportedException(
                    $"The filesystem could not durably flush {description} (errno {error}).");
            }
            return;
        }

        throw new PlatformNotSupportedException(
            "Durable filesystem barriers are supported only on Windows, Linux, and macOS.");
    }

    private static void FlushDirectoryPathToDisk(
        SafeFileHandle pinnedHandle,
        string path,
        bool followVisibleFinalLink)
    {
        if (!OperatingSystem.IsWindows())
        {
            FlushHandleToDisk(pinnedHandle, $"directory '{path}'");
            return;
        }

        using var flushHandle = CreateFileWindows(
            path,
            GenericRead | GenericWrite | Synchronize,
            FileShareAll,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics
                | (followVisibleFinalLink ? 0u : FileFlagOpenReparsePoint),
            IntPtr.Zero);
        if (flushHandle.IsInvalid)
        {
            throw new PlatformNotSupportedException(
                $"The filesystem could not open a durable directory barrier for '{path}' (Windows error {Marshal.GetLastWin32Error()}).");
        }
        if (!followVisibleFinalLink)
        {
            EnsureWindowsParentIsNotReparsePoint(flushHandle, path);
        }
        if (!HandlesIdentifySameDirectory(pinnedHandle, flushHandle))
        {
            throw new InvalidOperationException(
                "The directory changed while its durability barrier was opened.");
        }
        if (!FlushFileBuffers(flushHandle))
        {
            throw new PlatformNotSupportedException(
                $"The filesystem could not durably flush directory '{path}' (Windows error {Marshal.GetLastWin32Error()}).");
        }
    }

    private static string GetDirectoryObjectIdentity(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            var identity = GetWindowsIdentity(handle);
            if (!GetFileBasicInformationByHandleEx(
                    handle,
                    FileInformationClass.FileBasicInfo,
                    out var basic,
                    (uint)Marshal.SizeOf<FileBasicInformation>()))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return FormattableString.Invariant(
                $"windows:{identity.VolumeSerialNumber:x16}:{identity.LowPart:x16}:{identity.HighPart:x16}:{basic.CreationTime:x16}");
        }

        if (OperatingSystem.IsLinux())
        {
            const uint requiredMask = 0x00000100 | 0x00000800;
            if (Statx(
                    handle.DangerousGetHandle().ToInt32(),
                    string.Empty,
                    0x1000,
                    requiredMask,
                    out var information) != 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            if ((information.Mask & requiredMask) != requiredMask)
            {
                throw new PlatformNotSupportedException(
                    "The filesystem does not expose complete directory generation identity.");
            }

            var baseIdentity = FormattableString.Invariant(
                $"linux:{information.DeviceMajor:x8}:{information.DeviceMinor:x8}:{information.Inode:x16}:{information.BirthTime.Seconds:x16}:{information.BirthTime.Nanoseconds:x8}");
            var generationIdentity = TryGetLinuxGenerationIdentity(handle);
            return string.IsNullOrWhiteSpace(generationIdentity)
                ? baseIdentity
                : $"{baseIdentity}:{generationIdentity}";
        }

        if (OperatingSystem.IsMacOS())
        {
            if (FStatMac(
                    handle.DangerousGetHandle().ToInt32(),
                    out var information) != 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return FormattableString.Invariant(
                $"macos:{information.Device:x8}:{information.Inode:x16}:{information.BirthTime.Seconds:x16}:{information.BirthTime.Nanoseconds:x16}:{information.Generation:x8}");
        }

        throw new PlatformNotSupportedException(
            "Directory object identity is supported only on Windows, Linux, and macOS.");
    }

    private static WindowsFileIdentity GetWindowsIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(
                handle,
                FileInformationClass.FileIdInfo,
                out var information,
                (uint)Marshal.SizeOf<FileIdInformation>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return new WindowsFileIdentity(
            information.VolumeSerialNumber,
            information.FileId.LowPart,
            information.FileId.HighPart);
    }

    private static uint GetHandleLinkCount(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileStandardInformationByHandleEx(
                    handle,
                    FileInformationClass.FileStandardInfo,
                    out var information,
                    (uint)Marshal.SizeOf<FileStandardInformation>()))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return information.NumberOfLinks;
        }

        if (OperatingSystem.IsLinux())
        {
            const uint statxLinkCount = 0x00000004;
            if (Statx(
                    handle.DangerousGetHandle().ToInt32(),
                    string.Empty,
                    0x1000,
                    statxLinkCount,
                    out var information) != 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            if ((information.Mask & statxLinkCount) == 0)
            {
                throw new PlatformNotSupportedException(
                    "The filesystem does not expose a pinned file link count.");
            }

            return information.LinkCount;
        }

        if (OperatingSystem.IsMacOS())
        {
            if (FStatMac(
                    handle.DangerousGetHandle().ToInt32(),
                    out var information) != 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return information.LinkCount;
        }

        throw new PlatformNotSupportedException(
            "Pinned file link counts are supported only on Windows, Linux, and macOS.");
    }

    private static bool HandlesAreOnSameVolume(
        SafeFileHandle first,
        SafeFileHandle second)
    {
        if (OperatingSystem.IsWindows())
        {
            return GetWindowsIdentity(first).VolumeSerialNumber
                == GetWindowsIdentity(second).VolumeSerialNumber;
        }
        if (OperatingSystem.IsLinux())
        {
            var firstIdentity = GetLinuxIdentity(first);
            var secondIdentity = GetLinuxIdentity(second);
            return firstIdentity.DeviceMajor == secondIdentity.DeviceMajor
                && firstIdentity.DeviceMinor == secondIdentity.DeviceMinor
                && firstIdentity.MountId == secondIdentity.MountId;
        }
        if (OperatingSystem.IsMacOS())
        {
            if (FStatMac(first.DangerousGetHandle().ToInt32(), out var firstInfo) != 0
                || FStatMac(second.DangerousGetHandle().ToInt32(), out var secondInfo) != 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return firstInfo.Device == secondInfo.Device;
        }

        throw new PlatformNotSupportedException(
            "Filesystem-volume identity is supported only on Windows, Linux, and macOS.");
    }

    private static bool HasUnsupportedCrossVolumeMetadata(
        SafeFileHandle handle,
        bool requireSingleLink)
    {
        if (requireSingleLink && GetHandleLinkCount(handle) != 1)
        {
            return true;
        }
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            // ADS/security descriptors and resource forks/ACLs cannot be
            // reproduced by the managed fallback without loss.
            return true;
        }
        if (OperatingSystem.IsLinux())
        {
            var size = FListXattrLinux(
                handle.DangerousGetHandle().ToInt32(),
                IntPtr.Zero,
                0);
            if (size < 0)
            {
                throw new PlatformNotSupportedException(
                    $"Extended metadata could not be inspected before a cross-volume move (errno {Marshal.GetLastWin32Error()}).");
            }
            return size != 0;
        }

        return true;
    }

}
