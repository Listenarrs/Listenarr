using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
    internal sealed partial class PinnedDirectoryAnchor
    {
    }

    private static int TryRenameRelativeEntryNoReplaceLinux(
        SafeFileHandle sourceDirectoryHandle,
        string sourceName,
        SafeFileHandle destinationDirectoryHandle,
        string finalName)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "The non-throwing no-replace rename probe is Linux-specific.");
        }

        var result = RenameAtNoReplaceLinux(
            sourceDirectoryHandle.DangerousGetHandle().ToInt32(),
            sourceName,
            destinationDirectoryHandle.DangerousGetHandle().ToInt32(),
            finalName,
            RenameNoReplace);
        return result == 0 ? 0 : Marshal.GetLastWin32Error();
    }

    private static void RenameRelativeEntry(
        SafeFileHandle sourceDirectoryHandle,
        SafeFileHandle entryHandle,
        string sourceName,
        SafeFileHandle destinationDirectoryHandle,
        string finalName,
        bool replaceExisting = false)
    {
        if (OperatingSystem.IsWindows())
        {
            RenameRelativeEntryWindows(
                destinationDirectoryHandle,
                entryHandle,
                finalName,
                replaceExisting);
            return;
        }

        var sourceDirectoryFileDescriptor = sourceDirectoryHandle
            .DangerousGetHandle()
            .ToInt32();
        var destinationDirectoryFileDescriptor = destinationDirectoryHandle
            .DangerousGetHandle()
            .ToInt32();
        var result = replaceExisting
            ? RenameAtUnix(
                sourceDirectoryFileDescriptor,
                sourceName,
                destinationDirectoryFileDescriptor,
                finalName)
            : OperatingSystem.IsMacOS()
            ? RenameAtExclusiveMac(
                sourceDirectoryFileDescriptor,
                sourceName,
                destinationDirectoryFileDescriptor,
                finalName,
                RenameExclusiveMac)
            : RenameAtNoReplaceLinux(
                sourceDirectoryFileDescriptor,
                sourceName,
                destinationDirectoryFileDescriptor,
                finalName,
                RenameNoReplace);
        if (result != 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not publish a pinned filesystem entry relative to its owned directory.");
        }
    }

    private static void RenameRelativeEntryWindows(
        SafeFileHandle directoryHandle,
        SafeFileHandle entryHandle,
        string finalName,
        bool replaceExisting)
    {
        var fileNameBytes = Encoding.Unicode.GetBytes(finalName);
        var rootDirectoryOffset = IntPtr.Size == 8 ? 8 : 4;
        var fileNameLengthOffset = rootDirectoryOffset + IntPtr.Size;
        var fileNameOffset = fileNameLengthOffset + sizeof(uint);
        var bufferSize = checked(fileNameOffset + fileNameBytes.Length);
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            for (var index = 0; index < bufferSize; index++)
            {
                Marshal.WriteByte(buffer, index, 0);
            }

            const int fileRenameInformation = 10;
            const int fileRenameInformationEx = 65;
            const int fileRenameReplaceIfExists = 0x00000001;
            const int fileRenamePosixSemantics = 0x00000002;
            if (replaceExisting)
            {
                Marshal.WriteInt32(
                    buffer,
                    0,
                    fileRenameReplaceIfExists | fileRenamePosixSemantics);
            }
            else
            {
                Marshal.WriteByte(buffer, 0, 0);
            }
            Marshal.WriteIntPtr(
                buffer,
                rootDirectoryOffset,
                directoryHandle.DangerousGetHandle());
            Marshal.WriteInt32(buffer, fileNameLengthOffset, fileNameBytes.Length);
            Marshal.Copy(fileNameBytes, 0, buffer + fileNameOffset, fileNameBytes.Length);
            var status = NtSetInformationFile(
                entryHandle,
                out _,
                buffer,
                checked((uint)bufferSize),
                replaceExisting
                    ? fileRenameInformationEx
                    : fileRenameInformation);
            if (status < 0)
            {
                var error = unchecked((int)RtlNtStatusToDosError(status));
                throw new Win32Exception(
                    error,
                    $"Could not publish a pinned filesystem entry relative to its owned directory (Windows error {error}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

}
