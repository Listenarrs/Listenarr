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

        return RenameNoReplaceLinuxCore(
            sourceDirectoryHandle.DangerousGetHandle().ToInt32(),
            sourceName,
            destinationDirectoryHandle.DangerousGetHandle().ToInt32(),
            finalName,
            NoReplaceRenamePrimitives.Native);
    }

    private const int UnixInvalidArgument = 22;
    private const int UnixFunctionNotImplemented = 38;
    private const int UnixOperationNotSupported = 95;

    /// <summary>
    /// The errno values under which <c>renameat2(RENAME_NOREPLACE)</c> means
    /// "this filesystem cannot do that", not "the rename was refused": NFS
    /// returns EINVAL for any rename flag, pre-3.15 kernels and some
    /// FUSE/overlay stacks return ENOSYS or EOPNOTSUPP.
    /// </summary>
    internal static bool IsNoReplaceRenameUnsupportedError(int nativeErrorCode) =>
        nativeErrorCode is UnixInvalidArgument
            or UnixFunctionNotImplemented
            or UnixOperationNotSupported;

    /// <summary>
    /// Native primitives behind the Linux no-replace rename, as delegates so the
    /// fallback sequencing is unit-testable without a filesystem that refuses
    /// <c>RENAME_NOREPLACE</c>. Each call captures errno immediately.
    /// </summary>
    internal sealed record NoReplaceRenamePrimitives(
        Func<int, string, int, string, (int Result, int Errno)> RenameNoReplace,
        Func<int, string, int, string, (int Result, int Errno)> Link,
        Func<int, string, (int Result, int Errno)> Unlink)
    {
        internal static readonly NoReplaceRenamePrimitives Native = new(
            (sourceFd, source, destinationFd, destination) =>
            {
                var result = RenameAtNoReplaceLinux(
                    sourceFd,
                    source,
                    destinationFd,
                    destination,
                    PinnedDirectoryCreation.RenameNoReplace);
                return (result, result == 0 ? 0 : Marshal.GetLastWin32Error());
            },
            (sourceFd, source, destinationFd, destination) =>
            {
                var result = LinkAt(sourceFd, source, destinationFd, destination, 0);
                return (result, result == 0 ? 0 : Marshal.GetLastWin32Error());
            },
            (directoryFd, name) =>
            {
                var result = UnlinkAt(directoryFd, name, 0);
                return (result, result == 0 ? 0 : Marshal.GetLastWin32Error());
            });
    }

    /// <summary>
    /// Atomic no-replace rename on Linux with a fallback for filesystems that
    /// reject <c>renameat2(RENAME_NOREPLACE)</c> outright (live case: a library
    /// on NFS 4.2, where every same-volume file move — split, transfer,
    /// organize — failed with EINVAL). The fallback keeps the no-replace
    /// contract without a check-then-act race: <c>linkat</c> refuses to
    /// overwrite an existing destination (EEXIST) and gives the destination the
    /// SAME inode, so pinned identity proofs still hold; the source name is then
    /// unlinked. Returns 0 on success or the errno callers should reason about:
    /// EEXIST when the destination already exists, the ORIGINAL unsupported
    /// errno when the filesystem cannot hard-link either (so existing
    /// "unsupported → verified copy" fallbacks still engage), or the unlink
    /// errno when the source name could not be removed (the new link is rolled
    /// back so the move is not half-applied).
    /// </summary>
    internal static int RenameNoReplaceLinuxCore(
        int sourceDirectoryFileDescriptor,
        string sourceName,
        int destinationDirectoryFileDescriptor,
        string finalName,
        NoReplaceRenamePrimitives primitives)
    {
        ArgumentNullException.ThrowIfNull(primitives);

        var rename = primitives.RenameNoReplace(
            sourceDirectoryFileDescriptor,
            sourceName,
            destinationDirectoryFileDescriptor,
            finalName);
        if (rename.Result == 0)
        {
            return 0;
        }
        if (!IsNoReplaceRenameUnsupportedError(rename.Errno))
        {
            return rename.Errno;
        }

        var link = primitives.Link(
            sourceDirectoryFileDescriptor,
            sourceName,
            destinationDirectoryFileDescriptor,
            finalName);
        if (link.Result != 0)
        {
            // EEXIST is the no-replace refusal callers expect; anything else
            // (EPERM/EXDEV/EMLINK/EOPNOTSUPP: no hard links here) means the
            // filesystem supports neither primitive — report the original
            // unsupported errno so the caller's copy fallback can take over.
            return link.Errno == UnixAlreadyExists ? link.Errno : rename.Errno;
        }

        var unlink = primitives.Unlink(sourceDirectoryFileDescriptor, sourceName);
        if (unlink.Result == 0 || unlink.Errno == UnixNoEntry)
        {
            // ENOENT: the source name vanished underneath us but the inode now
            // lives at the destination — the move is complete.
            return 0;
        }

        // The source name could not be removed: undo the new link so the file
        // is not left published under two names, then surface the real error.
        primitives.Unlink(destinationDirectoryFileDescriptor, finalName);
        return unlink.Errno;
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
        int nativeError;
        if (replaceExisting)
        {
            var result = RenameAtUnix(
                sourceDirectoryFileDescriptor,
                sourceName,
                destinationDirectoryFileDescriptor,
                finalName);
            nativeError = result == 0 ? 0 : Marshal.GetLastWin32Error();
        }
        else if (OperatingSystem.IsMacOS())
        {
            var result = RenameAtExclusiveMac(
                sourceDirectoryFileDescriptor,
                sourceName,
                destinationDirectoryFileDescriptor,
                finalName,
                RenameExclusiveMac);
            nativeError = result == 0 ? 0 : Marshal.GetLastWin32Error();
        }
        else
        {
            // Same no-replace contract as the probing variant, including the
            // hard-link fallback for filesystems that reject RENAME_NOREPLACE.
            nativeError = RenameNoReplaceLinuxCore(
                sourceDirectoryFileDescriptor,
                sourceName,
                destinationDirectoryFileDescriptor,
                finalName,
                NoReplaceRenamePrimitives.Native);
        }
        if (nativeError != 0)
        {
            throw new Win32Exception(
                nativeError,
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
