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

        // Tag libraries rewrite a container in place: they parse the existing box or frame
        // structure through the same stream they then write back to, so a write-only handle
        // fails while still reading. The pinning is unaffected by the wider access mode — the
        // handle is still opened relative to the pinned parent, still refuses to follow a
        // link, and is still checked against the validated file object below.
        internal FileStream OpenIndependentReadWriteStream(int bufferSize, bool asynchronous)
        {
            ThrowIfDisposed();
            var handle = OperatingSystem.IsWindows()
                ? OpenRelativeFileForWriteWindows(
                    _parentHandle,
                    _fileName,
                    FullPath,
                    readable: true)
                : OpenRelativeFileForWriteUnix(
                    _parentHandle,
                    _fileName,
                    FullPath,
                    readable: true);
            return OpenVerifiedIndependentStream(
                handle,
                FileAccess.ReadWrite,
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
