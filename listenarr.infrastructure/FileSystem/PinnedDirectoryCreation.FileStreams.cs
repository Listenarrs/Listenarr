using Microsoft.Win32.SafeHandles;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
    internal sealed partial class PinnedFileEntry
    {
        internal FileStream OpenIndependentReadStream(int bufferSize, bool asynchronous)
        {
            ThrowIfDisposed();
            var handle = OperatingSystem.IsWindows()
                ? OpenRelativeFileWindows(
                    _parentHandle,
                    _fileName,
                    FullPath,
                    requireDeleteAccess: false)
                : OpenRelativeFileUnix(_parentHandle, _fileName, FullPath);
            return OpenVerifiedIndependentStream(
                handle,
                FileAccess.Read,
                bufferSize,
                asynchronous);
        }

        internal FileStream OpenIndependentWriteStream(int bufferSize, bool asynchronous)
        {
            ThrowIfDisposed();
            var handle = OperatingSystem.IsWindows()
                ? OpenRelativeFileForWriteWindows(
                    _parentHandle,
                    _fileName,
                    FullPath)
                : OpenRelativeFileForWriteUnix(
                    _parentHandle,
                    _fileName,
                    FullPath);
            return OpenVerifiedIndependentStream(
                handle,
                FileAccess.Write,
                bufferSize,
                asynchronous);
        }

        private FileStream OpenVerifiedIndependentStream(
            SafeFileHandle handle,
            FileAccess access,
            int bufferSize,
            bool asynchronous)
        {
            try
            {
                if (!HandlesIdentifySameDirectory(_fileHandle, handle))
                {
                    throw new InvalidOperationException(
                        "The reopened pinned file does not identify the validated file object.");
                }

                return new FileStream(handle, access, bufferSize, asynchronous);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
    }
}
