using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    internal async Task<IReadOnlyDictionary<string, string>>
        CapturePublishedTargetPhysicalIdentitiesAsync(
            Guid jobId,
            string target,
            FileSystemPathSemantics targetSemantics,
            CancellationToken cancellationToken)
    {
        var manifest = await LoadManifestAsync(jobId, cancellationToken);
        if (manifest.Count == 0)
        {
            throw new MoveNeedsAttentionException(
                "The published target physical identities cannot be captured without a persisted move manifest.");
        }

        var authorization = await GetBoundaryAuthorizationsAsync(
            jobId,
            cancellationToken);
        return await CapturePublishedTargetPhysicalIdentitiesAsync(
            target,
            manifest,
            targetSemantics,
            authorization,
            cancellationToken);
    }

    private static IReadOnlyDictionary<string, string>
        CreatePersistedTargetPhysicalIdentityMap(
            string target,
            IEnumerable<MoveJobEntry> manifest,
            FileSystemPathSemantics targetSemantics)
    {
        // Kept as a compatibility-shaped result for the existing path-rewrite
        // API. Path rewrite deliberately ignores physical identities and clears
        // any prior persisted observation. Durable target evidence lives in the
        // manifest's path/length/SHA-256 fields.
        _ = target;
        _ = manifest;
        return new Dictionary<string, string>(targetSemantics.Comparer);
    }

    private static async Task<IReadOnlyDictionary<string, string>>
        CapturePublishedTargetPhysicalIdentitiesAsync(
            string target,
            IReadOnlyCollection<MoveJobEntry> manifest,
            FileSystemPathSemantics targetSemantics,
            MarkerlessMoveBoundaryAuthorizationState authorization,
            CancellationToken cancellationToken)
    {
        var identities = new Dictionary<string, string>(targetSemantics.Comparer);
        foreach (var entry in manifest.Where(candidate =>
            candidate.EntryType == MoveJobEntryType.File))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directorySegments, fileName) = SplitPinnedRelativeFilePath(
                entry.RelativePath,
                targetSemantics);
            using var targetPath = PinnedMoveDirectoryPath.OpenExisting(
                authorization.TargetBoundaryPath,
                target,
                targetSemantics,
                directorySegments);
            using var targetEntry = targetPath.Current.OpenExistingFile(
                fileName,
                requireDeleteAccess: false);
            targetPath.EnsureVisibleHierarchy();
            if (!PinnedFileVisibleOrThrowUnavailable(
                    targetEntry,
                    $"The published target generation is temporarily unavailable before identity capture: {entry.RelativePath}")
                || !await targetEntry.MatchesAsync(
                    entry.Length,
                    entry.Sha256,
                    cancellationToken))
            {
                throw new MoveNeedsAttentionException(
                    $"The published target generation changed before identity capture: {entry.RelativePath}");
            }

            var objectIdentity = PinnedDirectoryCreation.CaptureDiagnosticIdentity(targetEntry.GetObjectIdentity);
            targetPath.EnsureVisibleHierarchy();
            if (!PinnedFileVisibleOrThrowUnavailable(
                    targetEntry,
                    $"The published target path is temporarily unavailable during identity capture: {entry.RelativePath}"))
            {
                throw new MoveNeedsAttentionException(
                    $"The published target path changed during identity capture: {entry.RelativePath}");
            }

            if (!FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                    target,
                    entry.RelativePath,
                    targetSemantics,
                    out var targetFilePath))
            {
                throw new MoveNeedsAttentionException(
                    $"The published target identity escaped its root: {entry.RelativePath}");
            }

            if (string.IsNullOrWhiteSpace(objectIdentity)) continue;
            identities.Add(
                FileSystemPathIdentity.Canonicalize(
                    targetFilePath,
                    targetSemantics.Syntax),
                objectIdentity);
        }

        return identities;
    }

    private static (IReadOnlyList<string> DirectorySegments, string FileName)
        SplitPinnedRelativeFilePath(
            string relativePath,
            FileSystemPathSemantics semantics)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new MoveNeedsAttentionException(
                "A file manifest entry has no relative path.");
        }

        var separators = semantics.Syntax == FileSystemPathSyntax.Windows
            ? new[] { '\\', '/' }
            : new[] { '/' };
        var lastSeparator = relativePath.LastIndexOfAny(separators);
        var fileName = lastSeparator < 0
            ? relativePath
            : relativePath[(lastSeparator + 1)..];
        var directoryPart = lastSeparator < 0
            ? string.Empty
            : relativePath[..lastSeparator];
        var segments = directoryPart.Split(
            separators,
            StringSplitOptions.RemoveEmptyEntries);
        if (string.IsNullOrWhiteSpace(fileName)
            || segments.Any(segment => segment is "." or ".."))
        {
            throw new MoveNeedsAttentionException(
                "A file manifest entry contains an invalid path segment.");
        }

        return (segments, fileName);
    }
}
