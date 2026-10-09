using System.Security.Cryptography;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class AudiobookFilesystemDeleteService
{
    // Preflight retains observations and content, never a handle per tree entry.
    // Only the current traversal path and current mutation remain pinned.
    private sealed record DeleteTreeEntryProof(
        string? DirectoryIdentity, DeleteFileContentProof? Content) : IDisposable
    {
        public static DeleteTreeEntryProof CaptureDirectory(PinnedDirectoryCreation.PinnedDirectoryAnchor directory) =>
            new(PinnedDirectoryCreation.CaptureDiagnosticIdentity(directory.GetDirectoryObjectIdentity), null);

        public static DeleteTreeEntryProof CaptureFile(PinnedDirectoryCreation.PinnedFileEntry file,
            DeleteFileContentProof? tracked)
        {
            if (tracked.HasValue) return new(null, tracked);
            var observation = FilePublicationObservation.Capture(file);
            using var stream = file.OpenReadStream(128 * 1024, asynchronous: false);
            var length = stream.Length;
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            return new(null, new DeleteFileContentProof(length, hash, observation));
        }

        public bool Matches(PinnedDirectoryCreation.PinnedDirectoryAnchor current) =>
            DirectoryIdentity != null && current.VisiblePathMatches()
            && (DirectoryIdentity.Length == 0 || current.MatchesDirectoryObjectIdentity(DirectoryIdentity));

        public bool Matches(PinnedDirectoryCreation.PinnedFileEntry current) =>
            Content.HasValue && PinnedFileMatchesContentProof(current, Content.Value);

        public void Dispose() { }
    }

    private sealed class CapturedDeleteTreeProofs(IEqualityComparer<string> comparer)
        : Dictionary<string, DeleteTreeEntryProof>(comparer), IDisposable
    {
        public void Capture(string relativePath, DeleteTreeEntryProof proof)
        {
            if (!TryAdd(relativePath, proof))
                throw new InvalidOperationException("Recursive-delete preflight contains ambiguous entry paths.");
        }

        public void Dispose() => Clear();
    }

    private readonly record struct DeleteFileContentProof(
        long Length, string Sha256, FilePublicationObservation Observation);

    private sealed class CapturedDeleteContentProofs(IEqualityComparer<string> comparer)
        : Dictionary<string, DeleteFileContentProof>(comparer), IDisposable
    {
        public void Dispose() => Clear();
    }

    private async Task<PinnedDirectoryCreation.PinnedDirectoryAnchor> OpenPinnedDeleteFileParentAsync(
        string filePath,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken)
    {
        var roots = await _rootFolderService.GetAllAsync();
        var isManaged = roots.Any(root =>
            FileSystemPathIdentity.StoredBoundaryMayContainPath(
                root.Path, filePath, semantics.Syntax, root.CaseSensitivityMode)
            || FileSystemPathIdentity.AmbiguousStoredBoundaryMayContainPath(
                root.Path, filePath, semantics.Syntax, root.CaseSensitivityMode));
        if (isManaged)
        {
            if (_ownershipAuthorizer == null)
                throw new InvalidOperationException("Managed-root authorization is unavailable for tracked-file deletion.");
            // The authorizer validates the deepest current configured boundary and
            // walks descendants without following links. Failure never falls back.
            using var authorization = await _ownershipAuthorizer.AuthorizeContainingRootAsync(
                filePath, semantics, cancellationToken);
            return authorization.ParentAnchor.Duplicate();
        }
        return PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
            Path.GetDirectoryName(filePath)
                ?? throw new InvalidOperationException("A tracked deletion path has no parent."),
            createMissing: false);
    }

    private async Task<CapturedDeleteContentProofs?>
        CaptureTrackedContentProofsAsync(
            IReadOnlyCollection<string> trackedFilePaths,
            FileSystemPathSemantics semantics,
            AudiobookFilesystemDeleteResult result,
            CancellationToken cancellationToken)
    {
        var proofs = new CapturedDeleteContentProofs(semantics.Comparer);
        var transferred = false;
        try
        {
            foreach (var trackedFilePath in trackedFilePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parentPath = Path.GetDirectoryName(trackedFilePath);
                var fileName = Path.GetFileName(trackedFilePath);
                if (string.IsNullOrWhiteSpace(parentPath)
                    || string.IsNullOrWhiteSpace(fileName))
                {
                    result.Warnings.Add(
                        "A tracked audiobook file could not be pinned for safe deletion.");
                    return null;
                }

                try
                {
                    using var parent =
                        await OpenPinnedDeleteFileParentAsync(
                            trackedFilePath, semantics, cancellationToken);
                    var outcome =
                        parent.TryOpenExistingFileForStableDeleteWithOutcome(
                            fileName,
                            out var openedEntry);
                    using var entry = openedEntry;
                    if (outcome == PinnedFileOpenOutcome.NotFound)
                    {
                        if (!parent.VisiblePathMatches())
                        {
                            result.Warnings.Add(
                                "A tracked audiobook file parent changed while absence was being proved.");
                            return null;
                        }

                        continue;
                    }

                    if (outcome != PinnedFileOpenOutcome.Opened || entry == null
                        || !entry.IsRegularFile()
                        || !parent.VisiblePathMatches()
                        || !entry.VisiblePathMatches())
                    {
                        result.Warnings.Add(
                            "A tracked audiobook file could not be pinned to a stable live object for deletion.");
                        return null;
                    }

                    await using var stream = entry.OpenReadStream(
                        bufferSize: 128 * 1024,
                        asynchronous: false);
                    var length = stream.Length;
                    var hash = Convert.ToHexString(
                        await SHA256.HashDataAsync(stream, cancellationToken));
                    if (!parent.VisiblePathMatches()
                        || !entry.VisiblePathMatches())
                    {
                        result.Warnings.Add(
                            "A tracked audiobook file changed while its live content proof was being captured.");
                        return null;
                    }

                    proofs[trackedFilePath] = new DeleteFileContentProof(
                        length,
                        hash,
                        FilePublicationObservation.Capture(entry));
                }
                catch (Exception exception) when (
                    FileSystemSafety.IsProvenMissingPathException(exception))
                {
                    continue;
                }
                catch (Exception exception) when (exception is
                    IOException or UnauthorizedAccessException
                        or InvalidOperationException or NotSupportedException
                        or System.ComponentModel.Win32Exception
                        or System.Security.SecurityException)
                {
                    result.Warnings.Add(
                        "A tracked audiobook file is temporarily unavailable for safe deletion.");
                    _logger.LogWarning(
                        exception,
                        "Could not capture live deletion proof for {Path}",
                        LogRedaction.SanitizeFilePath(trackedFilePath));
                    return null;
                }
            }

            transferred = true;
            return proofs;
        }
        finally
        {
            if (!transferred)
            {
                proofs.Dispose();
            }
        }
    }
}
