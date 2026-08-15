using System.ComponentModel;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover : IFilePublicationSourceCapability
{
    public Task<FilePublicationSourceCapabilityResult> CheckAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        try
        {
            var fullPath = Path.GetFullPath(sourcePath);
            var parent = Path.GetDirectoryName(fullPath);
            var fileName = Path.GetFileName(fullPath);
            if (string.IsNullOrWhiteSpace(parent)
                || string.IsNullOrWhiteSpace(fileName))
            {
                return Task.FromResult(
                    FilePublicationSourceCapabilityResult.Unsupported(
                        "The source path does not identify a file beneath a directory."));
            }

            using var anchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(parent);
            using var entry = anchor.TryOpenExistingFile(
                fileName,
                requireDeleteAccess: false);
            if (entry == null || !entry.VisiblePathMatches())
            {
                return Task.FromResult(
                    FilePublicationSourceCapabilityResult.Unsupported(
                        "The source file is unavailable or changed while its identity is being verified."));
            }

            _ = entry.GetObjectIdentity();
            if (!anchor.VisiblePathMatches()
                || !entry.VisiblePathMatches())
            {
                return Task.FromResult(
                    FilePublicationSourceCapabilityResult.Unsupported(
                        "The source file changed while its durable identity was being verified."));
            }

            return Task.FromResult(FilePublicationSourceCapabilityResult.Supported);
        }
        catch (PlatformNotSupportedException exception)
        {
            return Task.FromResult(
                FilePublicationSourceCapabilityResult.Unsupported(exception.Message));
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or Win32Exception
                or InvalidOperationException or NotSupportedException
                or PathTooLongException or System.Security.SecurityException)
        {
            return Task.FromResult(
                FilePublicationSourceCapabilityResult.Unsupported(
                    "The source file cannot be pinned to a durable physical generation."));
        }
    }
}
