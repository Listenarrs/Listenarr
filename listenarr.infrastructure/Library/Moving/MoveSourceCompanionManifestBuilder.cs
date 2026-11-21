using System.Security.Cryptography;
using Listenarr.Application.Common.Exceptions;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal static class MoveSourceCompanionManifestBuilder
{
    public static async Task<IReadOnlyList<MoveSourceManifestEntry>> BuildAsync(
        int audiobookId,
        string? audiobookBasePath,
        string sourceRoot,
        PathIdentitySnapshot sourceIdentity,
        IReadOnlyCollection<string> trackedFilePaths,
        LibraryDirectoryOwnershipBoundaryAuthorizer? ownershipAuthorizer,
        IAudiobookRepository? audiobookRepository,
        IAudiobookFileRepository fileRepository,
        bool includeContentHashes,
        CancellationToken cancellationToken)
    {
        if (ownershipAuthorizer == null
            || audiobookRepository == null
            || string.IsNullOrWhiteSpace(audiobookBasePath)
            || !FileSystemPathIdentity.TryCanonicalizeStoredPathWithIdentityForHost(
                audiobookBasePath,
                sourceIdentity,
                out var canonicalBasePath,
                out _)
            || !FileSystemPathIdentity.AreEquivalent(
                canonicalBasePath,
                sourceRoot,
                sourceIdentity.Semantics))
        {
            return [];
        }

        if (!await AudiobookExclusiveFolderReference.IsExclusiveAsync(
                audiobookId,
                sourceRoot,
                sourceIdentity.Semantics,
                audiobookRepository,
                fileRepository,
                cancellationToken))
        {
            return [];
        }

        var tracked = trackedFilePaths
            .Select(path => FileSystemPathIdentity.Canonicalize(
                path,
                sourceIdentity.Syntax))
            .ToHashSet(sourceIdentity.Semantics.Comparer);

        try
        {
            using var authorization = await ownershipAuthorizer.TryAuthorizeContainingRootAsync(
                sourceRoot,
                sourceIdentity.Semantics,
                cancellationToken);
            if (authorization == null)
            {
                return [];
            }

            var sourceParent = Path.GetDirectoryName(sourceRoot)
                ?? throw Conflict("The audiobook companion source root has no parent directory.");
            if (!FileSystemPathIdentity.AreEquivalent(
                    authorization.ParentAnchor.FullPath,
                    sourceParent,
                    sourceIdentity.Semantics))
            {
                throw Conflict(
                    "The audiobook companion source root could not be pinned beneath its managed library root.");
            }
            EnsureVisible(
                authorization.ParentAnchor,
                "The audiobook companion source parent is temporarily unavailable.",
                "The audiobook companion source parent changed during manifest capture.");

            using var source = authorization.ParentAnchor.OpenExistingChild(
                Path.GetFileName(sourceRoot));
            EnsureVisible(
                source,
                sourceRoot,
                "The audiobook companion source directory is temporarily unavailable.",
                "The audiobook companion source directory changed while its move manifest was being created.");

            var entries = new List<MoveSourceManifestEntry>();
            await CaptureDirectoryAsync(
                source,
                source,
                tracked,
                sourceIdentity.Semantics,
                entries,
                includeContentHashes,
                cancellationToken);
            EnsureVisible(
                source,
                sourceRoot,
                "The audiobook companion source directory is temporarily unavailable after manifest capture.",
                "The audiobook companion source directory changed after its move manifest was created.");

            return entries;
        }
        catch (ApplicationConflictException)
        {
            throw;
        }
        catch (ApplicationUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (
            FileSystemSafety.IsProvenMissingPathException(exception))
        {
            throw Conflict(
                "Audiobook companion files changed while the move manifest was being created.");
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception)
        {
            throw Unavailable(
                "Audiobook companion files are temporarily unavailable while the move manifest is being created.",
                exception);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException
                or NotSupportedException or PathTooLongException)
        {
            throw Conflict(
                $"Audiobook companion files could not be safely included in the move manifest: {exception.Message}");
        }
    }

    private static async Task<bool> CaptureDirectoryAsync(
        PinnedDirectoryCreation.PinnedDirectoryAnchor root,
        PinnedDirectoryCreation.PinnedDirectoryAnchor current,
        IReadOnlySet<string> trackedFilePaths,
        FileSystemPathSemantics semantics,
        ICollection<MoveSourceManifestEntry> entries,
        bool includeContentHashes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureVisible(root, current);
        var beforeNames = EnumerateEntryNames(current, semantics);
        var containsCompanion = false;

        foreach (var entryName in beforeNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryPath = Path.Join(current.FullPath, entryName);
            var attributes = File.GetAttributes(entryPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw Conflict(
                    $"A linked or reparse-point entry exists in the audiobook companion tree: {entryName}");
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                using var child = current.OpenExistingChild(entryName);
                var childContainsCompanion = await CaptureDirectoryAsync(
                    root,
                    child,
                    trackedFilePaths,
                    semantics,
                    entries,
                    includeContentHashes,
                    cancellationToken);
                if (childContainsCompanion)
                {
                    entries.Add(new MoveSourceManifestEntry(
                        GetRelativePath(root, child.FullPath, semantics),
                        MoveJobEntryType.Directory,
                        0,
                        Directory.GetLastWriteTimeUtc(child.FullPath),
                        null));
                    containsCompanion = true;
                }

                continue;
            }

            var canonicalFilePath = FileSystemPathIdentity.Canonicalize(
                entryPath,
                semantics.Syntax);
            if (trackedFilePaths.Contains(canonicalFilePath)
                || FileUtils.IsAudioFile(canonicalFilePath))
            {
                continue;
            }

            entries.Add(await CaptureFileAsync(
                root,
                current,
                entryName,
                semantics,
                includeContentHashes,
                cancellationToken));
            containsCompanion = true;
        }

        EnsureVisible(root, current);
        var afterNames = EnumerateEntryNames(current, semantics);
        if (!beforeNames.SequenceEqual(afterNames, semantics.Comparer))
        {
            throw Conflict(
                "The audiobook companion directory changed while its move manifest was being created.");
        }

        return containsCompanion;
    }

    private static async Task<MoveSourceManifestEntry> CaptureFileAsync(
        PinnedDirectoryCreation.PinnedDirectoryAnchor root,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent,
        string fileName,
        FileSystemPathSemantics semantics,
        bool includeContentHash,
        CancellationToken cancellationToken)
    {
        using var file = parent.OpenExistingFileForStableRead(fileName);
        if (!file.IsRegularFile())
        {
            throw Conflict(
                $"Audiobook companion entry is not a regular file: {fileName}");
        }

        var physicalObjectIdentity = file.GetObjectIdentity();
        await using var stream = file.OpenReadStream(
            bufferSize: 128 * 1024,
            asynchronous: false);
        var length = stream.Length;
        var lastWriteTimeUtc = file.GetLastWriteTimeUtc();
        var hash = includeContentHash
            ? Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken))
            : null;
        EnsureVisible(
            root,
            "The audiobook companion root is temporarily unavailable during file capture.",
            "The audiobook companion root changed during file capture.");
        EnsureVisible(
            parent,
            "The audiobook companion parent is temporarily unavailable during file capture.",
            "The audiobook companion parent changed during file capture.");
        var fileVisibility = file.ProbeVisiblePathMatch();
        if (fileVisibility == RegistrationPublicationMatchOutcome.Unavailable)
        {
            throw new IOException(
                $"Audiobook companion file is temporarily unavailable: {fileName}");
        }
        if (fileVisibility != RegistrationPublicationMatchOutcome.Match
            || !file.MatchesObjectIdentity(physicalObjectIdentity)
            || stream.Length != length
            || file.GetLastWriteTimeUtc() != lastWriteTimeUtc)
        {
            throw Conflict(
                $"Audiobook companion file changed while its move manifest was being created: {fileName}");
        }

        return new MoveSourceManifestEntry(
            GetRelativePath(root, file.FullPath, semantics),
            MoveJobEntryType.File,
            length,
            lastWriteTimeUtc,
            hash);
    }

    private static string[] EnumerateEntryNames(
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory,
        FileSystemPathSemantics semantics)
    {
        var names = Directory.EnumerateFileSystemEntries(directory.FullPath)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .OrderBy(name => name, semantics.Comparer)
            .ToArray();
        EnsureVisible(
            directory,
            "The audiobook companion directory is temporarily unavailable during enumeration.",
            "The audiobook companion directory changed during enumeration.");

        return names;
    }

    private static string GetRelativePath(
        PinnedDirectoryCreation.PinnedDirectoryAnchor root,
        string path,
        FileSystemPathSemantics semantics)
    {
        if (!FileSystemPathIdentity.TryGetRelativePathWithinBase(
                root.FullPath,
                path,
                semantics,
                out var relativePath)
            || string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath))
        {
            throw Conflict(
                "An audiobook companion manifest entry escaped the tracked source root.");
        }

        return relativePath;
    }

    private static void EnsureVisible(
        PinnedDirectoryCreation.PinnedDirectoryAnchor root,
        PinnedDirectoryCreation.PinnedDirectoryAnchor current)
    {
        EnsureVisible(
            root,
            "The audiobook companion root is temporarily unavailable during manifest capture.",
            "The audiobook companion root changed during manifest capture.");
        EnsureVisible(
            current,
            "The audiobook companion directory is temporarily unavailable during manifest capture.",
            "The audiobook companion directory generation changed during manifest capture.");
    }

    private static void EnsureVisible(
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory,
        string unavailableMessage,
        string mismatchMessage)
    {
        var visibility = directory.ProbeVisiblePathMatch();
        if (visibility == RegistrationPublicationMatchOutcome.Unavailable)
        {
            throw new IOException(unavailableMessage);
        }
        if (visibility != RegistrationPublicationMatchOutcome.Match)
        {
            throw Conflict(mismatchMessage);
        }
    }

    private static void EnsureVisible(
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory,
        string visiblePath,
        string unavailableMessage,
        string mismatchMessage)
    {
        var visibility = directory.ProbeVisiblePathMatch(visiblePath);
        if (visibility == RegistrationPublicationMatchOutcome.Unavailable)
        {
            throw new IOException(unavailableMessage);
        }
        if (visibility != RegistrationPublicationMatchOutcome.Match)
        {
            throw Conflict(mismatchMessage);
        }
    }

    private static ApplicationConflictException Conflict(string message) =>
        new("move_source_unverified", message);

    private static ApplicationUnavailableException Unavailable(
        string message,
        Exception? innerException = null) =>
        new("move_source_temporarily_unavailable", message, innerException);
}
