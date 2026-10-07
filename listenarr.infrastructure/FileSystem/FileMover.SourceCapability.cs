using System.ComponentModel;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover : IFilePublicationSourceCapability
{
    public async Task<FilePublicationSourceCapabilityResult> CheckAsync(
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
                return FilePublicationSourceCapabilityResult.Unsupported(
                    "The source path does not identify a file beneath a directory.");
            }

            using var anchor = await OpenSourceProofParentAsync(parent, cancellationToken);
            var openOutcome = anchor.TryOpenExistingFileWithOutcome(
                fileName,
                requireDeleteAccess: false,
                out var openedEntry);
            using var entry = openedEntry;
            if (openOutcome == PinnedFileOpenOutcome.NotFound)
            {
                return FilePublicationSourceCapabilityResult.Unsupported(
                    "The source file does not exist.",
                    FilePublicationSourceCapabilityFailureKind.Missing);
            }
            if (openOutcome == PinnedFileOpenOutcome.Unavailable)
            {
                return FilePublicationSourceCapabilityResult.Unsupported(
                    "The source file is temporarily unavailable.",
                    FilePublicationSourceCapabilityFailureKind.Unavailable);
            }
            if (entry == null || !entry.IsRegularFile())
            {
                return FilePublicationSourceCapabilityResult.Unsupported(
                    "The source path is not a regular file that can be published safely.");
            }
            if (!entry.VisiblePathMatches())
            {
                return FilePublicationSourceCapabilityResult.Unsupported(
                    "The source file changed while its publication capability was being verified.",
                    FilePublicationSourceCapabilityFailureKind.Unavailable);
            }

            // Source capability exposes durable content evidence only. The pinned
            // entry here prevents path substitution while the proof is captured, but
            // its kernel identity is deliberately not exported as restart authority.
            var sourceProof = await CaptureContentOnlySourceProofAsync(
                entry,
                cancellationToken);
            if (!anchor.VisiblePathMatches()
                || !entry.VisiblePathMatches())
            {
                return FilePublicationSourceCapabilityResult.Unsupported(
                    "The source file changed while its content proof was being captured.",
                    FilePublicationSourceCapabilityFailureKind.Unavailable);
            }

            return FilePublicationSourceCapabilityResult.SupportedForProof(
                sourceProof);
        }
        catch (Exception exception) when (
            FileSystemSafety.IsProvenMissingPathException(exception))
        {
            return FilePublicationSourceCapabilityResult.Unsupported(
                "The source file does not exist.",
                FilePublicationSourceCapabilityFailureKind.Missing);
        }
        catch (PlatformNotSupportedException exception)
        {
            return FilePublicationSourceCapabilityResult.Unsupported(exception.Message);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or Win32Exception
                or InvalidOperationException or NotSupportedException
                or PathTooLongException or System.Security.SecurityException)
        {
            return FilePublicationSourceCapabilityResult.Unsupported(
                "The source file cannot be pinned long enough to capture a stable content proof.",
                FilePublicationSourceCapabilityFailureKind.Unavailable);
        }
    }

    private async Task<PinnedDirectoryCreation.PinnedDirectoryAnchor> OpenSourceProofParentAsync(
        string parent, CancellationToken cancellationToken)
    {
        var managed = await ResolveManagedRootPathAsync(parent);
        if (managed.HasUnavailableOverlap)
            throw new InvalidOperationException("The source overlaps an unresolved managed boundary.");
        if (managed.Root == null || managed.Semantics == null)
            return PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(parent, createMissing: false);
        if (_directoryBoundaryAuthorizer == null)
            throw new InvalidOperationException("The configured source boundary cannot be authorized.");
        using var authorization = await _directoryBoundaryAuthorizer.AuthorizeAsync(
            managed.Root.Path, managed.Semantics.Value, cancellationToken);
        var current = authorization.BoundaryAnchor.Duplicate();
        try
        {
            foreach (var segment in Path.GetRelativePath(managed.Root.Path, parent).Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries).Where(segment => segment != "."))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var next = current.OpenExistingChild(segment);
                current.Dispose();
                current = next;
            }
            if (!authorization.BoundaryAnchor.VisiblePathMatches() || !current.VisiblePathMatches())
                throw new IOException("The configured source boundary changed during content capture.");
            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }
}
