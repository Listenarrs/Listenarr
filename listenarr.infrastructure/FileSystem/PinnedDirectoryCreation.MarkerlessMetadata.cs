using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
    private static SafeFileHandle OpenRelativeFileForMetadataWindows(
        SafeFileHandle parentHandle,
        string fileName,
        string fullPath)
    {
        var nameBuffer = Marshal.StringToHGlobalUni(fileName);
        var unicodeStringPointer = IntPtr.Zero;
        try
        {
            var unicodeString = new UnicodeString
            {
                Length = checked((ushort)(fileName.Length * sizeof(char))),
                MaximumLength = checked((ushort)((fileName.Length + 1) * sizeof(char))),
                Buffer = nameBuffer
            };
            unicodeStringPointer = Marshal.AllocHGlobal(
                Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(
                unicodeString,
                unicodeStringPointer,
                fDeleteOld: false);
            var attributes = new ObjectAttributes
            {
                Length = (uint)Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parentHandle.DangerousGetHandle(),
                ObjectName = unicodeStringPointer
            };
            var status = NtCreateFile(
                out var rawHandle,
                FileReadAttributes | FileWriteAttributes | Synchronize,
                ref attributes,
                out _,
                IntPtr.Zero,
                fileAttributes: 0,
                FileShareAll,
                FileOpen,
                FileNonDirectoryFile | FileSynchronousIoNonAlert
                    | FileOpenReparsePoint,
                IntPtr.Zero,
                0);
            if (status < 0)
            {
                throw CreateNtOpenException(status, fullPath);
            }

            var handle = new SafeFileHandle(rawHandle, ownsHandle: true);
            try
            {
                EnsureFileHandleIsNotReparsePoint(handle, fullPath);
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
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

    internal sealed partial class PinnedFileEntry
    {
        internal bool MatchesMetadata(
            long expectedLength,
            DateTime expectedLastWriteTimeUtc)
        {
            ThrowIfDisposed();
            using var stream = OpenReadStream(
                bufferSize: 1,
                asynchronous: false);
            return stream.Length == expectedLength
                && GetLastWriteTimeUtc() == expectedLastWriteTimeUtc;
        }

        internal void PreserveMetadataTo(PinnedFileEntry destination)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(destination);
            destination.ThrowIfDisposed();
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    destination._fileHandle,
                    File.GetUnixFileMode(_fileHandle));

                // UnixFileMode is the authoritative permission contract on Unix.
                // FileAttributes.ReadOnly reflects the current caller's effective
                // access, so copying it from a differently owned source can remove
                // the destination owner's write bit after the mode was preserved.
                File.SetLastWriteTimeUtc(
                    destination._fileHandle,
                    GetLastWriteTimeUtc());
                File.SetCreationTimeUtc(
                    destination._fileHandle,
                    File.GetCreationTimeUtc(_fileHandle));
                return;
            }
            File.SetAttributes(
                destination._fileHandle,
                File.GetAttributes(_fileHandle));
            File.SetLastWriteTimeUtc(
                destination._fileHandle,
                GetLastWriteTimeUtc());
            File.SetCreationTimeUtc(
                destination._fileHandle,
                File.GetCreationTimeUtc(_fileHandle));
        }

        internal void PreserveMarkerlessMetadataTo(PinnedFileEntry destination)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(destination);
            destination.ThrowIfDisposed();
            if (OperatingSystem.IsWindows())
            {
                PreserveMarkerlessMetadataWindows(destination);
                return;
            }

            File.SetUnixFileMode(
                destination._fileHandle,
                File.GetUnixFileMode(_fileHandle));
            File.SetLastWriteTimeUtc(
                destination._fileHandle,
                GetLastWriteTimeUtc());
        }

        private void PreserveMarkerlessMetadataWindows(
            PinnedFileEntry destination)
        {
            using var metadataHandle = OpenRelativeFileForMetadataWindows(
                destination._parentHandle,
                destination._fileName,
                destination.FullPath);
            if (!HandlesIdentifySameDirectory(
                    destination._fileHandle,
                    metadataHandle))
            {
                throw new InvalidOperationException(
                    "The markerless destination changed before metadata preservation.");
            }

            if (!GetFileBasicInformationByHandleEx(
                    _fileHandle,
                    FileInformationClass.FileBasicInfo,
                    out var sourceInformation,
                    (uint)Marshal.SizeOf<FileBasicInformation>()))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not read markerless source metadata from the pinned file handle.");
            }

            if (!GetFileBasicInformationByHandleEx(
                    metadataHandle,
                    FileInformationClass.FileBasicInfo,
                    out var destinationInformation,
                    (uint)Marshal.SizeOf<FileBasicInformation>()))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not read markerless destination metadata from the pinned file handle.");
            }

            destinationInformation.LastWriteTime = sourceInformation.LastWriteTime;
            destinationInformation.FileAttributes = sourceInformation.FileAttributes;
            var buffer = Marshal.AllocHGlobal(
                Marshal.SizeOf<FileBasicInformation>());
            try
            {
                Marshal.StructureToPtr(
                    destinationInformation,
                    buffer,
                    fDeleteOld: false);
                if (!SetFileInformationByHandle(
                        metadataHandle,
                        FileInformationClass.FileBasicInfo,
                        buffer,
                        (uint)Marshal.SizeOf<FileBasicInformation>()))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not preserve markerless destination metadata on the pinned file handle.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
