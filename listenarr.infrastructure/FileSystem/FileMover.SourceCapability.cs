using System.ComponentModel;
using Microsoft.Extensions.Logging;

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

            using var anchor = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                parent,
                createMissing: false);
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

            FilePublicationSourceProof sourceProof;
            try
            {
                if (ForceContentOnlySourceProofForTest)
                {
                    throw new PlatformNotSupportedException(
                        "Durable source identity was disabled for this test.");
                }
                var proof = await CaptureMarkerlessSourceProofAsync(
                    entry,
                    cancellationToken,
                    includeSha256: true);
                sourceProof = new FilePublicationSourceProof(
                    proof.PhysicalObjectIdentity,
                    proof.Length,
                    proof.Sha256!);
            }
            catch (Exception exception) when (exception is
                PlatformNotSupportedException or NotSupportedException)
            {
                sourceProof = await CaptureContentOnlySourceProofAsync(
                    entry,
                    cancellationToken);
            }
            if (!anchor.VisiblePathMatches()
                || !entry.VisiblePathMatches())
            {
                return FilePublicationSourceCapabilityResult.Unsupported(
                    "The source file changed while its durable identity was being verified.",
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
            // Seven exception types reach here and used to leave through one sentence that named
            // none of them. A locked file, a permissions problem and an unreadable mount were
            // indistinguishable afterwards, and this is the gate that refuses the import, so the
            // one line an operator gets is the only thing they have to go on.
            // Both interpolated values are attacker-influenced: a download client writes the
            // file name, and the file name is what most of these exception messages quote. A
            // newline in either would let a crafted name forge a second log record, so they go
            // through the same SanitizeText every other call site in this directory uses.
            var linkedAncestor = FindSymlinkedAncestor(sourcePath);
            var cause = $"{exception.GetType().Name}: {LogRedaction.SanitizeText(exception.Message)}";
            var detail = linkedAncestor == null
                ? cause
                : $"the path is reached through a symbolic link at '{LogRedaction.SanitizeText(linkedAncestor)}', "
                  + $"which cannot be pinned; configure the real path instead ({cause})";

            _logger.LogWarning(
                exception,
                "Source publication capability unavailable for {Source}: {Detail} (native error {NativeError})",
                LogRedaction.SanitizeText(sourcePath),
                detail,
                // Nullable on purpose. Zero is a real errno meaning success, so reporting it for
                // the six exception types that carry no native code would be a false reading.
                (exception as Win32Exception)?.NativeErrorCode);

            return FilePublicationSourceCapabilityResult.Unsupported(
                $"The source file cannot be pinned to a durable physical generation and content proof: {detail}",
                FilePublicationSourceCapabilityFailureKind.Unavailable);
        }
    }

    /// <summary>
    /// The first directory in the path that is a symbolic link, or null if there is none.
    /// </summary>
    /// <remarks>
    /// Refusing a linked ancestor is deliberate and covered by
    /// CheckPublicationSource_LinkedAncestor_ReturnsUnsupported, so this does not change the
    /// answer. It only says which segment caused it, because the raw failure is an ENOTDIR from
    /// openat and gives an operator nothing to act on.
    /// </remarks>
    private static string? FindSymlinkedAncestor(string sourcePath)
    {
        try
        {
            var current = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
            while (!string.IsNullOrEmpty(current))
            {
                if (Directory.Exists(current)
                    && Directory.ResolveLinkTarget(current, returnFinalTarget: false) != null)
                {
                    return current;
                }
                current = Path.GetDirectoryName(current);
            }
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or NotSupportedException
                or System.Security.SecurityException)
        {
            // Best effort only. The caller still reports the original failure.
        }

        return null;
    }
}
