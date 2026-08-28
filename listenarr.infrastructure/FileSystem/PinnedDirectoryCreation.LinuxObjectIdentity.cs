using System.ComponentModel;
using System.Runtime.InteropServices;
using Listenarr.Domain.Common;
using Microsoft.Win32.SafeHandles;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
    private const int LinuxAtHandleFid = 0x0200;
    private const int LinuxAtEmptyPath = 0x1000;
    private const int LinuxOperationNotPermitted = 1;
    private const int LinuxPermissionDenied = 13;
    private const int LinuxInvalidArgument = 22;
    private const int LinuxNotTy = 25;
    private const int LinuxFunctionNotImplemented = 38;
    private const int LinuxOverflow = 75;
    private const int LinuxOperationNotSupported = 95;
    private const int LinuxFileHandleHeaderBytes = 8;
    private const int LinuxInitialFileHandleBytes = 128;
    private const int LinuxMaximumFileHandleBytes = 4096;
    private const ulong LinuxFsIocGetVersion64 = 0x80087601;
    private const ulong LinuxFsIocGetVersion32 = 0x80047601;

    private static IReadOnlyList<string> GetLinuxGenerationIdentityCandidates(
        SafeFileHandle handle)
    {
        // This PoC deliberately leaves the final carrier open: a root folder,
        // storage endpoint, or mount provider can later select an explicit mode.
        const FileSystemObjectIdentityTrustMode trustMode =
            FileSystemObjectIdentityTrustMode.Auto;
        var fileSystemType = LinuxFileSystemType.TryGet(
            handle.DangerousGetHandle().ToInt32());
        var trustsFileHandle = CanUseLinuxFileHandleAsDurableGeneration(
            trustMode,
            fileSystemType);
        var fileHandle = trustsFileHandle
            ? TryGetLinuxFileHandleIdentity(
                handle,
                LinuxAtEmptyPath | LinuxAtHandleFid,
                retryWithoutHandleFid: true)
            : null;
        var hasInodeGeneration = false;
        var inodeGeneration = 0U;

        try
        {
            hasInodeGeneration = TryGetLinuxInodeGeneration(
                handle,
                out inodeGeneration);
        }
        catch (Win32Exception) when (!string.IsNullOrWhiteSpace(fileHandle))
        {
            // A second, supplementary capability failing unexpectedly must not
            // invalidate a strong identity already obtained from this pinned
            // object. Persisted identities that require the failed scheme will
            // still fail closed because their candidate will be absent.
        }

        return CreateLinuxGenerationIdentityCandidatesFromEvidence(
            fileHandle,
            hasInodeGeneration,
            inodeGeneration,
            trustMode,
            fileSystemType);
    }

    internal static IReadOnlyList<string>
        CreateLinuxGenerationIdentityCandidatesFromEvidence(
            string? fileHandle,
            bool hasInodeGeneration,
            uint inodeGeneration,
            FileSystemObjectIdentityTrustMode trustMode,
            long? fileSystemType)
    {
        var candidates = new List<string>(2);
        var hasFileHandle = !string.IsNullOrWhiteSpace(fileHandle);
        var trustsFileHandle = CanUseLinuxFileHandleAsDurableGeneration(
            trustMode,
            fileSystemType);
        if (hasFileHandle && trustsFileHandle)
        {
            candidates.Add($"fh:{fileHandle}");
        }
        if (hasInodeGeneration)
        {
            candidates.Add(FormattableString.Invariant(
                $"gen:{inodeGeneration:x8}"));
        }

        if (candidates.Count == 0 && !trustsFileHandle)
        {
            throw new PlatformNotSupportedException(
                CreateUntrustedLinuxFileHandleReason(trustMode, fileSystemType));
        }

        return candidates;
    }

    internal static bool CanUseLinuxFileHandleAsDurableGeneration(
        FileSystemObjectIdentityTrustMode trustMode,
        long? fileSystemType) =>
        trustMode switch
        {
            FileSystemObjectIdentityTrustMode.Trusted => true,
            FileSystemObjectIdentityTrustMode.Untrusted => false,
            FileSystemObjectIdentityTrustMode.Auto =>
                fileSystemType.HasValue
                && fileSystemType.Value != LinuxFileSystemType.FuseSuperMagic,
            _ => throw new ArgumentOutOfRangeException(nameof(trustMode))
        };

    private static string CreateUntrustedLinuxFileHandleReason(
        FileSystemObjectIdentityTrustMode trustMode,
        long? fileSystemType)
    {
        if (trustMode == FileSystemObjectIdentityTrustMode.Auto
            && fileSystemType == LinuxFileSystemType.FuseSuperMagic)
        {
            return "Opaque FUSE file-handle persistence is not trusted automatically, and the filesystem exposes no independent inode generation.";
        }

        if (trustMode == FileSystemObjectIdentityTrustMode.Auto)
        {
            return "Opaque Linux file-handle persistence is not trusted automatically when the filesystem type is unavailable, and the filesystem exposes no independent inode generation.";
        }

        return "Opaque Linux file-handle persistence is disabled by the filesystem object identity trust policy, and the filesystem exposes no independent inode generation.";
    }

    private static string? TryGetLinuxFileHandleIdentity(
        SafeFileHandle handle,
        int flags,
        bool retryWithoutHandleFid)
    {
        var capacity = LinuxInitialFileHandleBytes;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(
                LinuxFileHandleHeaderBytes + capacity);
            try
            {
                Marshal.WriteInt32(buffer, 0, capacity);
                Marshal.WriteInt32(buffer, sizeof(int), 0);
                if (NameToHandleAt(
                        handle.DangerousGetHandle().ToInt32(),
                        string.Empty,
                        buffer,
                        out _,
                        flags) == 0)
                {
                    var handleBytes = Marshal.ReadInt32(buffer, 0);
                    if (handleBytes <= 0 || handleBytes > capacity)
                    {
                        throw new InvalidOperationException(
                            "Linux returned an invalid filesystem file-handle length.");
                    }

                    var handleType = Marshal.ReadInt32(buffer, sizeof(int));
                    var bytes = new byte[handleBytes];
                    Marshal.Copy(
                        IntPtr.Add(buffer, LinuxFileHandleHeaderBytes),
                        bytes,
                        0,
                        handleBytes);
                    return FormattableString.Invariant(
                        $"{handleType:x8}:{Convert.ToHexString(bytes).ToLowerInvariant()}");
                }

                var error = Marshal.GetLastWin32Error();
                var requiredBytes = Marshal.ReadInt32(buffer, 0);
                if (error == LinuxOverflow
                    && requiredBytes > capacity
                    && requiredBytes <= LinuxMaximumFileHandleBytes)
                {
                    capacity = requiredBytes;
                    continue;
                }

                if (retryWithoutHandleFid
                    && error == LinuxInvalidArgument
                    && (flags & LinuxAtHandleFid) != 0)
                {
                    return TryGetLinuxFileHandleIdentity(
                        handle,
                        LinuxAtEmptyPath,
                        retryWithoutHandleFid: false);
                }

                if (IsUnavailableLinuxGenerationProbeError(error)
                    || error == LinuxOverflow)
                {
                    return null;
                }

                throw new Win32Exception(error);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    private static bool TryGetLinuxInodeGeneration(
        SafeFileHandle handle,
        out uint generation)
    {
        generation = 0;
        var fileDescriptor = handle.DangerousGetHandle().ToInt32();
        int result;
        if (IntPtr.Size == sizeof(long))
        {
            result = IoctlGetVersion64(
                fileDescriptor,
                LinuxFsIocGetVersion64,
                out var rawGeneration);
            if (result == 0)
            {
                generation = unchecked((uint)rawGeneration);
                return true;
            }
        }
        else
        {
            result = IoctlGetVersion32(
                fileDescriptor,
                LinuxFsIocGetVersion32,
                out var rawGeneration);
            if (result == 0)
            {
                generation = unchecked((uint)rawGeneration);
                return true;
            }
        }

        var error = Marshal.GetLastWin32Error();
        if (IsUnavailableLinuxGenerationProbeError(error))
        {
            return false;
        }

        throw new Win32Exception(error);
    }

    internal static bool IsUnavailableLinuxGenerationProbeError(int error) =>
        error is LinuxOperationNotPermitted
            or LinuxPermissionDenied
            or LinuxInvalidArgument
            or LinuxNotTy
            or LinuxFunctionNotImplemented
            or LinuxOperationNotSupported;

    [DllImport("libc", EntryPoint = "name_to_handle_at", SetLastError = true)]
    private static extern int NameToHandleAt(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        IntPtr handle,
        out int mountId,
        int flags);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int IoctlGetVersion64(
        int fileDescriptor,
        ulong request,
        out long version);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int IoctlGetVersion32(
        int fileDescriptor,
        ulong request,
        out int version);
}
