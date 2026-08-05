using System.Security.Cryptography;
using System.Text.Json;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private static async Task WriteCleanupJournalAsync(
        PinnedDirectoryCreation.PinnedFileEntry journal,
        CleanupJournalPayload payload)
    {
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var envelope = new CleanupJournalEnvelope(
            Convert.ToBase64String(payloadBytes),
            Convert.ToHexString(SHA256.HashData(payloadBytes)));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope);
        await using var stream = journal.OpenWriteStream(4096, asynchronous: false);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
        stream.Flush(flushToDisk: true);
    }

    private static async Task<bool> ValidateCleanupManifestAsync(
        CleanupJournalPayload payload,
        string candidateRoot,
        bool validateDestination)
    {
        if (!TryGetDirectoryIdentity(candidateRoot, out var rootIdentity)
            || rootIdentity != payload.SourceRootIdentity
            || (validateDestination
                && (!TryGetDirectoryIdentity(
                        payload.DestinationRoot,
                        out var destinationIdentity)
                    || destinationIdentity != payload.DestinationRootIdentity)))
        {
            return false;
        }
        if (!FileSystemSafety.TryEnumerateTreeWithoutLinks(
                candidateRoot,
                out var actualFiles,
                out var actualDirectories,
                out _))
        {
            return false;
        }
        var expectedFilePaths = payload.Files
            .Select(file => file.RelativePath)
            .ToHashSet(StringComparer.Ordinal);
        var recoveryFiles = payload.Files.ToDictionary(
            file => GetCleanupRecoveryRelativePath(payload, file),
            file => file,
            StringComparer.Ordinal);
        var actualLogicalFilePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var actualFile in actualFiles)
        {
            var actualRelative = GetVerifiedRelativePath(
                candidateRoot,
                actualFile);
            var logicalRelative = expectedFilePaths.Contains(actualRelative)
                ? actualRelative
                : recoveryFiles.TryGetValue(actualRelative, out var recoveryFile)
                    ? recoveryFile.RelativePath
                    : null;
            if (logicalRelative == null
                || !actualLogicalFilePaths.Add(logicalRelative))
            {
                return false;
            }
        }

        var expectedDirectoryPaths = payload.Directories.Keys
            .ToHashSet(StringComparer.Ordinal);
        var actualDirectoryPaths = actualDirectories
            .Select(path => GetVerifiedRelativePath(candidateRoot, path))
            .ToHashSet(StringComparer.Ordinal);
        if (!expectedDirectoryPaths.SetEquals(actualDirectoryPaths))
        {
            return false;
        }

        foreach (var file in payload.Files)
        {
            var candidate = ResolveSnapshotPath(
                candidateRoot,
                file.RelativePath,
                "quarantined cleanup file");
            if (!File.Exists(candidate))
            {
                candidate = ResolveSnapshotPath(
                    candidateRoot,
                    GetCleanupRecoveryRelativePath(payload, file),
                    "quarantined cleanup recovery file");
            }

            var candidateExists = File.Exists(candidate);
            if (candidateExists)
            {
                if (!TryGetRegularFileIdentity(candidate, out var identity)
                    || identity != file.Identity)
                {
                    return false;
                }
                await using var stream = new FileStream(
                    candidate,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length != file.Length
                    || !string.Equals(
                        Convert.ToHexString(await SHA256.HashDataAsync(stream)),
                        file.Sha256,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }
            else if (!validateDestination)
            {
                return false;
            }
            if (validateDestination)
            {
                var destination = ResolveSnapshotPath(
                    payload.DestinationRoot,
                    file.RelativePath,
                    "cleanup destination file");
                if (candidateExists)
                {
                    if (!await FileSystemSafety.FilesHaveSameContentAsync(
                            candidate,
                            destination))
                    {
                        return false;
                    }
                }
                else
                {
                    using var retention =
                        await OpenExistingCleanupRetentionAsync(payload, file);
                    if (retention != null)
                    {
                        if (!await retention.CurrentPublicationMatchesAsync(
                                CancellationToken.None))
                        {
                            return false;
                        }
                    }
                    else
                    {
                        await using var destinationStream = new FileStream(
                            destination,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read | FileShare.Delete,
                            128 * 1024,
                            FileOptions.Asynchronous | FileOptions.SequentialScan);
                        if (destinationStream.Length != file.Length
                            || !string.Equals(
                                Convert.ToHexString(
                                    await SHA256.HashDataAsync(destinationStream)),
                                file.Sha256,
                                StringComparison.Ordinal))
                        {
                            return false;
                        }
                    }
                }

                if (payload.Version == 2
                    && (!file.DestinationIdentity.HasValue
                        || !TryGetRegularFileIdentity(
                            destination,
                            out var destinationFileIdentity)
                        || destinationFileIdentity
                            != file.DestinationIdentity.Value))
                {
                    return false;
                }
                if (payload.Version is not (1 or 2))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private async Task<bool> DeletePinnedCleanupTreeAsync(
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory,
        CleanupJournalPayload payload,
        string relativeDirectory)
    {
        var expectedFiles = payload.Files.ToDictionary(
            file => file.RelativePath,
            file => file,
            StringComparer.Ordinal);
        var recoveryFiles = payload.Files.ToDictionary(
            file => GetCleanupRecoveryRelativePath(payload, file),
            file => file,
            StringComparer.Ordinal);
        var processedFiles = new HashSet<string>(StringComparer.Ordinal);
        var names = Directory.EnumerateFileSystemEntries(directory.FullPath)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Cast<string>()
            .ToArray();
        if (!directory.VisiblePathMatches())
        {
            return false;
        }

        foreach (var name in names)
        {
            var relative = string.IsNullOrEmpty(relativeDirectory)
                ? name
                : Path.Join(relativeDirectory, name);
            var path = Path.Join(directory.FullPath, name);
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (!payload.Directories.ContainsKey(relative))
                {
                    return false;
                }
                using var childPublication =
                    directory.OpenExistingChildForPublication(name);
                using var child = childPublication.OpenCreatedDirectoryAnchor();
                using var childHandle = child.DuplicateHandleForOperation();
                if (!payload.Directories.TryGetValue(
                        relative,
                        out var expectedDirectoryIdentity)
                    || !TryGetRegularFileIdentity(
                        childHandle,
                        out var childIdentity)
                    || childIdentity != expectedDirectoryIdentity
                    || !await DeletePinnedCleanupTreeAsync(
                        child,
                        payload,
                        relative)
                    || !child.VisiblePathMatches()
                    || Directory.EnumerateFileSystemEntries(path).Any())
                {
                    return false;
                }
                childPublication.RetirePinnedEmptyDirectoryFromNamespace(
                    name);
                continue;
            }
            var isRecoveryFile = false;
            if (!expectedFiles.TryGetValue(relative, out var expectedFile))
            {
                if (!recoveryFiles.TryGetValue(relative, out expectedFile))
                {
                    return false;
                }

                isRecoveryFile = true;
            }
            if (!processedFiles.Add(expectedFile.RelativePath))
            {
                return false;
            }

            using var file = directory.OpenExistingFile(
                name,
                requireDeleteAccess: true);
            if (payload.Version != 2
                || !expectedFile.DestinationIdentity.HasValue)
            {
                return false;
            }

            using var destinationRoot =
                PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                    payload.DestinationRoot,
                    createMissing: false);
            using var destinationRootHandle =
                destinationRoot.DuplicateHandleForOperation();
            if (!TryGetRegularFileIdentity(
                    destinationRootHandle,
                    out var destinationRootIdentity)
                || destinationRootIdentity != payload.DestinationRootIdentity)
            {
                return false;
            }

            var destinationParentRelative =
                Path.GetDirectoryName(expectedFile.RelativePath);
            using var destinationParent = OpenRelativeCleanupDirectory(
                destinationRoot,
                destinationParentRelative);
            using var destinationFile = destinationParent.OpenExistingFile(
                Path.GetFileName(expectedFile.RelativePath),
                requireDeleteAccess: false);
            using var destinationFileHandle =
                destinationFile.DuplicateHandleForOperation();
            using var fileHandle = file.DuplicateHandleForOperation();
            if (!TryGetRegularFileIdentity(fileHandle, out var fileIdentity)
                || fileIdentity != expectedFile.Identity
                || !TryGetRegularFileIdentity(
                    destinationFileHandle,
                    out var destinationFileIdentity)
                || destinationFileIdentity
                    != expectedFile.DestinationIdentity.Value
                || !await file.MatchesAsync(
                    expectedFile.Length,
                    expectedFile.Sha256,
                    CancellationToken.None)
                || !await destinationFile.MatchesAsync(
                    expectedFile.Length,
                    expectedFile.Sha256,
                    CancellationToken.None)
                || !directory.VisiblePathMatches()
                || !file.VisiblePathMatches()
                || !destinationRoot.VisiblePathMatches()
                || !destinationParent.VisiblePathMatches()
                || !destinationFile.VisiblePathMatches())
            {
                return false;
            }

            if (AfterCleanupDestinationPinnedForTestAsync != null)
            {
                await AfterCleanupDestinationPinnedForTestAsync(
                    expectedFile.RelativePath);
            }

            if (!directory.VisiblePathMatches()
                || !file.VisiblePathMatches()
                || !destinationRoot.VisiblePathMatches()
                || !destinationParent.VisiblePathMatches()
                || !destinationFile.VisiblePathMatches()
                || !await destinationFile.MatchesAsync(
                    expectedFile.Length,
                    expectedFile.Sha256,
                    CancellationToken.None))
            {
                return false;
            }

            var retentionName =
                PinnedDestinationRetentionGuard.CreateRetentionName(
                    payload.OperationId,
                    expectedFile.RelativePath);
            using var retention =
                await PinnedDestinationRetentionGuard.OpenOrCreateAsync(
                    destinationParent,
                    Path.GetFileName(expectedFile.RelativePath),
                    retentionName,
                    expectedFile.Length,
                    expectedFile.Sha256,
                    CancellationToken.None);
            if (retention == null)
            {
                return false;
            }

            var recoveryName =
                PinnedDestinationRetentionGuard.CreateSourceRecoveryName(
                    payload.OperationId,
                    expectedFile.RelativePath);
            if (!isRecoveryFile)
            {
                file.MoveWithinParent(recoveryName);
                directory.FlushDirectoryEntry();
            }
            if (AfterCleanupSourceFileRetiredForTestAsync != null)
            {
                await AfterCleanupSourceFileRetiredForTestAsync(
                    expectedFile.RelativePath);
            }

            if (BeforeCleanupSourceRecoveryDeleteForTestAsync != null)
            {
                await BeforeCleanupSourceRecoveryDeleteForTestAsync(
                    expectedFile.RelativePath);
            }

            if (!await retention.TryLinearizePublicationAsync(
                    CancellationToken.None))
            {
                return false;
            }

            file.Delete(immediateWindows: true);
            directory.FlushDirectoryEntry();
            if (AfterCleanupSourceRecoveryDeleteForTestAsync != null)
            {
                await AfterCleanupSourceRecoveryDeleteForTestAsync(
                    expectedFile.RelativePath);
            }

            if (!await retention.CompleteAsync(CancellationToken.None))
            {
                return false;
            }
        }

        foreach (var expectedFile in payload.Files.Where(file =>
            IsDirectChildOfCleanupDirectory(
                file.RelativePath,
                relativeDirectory)))
        {
            if (processedFiles.Contains(expectedFile.RelativePath))
            {
                continue;
            }

            using var retention = await OpenExistingCleanupRetentionAsync(
                payload,
                expectedFile);
            if (retention == null)
            {
                continue;
            }

            if (!await retention.TryLinearizePublicationAsync(
                    CancellationToken.None)
                || !await retention.CompleteAsync(CancellationToken.None))
            {
                return false;
            }
        }

        return directory.VisiblePathMatches()
            && !Directory.EnumerateFileSystemEntries(directory.FullPath).Any();
    }

}
