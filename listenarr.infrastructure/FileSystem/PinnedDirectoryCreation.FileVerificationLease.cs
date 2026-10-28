namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
    internal sealed partial class PinnedDirectoryAnchor
    {
        internal PinnedFileEntry OpenExistingFileForVerificationLease(
            string fileName)
        {
            ThrowIfDisposed();
            ValidateLeafName(fileName);
            var fullPath = Path.Join(FullPath, fileName);
            ExclusiveDirectoryCreator.InvokeBeforeOpenParentHook(fullPath);
            EnsureVisiblePathMatches();
            var handle = OperatingSystem.IsWindows()
                ? OpenRelativeFileVerificationLeaseWindows(
                    _handle,
                    fileName,
                    fullPath)
                : OpenRelativeFileUnix(_handle, fileName, fullPath);
            var entry = new PinnedFileEntry(
                DuplicateSafeHandle(_handle),
                handle,
                FullPath,
                fileName,
                _followVisibleFinalLink);
            if (entry.VisiblePathMatches())
            {
                return entry;
            }

            entry.Dispose();
            throw new InvalidOperationException(
                "The file changed while its verification lease was being opened.");
        }
    }
}
