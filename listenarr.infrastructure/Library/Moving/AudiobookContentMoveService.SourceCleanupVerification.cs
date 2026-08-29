using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    internal static bool CanAttemptFinalizedMoveVerification(
        string sourcePath,
        string targetPath,
        FileSystemPathSemantics semantics)
    {
        var source = Path.GetFullPath(sourcePath);
        if (!Directory.Exists(source))
        {
            return true;
        }

        return FileSystemSafety.TryEnumerateTreeWithoutLinks(
            source,
            out _,
            out _,
            out _);
    }

    private static void VerifySourceCleanupState(
        AudiobookContentMoveRequest request,
        string sourcePath,
        string targetPath,
        IReadOnlyCollection<MoveJobEntry> manifest)
    {
        if (request.ForceCopyAndRetainSource
            && manifest
                .Where(IsPhysicalManifestEntry)
                .Any(entry => IsDestructiveCleanupState(entry.CleanupState)))
        {
            throw new MoveNeedsAttentionException(
                "Forced source retention has contradictory destructive cleanup evidence.");
        }

        var source = Path.GetFullPath(sourcePath);
        if (!AuthorizedSourceDirectoryExists(request, source))
        {
            return;
        }

        ValidateExistingMoveDirectory(source, "source cleanup verification directory");
        if (!FileSystemSafety.TryEnumerateTreeWithoutLinks(
                source,
                out var remainingFiles,
                out var remainingDirectories,
                out var reason))
        {
            if (!AuthorizedSourceDirectoryExists(request, source))
            {
                return;
            }
            if (reason.Contains("link", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("reparse", StringComparison.OrdinalIgnoreCase))
            {
                throw new MoveNeedsAttentionException(
                    $"The completed move source could not be verified safely: {reason}");
            }

            throw new IOException(
                $"The completed move source could not be enumerated safely: {reason}");
        }

        foreach (var entry in manifest.Where(entry => !IsRootManifestEntry(entry)))
        {
            if (!FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                    source,
                    entry.RelativePath,
                    request.SourceSemantics,
                    out var sourceEntry))
            {
                throw new MoveNeedsAttentionException(
                    "A persisted source manifest entry escaped its authorized source root during cleanup verification.");
            }

            if (entry.EntryType == MoveJobEntryType.File)
            {
                if (entry.CleanupState == MoveJobEntryCleanupState.Retained)
                {
                    if (!TryGetExistingPathAttributes(sourceEntry, out var retainedAttributes)
                        || (retainedAttributes & FileAttributes.Directory) != 0
                        || (retainedAttributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new MoveNeedsAttentionException(
                            $"A retained source file is missing or changed type: {entry.RelativePath}");
                    }
                    continue;
                }
                if (TryGetExistingPathAttributes(sourceEntry, out _))
                {
                    throw new MoveNeedsAttentionException(
                        $"The completed move source contains a recreated or uncleared owned file path: {entry.RelativePath}");
                }
                continue;
            }

            if (entry.EntryType != MoveJobEntryType.Directory)
            {
                throw new MoveNeedsAttentionException(
                    "The persisted source manifest contains an unsupported entry type.");
            }

            if (entry.CleanupState == MoveJobEntryCleanupState.Retained)
            {
                if (!TryGetExistingPathAttributes(sourceEntry, out var retainedAttributes)
                    || (retainedAttributes & FileAttributes.Directory) == 0
                    || (retainedAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new MoveNeedsAttentionException(
                        $"A retained source directory is missing or changed type: {entry.RelativePath}");
                }
                continue;
            }

            if (TryGetExistingPathAttributes(sourceEntry, out var attributes))
            {
                if ((attributes & FileAttributes.Directory) == 0)
                {
                    throw new MoveNeedsAttentionException(
                        $"The completed move source directory changed into a file: {entry.RelativePath}");
                }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new MoveNeedsAttentionException(
                        $"The completed move source directory changed into a link or reparse point: {entry.RelativePath}");
                }
                if (!Directory.EnumerateFileSystemEntries(sourceEntry).Any())
                {
                    throw new MoveNeedsAttentionException(
                        $"The completed move source contains an uncleared empty owned directory: {entry.RelativePath}");
                }
            }
        }

        var target = Path.GetFullPath(targetPath);
        var targetInsideSource = IsSameOrInside(
            target,
            source,
            request.SourceSemantics);
        var ordinaryRemainingEntries = remainingFiles
            .Concat(remainingDirectories)
            .Where(entry => !targetInsideSource
                || (!IsSameOrInside(entry, target, request.SourceSemantics)
                    && !IsSameOrInside(target, entry, request.SourceSemantics)))
            .ToList();

        if (ordinaryRemainingEntries.Count == 0
            && request.DeleteEmptySource
            && !targetInsideSource
            && !IsSourceCleanupBoundary(
                source,
                request.SourceCleanupBoundary,
                request.SourceSemantics))
        {
            throw new MoveNeedsAttentionException(
                "The completed move source directory was recreated after cleanup.");
        }
    }

    private static bool AuthorizedSourceDirectoryExists(
        AudiobookContentMoveRequest request,
        string source)
    {
        var authorization = request.BoundaryAuthorization
            ?? throw new MoveNeedsAttentionException(
                "The move lacks loaded source-boundary authorization during cleanup verification.");
        var boundary = authorization.SourceBoundaryPath;
        if (!FileSystemPathIdentity.TryGetRelativePathWithinBase(
                boundary,
                source,
                request.SourceSemantics,
                out var relativePath))
        {
            throw new MoveNeedsAttentionException(
                "The source cleanup verification path escaped its authorized boundary.");
        }

        var current = PinnedDirectoryCreation.OpenPinnedBoundary(boundary);
        try
        {
            if (!current.MatchesManagedDirectoryIdentity(
                    authorization.SourceDirectoryObjectIdentityVersion,
                    authorization.SourceDirectoryObjectIdentity)
                || !PinnedDirectoryVisibleOrThrowUnavailable(
                    current,
                    "The source boundary is temporarily unavailable during cleanup verification."))
            {
                throw new MoveNeedsAttentionException(
                    "The source boundary changed physical generation during cleanup verification.");
            }

            foreach (var segment in SplitMovePathSegments(relativePath, request.SourceSemantics))
            {
                PinnedDirectoryCreation.PinnedDirectoryAnchor next;
                try
                {
                    next = current.OpenExistingChild(segment);
                }
                catch (System.ComponentModel.Win32Exception exception) when (
                    IsMissingDirectoryComponent(exception))
                {
                    return false;
                }
                catch (Exception exception) when (exception is
                    InvalidOperationException or NotSupportedException)
                {
                    throw new MoveNeedsAttentionException(
                        $"A source cleanup path component changed type or physical generation: {exception.Message}");
                }

                current.Dispose();
                current = next;
            }

            if (!PinnedDirectoryVisibleOrThrowUnavailable(
                    current,
                    "The source cleanup path is temporarily unavailable during final verification."))
            {
                throw new MoveNeedsAttentionException(
                    "The source cleanup path changed physical generation during final verification.");
            }

            return true;
        }
        finally
        {
            current.Dispose();
        }
    }

    private static bool IsMissingDirectoryComponent(
        System.ComponentModel.Win32Exception exception) =>
        OperatingSystem.IsWindows()
            ? exception.NativeErrorCode is 2 or 3
            : exception.NativeErrorCode == 2;

    private static bool TryGetExistingPathAttributes(
        string path,
        out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (exception is
            FileNotFoundException or DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }
}
