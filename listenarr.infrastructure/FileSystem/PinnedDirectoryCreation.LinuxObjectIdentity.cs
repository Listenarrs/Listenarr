using System.ComponentModel;
using System.Runtime.InteropServices;
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
    private const int LinuxGenericFileIdentifierType = 0x81;
    private const ulong LinuxFsIocGetVersion64 = 0x80087601;
    private const ulong LinuxFsIocGetVersion32 = 0x80047601;

    private static IReadOnlyList<string> GetLinuxGenerationIdentityCandidates(
        SafeFileHandle handle)
    {
        var candidates = new List<string>(3);
        var fileHandleEvidence = TryGetLinuxFileHandleEvidence(
            handle,
            LinuxAtEmptyPath | LinuxAtHandleFid,
            retryWithoutHandleFid: true);
        if (fileHandleEvidence != null)
        {
            candidates.Add(fileHandleEvidence.ToGenerationIdentity());
            if (!fileHandleEvidence.IsDurableGenerationEvidence)
            {
                // AT_HANDLE_FID may deliberately return the generic exportfs
                // FILEID_INO64_GEN representation even when the filesystem can
                // provide a stronger ordinary handle. Probe without FID before
                // concluding that only weak evidence is available.
                var ordinaryFileHandleEvidence = TryGetLinuxFileHandleEvidence(
                    handle,
                    LinuxAtEmptyPath,
                    retryWithoutHandleFid: false);
                if (ordinaryFileHandleEvidence != null
                    && !ordinaryFileHandleEvidence.HasSameHandle(fileHandleEvidence))
                {
                    candidates.Add(ordinaryFileHandleEvidence.ToGenerationIdentity());
                }
            }
        }

        try
        {
            if (TryGetLinuxInodeGeneration(handle, out var generation))
            {
                candidates.Add(FormattableString.Invariant($"gen:{generation:x8}"));
            }
        }
        catch (Win32Exception) when (candidates.Any(IsDurableLinuxGenerationIdentity))
        {
            // A second, supplementary capability failing unexpectedly must not
            // invalidate a strong identity already obtained from this pinned
            // object. A generic FILEID_INO64_GEN FID is deliberately excluded
            // here because it is compatibility evidence, not durable authority.
        }

        return candidates;
    }

    private static LinuxFileHandleEvidence? TryGetLinuxFileHandleEvidence(
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
                    return new LinuxFileHandleEvidence(
                        handleType,
                        bytes,
                        (flags & LinuxAtHandleFid) != 0
                            ? LinuxFileHandleProbeKind.FileIdentifier
                            : LinuxFileHandleProbeKind.FileHandle);
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
                    && (flags & LinuxAtHandleFid) != 0
                    && (IsUnavailableLinuxGenerationProbeError(error)
                        || error == LinuxOverflow))
                {
                    return TryGetLinuxFileHandleEvidence(
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

    private enum LinuxFileHandleProbeKind
    {
        FileHandle,
        FileIdentifier
    }

    private sealed record LinuxFileHandleEvidence(
        int HandleType,
        byte[] Bytes,
        LinuxFileHandleProbeKind ProbeKind)
    {
        internal bool IsDurableGenerationEvidence =>
            HandleType != LinuxGenericFileIdentifierType;

        internal string ToGenerationIdentity() =>
            FormattableString.Invariant(
                $"fh:{HandleType:x8}:{Convert.ToHexString(Bytes).ToLowerInvariant()}");

        internal bool HasSameHandle(LinuxFileHandleEvidence other) =>
            HandleType == other.HandleType
            && Bytes.AsSpan().SequenceEqual(other.Bytes);
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
