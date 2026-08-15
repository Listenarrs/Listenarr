using System.Security.Cryptography;
using Listenarr.Application.Common.Exceptions;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class MoveSourceManifestService(
    IAudiobookFileRepository fileRepository,
    LibraryDirectoryOwnershipBoundaryAuthorizer? ownershipAuthorizer = null,
    IAudiobookRepository? audiobookRepository = null)
    : IMoveSourceManifestService, IMoveSourcePlanService
{
    private async Task<MoveSourceManifest> BuildCoreAsync(
        int audiobookId,
        string? audiobookBasePath,
        bool includeContentHashes,
        CancellationToken cancellationToken)
    {
        var trackedFiles = await fileRepository.GetByAudiobookIdAsync(
            audiobookId,
            cancellationToken);
        if (trackedFiles.Count == 0)
        {
            throw Conflict(
                "The audiobook has no validated tracked files. Rescan or repair it before moving files.");
        }

        var validated = new List<ValidatedTrackedFile>(trackedFiles.Count);
        PathIdentitySnapshot? sharedIdentity = null;
        foreach (var trackedFile in trackedFiles.OrderBy(file => file.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = GetRequiredIdentity(trackedFile);
            var path = ResolveRequiredCanonicalPath(trackedFile, identity);
            if (sharedIdentity.HasValue
                && !HasSameFilesystemAuthority(sharedIdentity.Value, identity))
            {
                throw Conflict(
                    "Tracked audiobook files span incompatible filesystem identities and cannot be moved safely.");
            }

            sharedIdentity ??= identity;
            validated.Add(await ValidateFileAsync(
                trackedFile.Id,
                path,
                trackedFile.PhysicalObjectIdentity,
                includeContentHashes,
                cancellationToken));
        }

        var identitySnapshot = sharedIdentity!.Value;
        var sourceRoot = CalculateSourceRoot(validated, identitySnapshot.Semantics);
        if (!FileSystemPathIdentity.IsSameOrInside(
                sourceRoot,
                identitySnapshot.BoundaryPath,
                identitySnapshot.Semantics))
        {
            throw Conflict(
                "The tracked audiobook source root escaped its persisted filesystem boundary.");
        }

        if (IsFilesystemRoot(sourceRoot, identitySnapshot.Semantics))
        {
            throw Conflict(
                "Tracked audiobook files resolve to a filesystem root, which cannot be used as a move source.");
        }

        ValidateSourceRoot(sourceRoot);
        ValidateSourceRootShape(
            sourceRoot,
            validated,
            identitySnapshot.Semantics);
        var entries = BuildEntries(
            sourceRoot,
            validated,
            identitySnapshot.Semantics);
        var companionEntries = await MoveSourceCompanionManifestBuilder.BuildAsync(
            audiobookId,
            audiobookBasePath,
            sourceRoot,
            identitySnapshot,
            validated.Select(file => file.Path).ToList(),
            ownershipAuthorizer,
            audiobookRepository,
            fileRepository,
            includeContentHashes,
            cancellationToken);
        entries = MergeEntries(
            entries,
            companionEntries,
            identitySnapshot.Semantics);
        var sourceIdentity = new PathIdentitySnapshot(
            identitySnapshot.Syntax,
            identitySnapshot.CaseSensitivity,
            identitySnapshot.RequestedMode,
            identitySnapshot.BoundaryPath);
        sourceIdentity.ValidateForPath(sourceRoot);
        return new MoveSourceManifest(
            sourceRoot,
            sourceIdentity,
            entries,
            validated.Select(file => file.AudiobookFileId).ToList());
    }

    private static PathIdentitySnapshot GetRequiredIdentity(AudiobookFile file)
    {
        if (file.PathIdentityState != PathIdentityState.Valid
            || !file.PathSyntax.HasValue
            || file.PathCaseSensitivity == FileSystemCaseSensitivity.Unknown
            || string.IsNullOrWhiteSpace(file.PathIdentityBoundary)
            || string.IsNullOrWhiteSpace(file.CanonicalPath))
        {
            throw Conflict(
                $"Tracked file {file.Id} has unresolved path identity and must be repaired before moving.");
        }

        return new PathIdentitySnapshot(
            file.PathSyntax.Value,
            file.PathCaseSensitivity,
            file.PathCaseSensitivityMode,
            file.PathIdentityBoundary);
    }

    private static string ResolveRequiredCanonicalPath(
        AudiobookFile file,
        PathIdentitySnapshot identity)
    {
        if (!FileSystemPathIdentity.TryCanonicalizeStoredPathWithIdentityForHost(
                file.CanonicalPath!,
                identity,
                out var canonical,
                out var reason))
        {
            throw Conflict(
                $"Tracked file {file.Id} cannot be authorized on the current host: {reason}");
        }

        return canonical;
    }

    private static async Task<ValidatedTrackedFile> ValidateFileAsync(
        int audiobookFileId,
        string path,
        string? expectedPhysicalObjectIdentity,
        bool includeContentHash,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw Conflict(
                $"Tracked file {audiobookFileId} is missing from disk and cannot be moved safely.");
        }

        if (string.IsNullOrWhiteSpace(expectedPhysicalObjectIdentity))
        {
            throw Conflict(
                $"Tracked file {audiobookFileId} has unresolved physical identity and must be rescanned before moving.");
        }

        var parentPath = Path.GetDirectoryName(path)
            ?? throw Conflict(
                $"Tracked file {audiobookFileId} has no containing directory.");
        try
        {
            using var parent = PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                parentPath,
                createMissing: false);
            using var file = parent.OpenExistingFileForStableRead(
                Path.GetFileName(path));
            if (!string.Equals(
                    file.GetObjectIdentity(),
                    expectedPhysicalObjectIdentity,
                    StringComparison.Ordinal))
            {
                throw Conflict(
                    $"Tracked file {audiobookFileId} identifies a different physical file generation and must be rescanned before moving.");
            }

            await using var stream = file.OpenReadStream(
                bufferSize: 128 * 1024,
                asynchronous: false);
            var length = stream.Length;
            var lastWriteTimeUtc = File.GetLastWriteTimeUtc(path);
            var hash = includeContentHash
                ? Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, cancellationToken))
                : null;
            if (!file.VisiblePathMatches()
                || !string.Equals(
                    file.GetObjectIdentity(),
                    expectedPhysicalObjectIdentity,
                    StringComparison.Ordinal)
                || stream.Length != length
                || File.GetLastWriteTimeUtc(path) != lastWriteTimeUtc)
            {
                throw Conflict(
                    $"Tracked file {audiobookFileId} changed while its move manifest was being created.");
            }

            return new ValidatedTrackedFile(
                audiobookFileId,
                path,
                length,
                lastWriteTimeUtc,
                hash);
        }
        catch (ApplicationConflictException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or ArgumentException or InvalidOperationException
                or NotSupportedException or PathTooLongException
                or System.ComponentModel.Win32Exception)
        {
            throw Conflict(
                $"Tracked file {audiobookFileId} could not be pinned to its persisted physical identity: {exception.Message}");
        }
    }

    private static string CalculateSourceRoot(
        IReadOnlyCollection<ValidatedTrackedFile> files,
        FileSystemPathSemantics semantics)
    {
        var directories = files
            .Select(file => Path.GetDirectoryName(file.Path)
                ?? throw Conflict(
                    $"Tracked file {file.AudiobookFileId} has no containing directory."))
            .Select(path => FileSystemPathIdentity.Canonicalize(
                path,
                semantics.Syntax))
            .Distinct(semantics.Comparer)
            .ToList();
        var common = directories.Count == 1
            ? directories[0]
            : FileUtils.GetCommonPathForDirectories(directories, semantics);
        if (string.IsNullOrWhiteSpace(common))
        {
            throw Conflict(
                "Tracked audiobook files do not share a safe source coordinate root.");
        }

        return common;
    }

    private static void ValidateSourceRoot(string sourceRoot)
    {
        if (!Directory.Exists(sourceRoot))
        {
            throw Conflict(
                "The computed tracked-file source root does not exist.");
        }

        var attributes = File.GetAttributes(sourceRoot);
        if ((attributes & FileAttributes.ReparsePoint) != 0
            || (attributes & FileAttributes.Directory) == 0)
        {
            throw Conflict(
                "The computed tracked-file source root became linked or changed type.");
        }
    }

    private static void ValidateSourceRootShape(
        string sourceRoot,
        IReadOnlyCollection<ValidatedTrackedFile> files,
        FileSystemPathSemantics semantics)
    {
        var firstSegments = new HashSet<string>(semantics.Comparer);
        var hasDirectFile = false;
        foreach (var file in files)
        {
            var directory = Path.GetDirectoryName(file.Path)
                ?? throw Conflict(
                    $"Tracked file {file.AudiobookFileId} has no containing directory.");
            if (FileSystemPathIdentity.AreEquivalent(
                    directory,
                    sourceRoot,
                    semantics))
            {
                hasDirectFile = true;
                continue;
            }

            var relative = Path.GetRelativePath(sourceRoot, directory);
            var firstSegment = relative.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(firstSegment))
            {
                firstSegments.Add(firstSegment);
            }
        }

        if (hasDirectFile || firstSegments.Count <= 1)
        {
            return;
        }

        if (firstSegments.All(IsDiscDirectory))
        {
            return;
        }

        throw Conflict(
            "Tracked audiobook files span unrelated source directories and require repair before moving.");
    }

    private static bool IsDiscDirectory(string segment)
    {
        var normalized = ScanFileDiscovery.NormalizeMetadataToken(segment)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        foreach (var prefix in new[] { "cd", "disc", "disk", "part" })
        {
            if (normalized.StartsWith(prefix, StringComparison.Ordinal)
                && normalized.Length > prefix.Length
                && normalized[prefix.Length..].All(char.IsDigit))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<MoveSourceManifestEntry> BuildEntries(
        string sourceRoot,
        IReadOnlyCollection<ValidatedTrackedFile> files,
        FileSystemPathSemantics semantics)
    {
        var directoryPaths = new HashSet<string>(semantics.Comparer);
        var fileEntries = new List<MoveSourceManifestEntry>(files.Count);
        foreach (var file in files)
        {
            if (!FileSystemPathIdentity.TryGetRelativePathWithinBase(
                    sourceRoot,
                    file.Path,
                    semantics,
                    out var relativePath)
                || string.IsNullOrWhiteSpace(relativePath)
                || Path.IsPathRooted(relativePath))
            {
                throw Conflict(
                    $"Tracked file {file.AudiobookFileId} escaped the computed source root.");
            }

            fileEntries.Add(new MoveSourceManifestEntry(
                relativePath,
                MoveJobEntryType.File,
                file.Length,
                file.LastWriteTimeUtc,
                file.Sha256));

            var directory = Path.GetDirectoryName(file.Path);
            while (!string.IsNullOrWhiteSpace(directory)
                && !FileSystemPathIdentity.AreEquivalent(
                    directory,
                    sourceRoot,
                    semantics))
            {
                if (!FileSystemPathIdentity.IsSameOrInside(
                        directory,
                        sourceRoot,
                        semantics))
                {
                    throw Conflict(
                        "A tracked file directory escaped the source root.");
                }

                var attributes = File.GetAttributes(directory);
                if ((attributes & FileAttributes.ReparsePoint) != 0
                    || (attributes & FileAttributes.Directory) == 0)
                {
                    throw Conflict(
                        "A tracked file directory became linked or changed type.");
                }

                directoryPaths.Add(directory);
                directory = Path.GetDirectoryName(directory);
            }
        }

        var directoryEntries = directoryPaths
            .OrderBy(path => path.Count(character =>
                character == Path.DirectorySeparatorChar
                || character == Path.AltDirectorySeparatorChar))
            .ThenBy(path => path, semantics.Comparer)
            .Select(path =>
            {
                if (!FileSystemPathIdentity.TryGetRelativePathWithinBase(
                        sourceRoot,
                        path,
                        semantics,
                        out var relativePath))
                {
                    throw Conflict(
                        "A tracked directory escaped the source root.");
                }

                return new MoveSourceManifestEntry(
                    relativePath,
                    MoveJobEntryType.Directory,
                    0,
                    Directory.GetLastWriteTimeUtc(path),
                    null);
            });
        return directoryEntries
            .Concat(fileEntries.OrderBy(entry => entry.RelativePath, semantics.Comparer))
            .ToList();
    }

    private static IReadOnlyList<MoveSourceManifestEntry> MergeEntries(
        IReadOnlyList<MoveSourceManifestEntry> trackedEntries,
        IReadOnlyList<MoveSourceManifestEntry> companionEntries,
        FileSystemPathSemantics semantics)
    {
        var merged = new Dictionary<string, MoveSourceManifestEntry>(
            semantics.Comparer);
        foreach (var entry in trackedEntries.Concat(companionEntries))
        {
            if (merged.TryGetValue(entry.RelativePath, out var existing))
            {
                if (existing.EntryType != entry.EntryType)
                {
                    throw Conflict(
                        $"Move manifest path changed type while companion files were being captured: {entry.RelativePath}");
                }

                continue;
            }

            merged.Add(entry.RelativePath, entry);
        }

        return merged.Values
            .OrderBy(entry => entry.EntryType == MoveJobEntryType.Directory ? 0 : 1)
            .ThenBy(entry => entry.RelativePath, semantics.Comparer)
            .ToList();
    }

    private static bool HasSameFilesystemAuthority(
        PathIdentitySnapshot left,
        PathIdentitySnapshot right) =>
        left.Syntax == right.Syntax
        && left.CaseSensitivity == right.CaseSensitivity
        && left.RequestedMode == right.RequestedMode
        && FileSystemPathIdentity.AreEquivalent(
            left.BoundaryPath,
            right.BoundaryPath,
            left.Semantics);

    private static bool IsFilesystemRoot(
        string path,
        FileSystemPathSemantics semantics)
    {
        var root = Path.GetPathRoot(path);
        return !string.IsNullOrWhiteSpace(root)
            && FileSystemPathIdentity.AreEquivalent(path, root, semantics);
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static ApplicationConflictException Conflict(string message) =>
        new("move_source_unverified", message);

    private sealed record ValidatedTrackedFile(
        int AudiobookFileId,
        string Path,
        long Length,
        DateTime LastWriteTimeUtc,
        string? Sha256);
}
