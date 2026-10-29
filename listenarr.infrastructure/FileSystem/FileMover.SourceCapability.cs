using System.ComponentModel;
using Listenarr.Domain.Common;
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
            // Seven exception types reach here and used to leave through one sentence that named
            // none of them. A locked file, a permissions problem and an unreadable mount were
            // indistinguishable afterwards, and this is the gate that refuses the import, so the
            // one line an operator gets is the only thing they have to go on.
            var reason = ComposeUnsupportedReason(exception, await FindUnpinnableLinkedAncestorAsync(sourcePath));

            _logger.LogWarning(
                exception,
                "Source publication capability unavailable for {Source}: {Detail} (native error {NativeError})",
                LogRedaction.SanitizeText(sourcePath),
                reason,
                // Nullable on purpose. Zero is a real errno meaning success, so reporting it for
                // the six exception types that carry no native code would be a false reading.
                (exception as Win32Exception)?.NativeErrorCode);

            return FilePublicationSourceCapabilityResult.Unsupported(
                reason,
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
            if (!FileSystemPathIdentity.TryGetRelativePathWithinBase(
                    managed.Root.Path, parent, managed.Semantics.Value, out var relative))
                throw new InvalidOperationException("The source parent escaped its configured boundary.");
            foreach (var segment in relative.Split(
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

    /// <summary>
    /// The refusal an operator reads, cause first.
    /// </summary>
    /// <remarks>
    /// Every consumer of <c>Reason</c> renders it through <c>LogRedaction.SanitizeText</c>, whose
    /// 200-character default used to cut the exception off the end and leave behind only the fixed
    /// sentence the operator already knew. Leading with the cause means the half that survives
    /// truncation is the half that says what went wrong.
    ///
    /// The cause is formatted by <c>ExceptionCause</c> rather than here, because the import result
    /// that persists a failure into History has to say the same thing this does. It also gives
    /// this gate the inner chain it had no way to reach while it formatted the outer exception
    /// on its own.
    ///
    /// Both interpolated values are attacker-influenced: a download client writes the file name,
    /// and the file name is what most of these exception messages quote. A newline in either would
    /// let a crafted name forge a second log record, so they go through the same SanitizeText
    /// every other call site in this directory uses.
    /// </remarks>
    internal static string ComposeUnsupportedReason(Exception exception, string? linkedAncestor)
    {
        var cause = LogRedaction.SanitizeText(ExceptionCause.Describe(exception));

        return linkedAncestor == null
            ? $"{cause} The source file could not be pinned long enough to capture a stable content proof."
            : $"{cause} The source file could not be pinned. It may be reached through a symbolic link at "
              + $"'{LogRedaction.SanitizeText(linkedAncestor)}', which cannot be pinned; if so, configure the real path instead.";
    }

    /// <summary>
    /// The first directory the pinned walk would meet that is a symbolic link it refuses, which is
    /// the outermost one on the path, or null if there is none.
    /// </summary>
    /// <remarks>
    /// Refusing a linked ancestor is deliberate and covered by
    /// CheckPublicationSource_LinkedAncestor_ReturnsUnsupported, so this does not change the
    /// answer. It only says which segment may have caused it, because the raw failure is an ENOTDIR
    /// from openat and gives an operator nothing to act on. The walk opens segments from the top
    /// down and stops at the first link, so with nested links the outer one is named, not the
    /// deeper one. The hint is a candidate rather than a diagnosis: a refusal that happens before
    /// the walk, such as a root that cannot be authorized, can still leave a link on the path.
    ///
    /// Beneath a configured root only the segments below the root are opened without following
    /// links. The root itself is opened by the boundary authorizer, which follows its link, so a
    /// link at or above the root is supported and blaming it would send the operator after the
    /// wrong thing. The walk stops at the root for that reason, and covers the whole path only for
    /// a source outside every configured root.
    ///
    /// A source that overlaps a root whose identity is unavailable is refused before any directory
    /// is opened, so no link on its path can be the cause and there is nothing to name.
    /// </remarks>
    private async Task<string?> FindUnpinnableLinkedAncestorAsync(string sourcePath)
    {
        try
        {
            var current = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
            if (string.IsNullOrEmpty(current))
            {
                return null;
            }

            var managed = await ResolveManagedRootPathAsync(current);
            if (managed.HasUnavailableOverlap)
            {
                return null;
            }

            string? outermostLink = null;
            while (!string.IsNullOrEmpty(current)
                && (managed.Root == null || managed.Semantics == null
                    || IsStrictlyBelowRoot(current, managed.Root.Path, managed.Semantics.Value)))
            {
                if (Directory.Exists(current)
                    && Directory.ResolveLinkTarget(current, returnFinalTarget: false) != null)
                {
                    outermostLink = current;
                }
                current = Path.GetDirectoryName(current);
            }

            return outermostLink;
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException
                or StackOverflowException))
        {
            // Best effort only. The caller still reports the original failure, and a diagnostic
            // that cannot be computed must not replace it.
        }

        return null;
    }

    private static bool IsStrictlyBelowRoot(
        string path,
        string rootPath,
        FileSystemPathSemantics semantics)
    {
        return FileSystemPathIdentity.TryGetRelativePathWithinBase(
                rootPath, path, semantics, out var relative)
            && relative.Length > 0;
    }
}
