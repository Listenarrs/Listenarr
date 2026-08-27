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
            if (!OperatingSystem.IsLinux())
            {
                return File.GetLastWriteTimeUtc(_fileHandle);
            }

            var before = ProbePublicPathMatch();
            if (before == RegistrationPublicationMatchOutcome.Unavailable)
            {
                throw new IOException(
                    "The pinned file is temporarily unavailable while its modification time is being read.");
            }
            if (before != RegistrationPublicationMatchOutcome.Match)
            {
                throw new InvalidOperationException(
                    "The pinned file changed before its modification time could be read.");
            }

            var lastWriteTimeUtc = File.GetLastWriteTimeUtc(FullPath);

            var after = ProbePublicPathMatch();
            if (after == RegistrationPublicationMatchOutcome.Unavailable)
            {
                throw new IOException(
                    "The pinned file became temporarily unavailable while its modification time was being read.");
            }
            if (after != RegistrationPublicationMatchOutcome.Match)
            {
                throw new InvalidOperationException(
                    "The pinned file changed while its modification time was being read.");
            }

            return lastWriteTimeUtc;
        }

        internal bool IsRegularFile()
        {
            ThrowIfDisposed();
            return HandleIsRegularFile(_fileHandle);
        }
    }
}
