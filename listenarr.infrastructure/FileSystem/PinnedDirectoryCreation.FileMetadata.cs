namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
    internal sealed partial class PinnedFileEntry
    {
        internal long GetLength()
        {
            ThrowIfDisposed();
            return RandomAccess.GetLength(_fileHandle);
        }

        internal DateTime GetLastWriteTimeUtc()
        {
            ThrowIfDisposed();
            return File.GetLastWriteTimeUtc(_fileHandle);
        }

        internal bool IsRegularFile()
        {
            ThrowIfDisposed();
            return HandleIsRegularFile(_fileHandle);
        }
    }
}
