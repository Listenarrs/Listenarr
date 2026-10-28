using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
    private static PinnedDirectoryCreation TryCreateRelative(
        SafeFileHandle parentHandle,
        string parentPath,
        string childName,
        bool requireDirectoryDeleteAccess,
        bool parentFollowsVisibleFinalLink) => OperatingSystem.IsWindows()
            ? TryCreateRelativeWindows(
                parentHandle,
                parentPath,
                childName,
                requireDirectoryDeleteAccess,
                parentFollowsVisibleFinalLink)
            : TryCreateRelativeUnix(
                parentHandle,
                parentPath,
                childName,
                parentFollowsVisibleFinalLink);

    private static PinnedDirectoryCreation TryCreateRelativeWindows(
        SafeFileHandle parentHandle,
        string parentPath,
        string childName,
        bool requireDirectoryDeleteAccess,
        bool parentFollowsVisibleFinalLink)
    {
        var ownedParentHandle = DuplicateSafeHandle(parentHandle);
        try
        {
            var status = CreateRelativeWindows(
                parentHandle,
                childName,
                directory: true,
                hiddenFile: false,
                requireDirectoryDeleteAccess,
                out var rawHandle);
            if (status == StatusObjectNameCollision)
            {
                return new PinnedDirectoryCreation(
                    ownedParentHandle,
                    directoryHandle: null,
                    parentPath,
                    childName,
                    created: false,
                    parentFollowsVisibleFinalLink);
            }
            if (status < 0)
            {
                throw CreateNtException(status, parentPath, childName);
            }

            return new PinnedDirectoryCreation(
                ownedParentHandle,
                new SafeFileHandle(rawHandle, ownsHandle: true),
                parentPath,
                childName,
                created: true,
                parentFollowsVisibleFinalLink);
        }
        catch
        {
            ownedParentHandle.Dispose();
            throw;
        }
    }

    private static PinnedDirectoryCreation TryCreateRelativeUnix(
        SafeFileHandle parentHandle,
        string parentPath,
        string childName,
        bool parentFollowsVisibleFinalLink)
    {
        var ownedParentHandle = DuplicateSafeHandle(parentHandle);
        SafeFileHandle? directoryHandle = null;
        try
        {
            var parentFd = parentHandle.DangerousGetHandle().ToInt32();
            if (MkdirAt(parentFd, childName, UnixDirectoryMode) != 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == UnixAlreadyExists)
                {
                    return new PinnedDirectoryCreation(
                        ownedParentHandle,
                        directoryHandle: null,
                        parentPath,
                        childName,
                        created: false,
                        parentFollowsVisibleFinalLink);
                }

                throw new Win32Exception(
                    error,
                    $"Could not create the requested directory beneath '{parentPath}'.");
            }

            PinnedFilesystemMutationHooks.InvokeAfterUnixDirectoryCreateBeforeOpen(
                Path.Join(parentPath, childName));
            directoryHandle = OpenDirectoryAtUnix(parentHandle, childName);
            var created = new PinnedDirectoryCreation(
                ownedParentHandle,
                directoryHandle,
                parentPath,
                childName,
                created: true,
                parentFollowsVisibleFinalLink);
            directoryHandle = null;
            if (!created.VisiblePathMatches())
            {
                created.Dispose();
                throw new InvalidOperationException(
                    "The newly created directory changed before it could be pinned.");
            }

            return created;
        }
        catch
        {
            directoryHandle?.Dispose();
            ownedParentHandle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenRelativeDirectoryWindows(
        SafeFileHandle parentHandle,
        string childName,
        string childPath,
        bool requireDeleteAccess = false)
    {
        var status = OpenRelativeDirectoryWindowsCore(
            parentHandle,
            childName,
            requireDeleteAccess,
            out var rawHandle);
        if (status < 0)
        {
            throw CreateNtOpenException(status, childPath);
        }

        var handle = new SafeFileHandle(rawHandle, ownsHandle: true);
        try
        {
            EnsureWindowsParentIsNotReparsePoint(handle, childPath);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static int OpenRelativeDirectoryWindowsCore(
        SafeFileHandle parentHandle,
        string childName,
        bool requireDeleteAccess,
        out IntPtr rawHandle)
    {
        var nameBuffer = Marshal.StringToHGlobalUni(childName);
        var unicodeStringPointer = IntPtr.Zero;
        try
        {
            var unicodeString = new UnicodeString
            {
                Length = checked((ushort)(childName.Length * sizeof(char))),
                MaximumLength = checked((ushort)((childName.Length + 1) * sizeof(char))),
                Buffer = nameBuffer
            };
            unicodeStringPointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(unicodeString, unicodeStringPointer, fDeleteOld: false);
            var attributes = new ObjectAttributes
            {
                Length = (uint)Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parentHandle.DangerousGetHandle(),
                ObjectName = unicodeStringPointer
            };
            var desiredAccess = FileListDirectory | FileReadAttributes | Synchronize
                | (requireDeleteAccess ? DeleteAccess : 0u);
            return NtCreateFile(
                out rawHandle,
                desiredAccess,
                ref attributes,
                out _,
                IntPtr.Zero,
                FileAttributeDirectory,
                FileShareAll,
                FileOpen,
                FileDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint,
                IntPtr.Zero,
                0);
        }
        finally
        {
            if (unicodeStringPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(unicodeStringPointer);
            }
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static Win32Exception CreateNtOpenException(int status, string path)
    {
        var error = RtlNtStatusToDosError(status);
        return new Win32Exception(
            unchecked((int)error),
            $"Could not open pinned filesystem entry '{path}' (Windows error {error}).");
    }

    private static SafeFileHandle DuplicateSafeHandle(SafeFileHandle sourceHandle)
    {
        if (OperatingSystem.IsWindows())
        {
            var process = GetCurrentProcess();
            if (DuplicateHandleWindows(
                    process,
                    sourceHandle.DangerousGetHandle(),
                    process,
                    out var duplicate,
                    desiredAccess: 0,
                    inheritHandle: false,
                    DuplicateSameAccess))
            {
                return duplicate;
            }

            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not duplicate a pinned directory handle.");
        }

        var descriptor = DuplicateFileDescriptor(
            sourceHandle.DangerousGetHandle().ToInt32());
        if (descriptor >= 0)
        {
            return new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
        }

        throw new Win32Exception(
            Marshal.GetLastWin32Error(),
            "Could not duplicate a pinned directory descriptor.");
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", EntryPoint = "DuplicateHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandleWindows(
        IntPtr sourceProcessHandle,
        IntPtr sourceHandle,
        IntPtr targetProcessHandle,
        out SafeFileHandle targetHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static extern int DuplicateFileDescriptor(int fileDescriptor);
}
