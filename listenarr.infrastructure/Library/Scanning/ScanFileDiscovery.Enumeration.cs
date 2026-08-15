using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Scanning;

internal static partial class ScanFileDiscovery
{
    internal static EnumerationResult CollectCandidates(
        IFileSystem fileSystem,
        string scanRoot,
        Guid jobId,
        ILogger logger,
        FileSystemPathSemantics semantics,
        PinnedDirectoryCreation.PinnedDirectoryAnchor? pinnedScanRoot)
    {
        var candidates = new HashSet<string>(semantics.Comparer);
        var enumeratedDirectories = new HashSet<string>(semantics.Comparer);
        var directoryObjectIdentities = new Dictionary<string, string>(
            semantics.Comparer);
        var fileObjectIdentities = new Dictionary<string, string>(
            semantics.Comparer);
        var issues = new List<ScanDiscoveryIssue>();
        var directories = new Stack<DirectoryEnumerationAnchor>();
        var root = pinnedScanRoot?.Duplicate()
            ?? PinnedDirectoryCreation.OpenPinnedBoundary(scanRoot);
        directories.Push(new DirectoryEnumerationAnchor(
            root,
            root.GetDirectoryObjectIdentity()));

        try
        {
            while (directories.Count > 0)
            {
                var pending = directories.Pop();
                using var directory = pending.Anchor;
                var localCandidates = new Dictionary<string, string>(
                    semantics.Comparer);
                var localChildren = new List<DirectoryEnumerationAnchor>();
                try
                {
                    if (!DirectoryIdentityMatches(directory, pending.ObjectIdentity))
                    {
                        RecordDirectoryGenerationChange(
                            issues,
                            logger,
                            jobId,
                            directory.FullPath);
                        continue;
                    }

                    foreach (var visibleFile in fileSystem
                        .EnumerateFiles(directory.FullPath)
                        .ToList())
                    {
                        try
                        {
                            if (fileSystem.IsReparsePoint(visibleFile))
                            {
                                issues.Add(new ScanDiscoveryIssue(
                                    ScanDiscoveryIssueKind.LinkSkipped,
                                    visibleFile,
                                    "Linked files are not scanned."));
                                continue;
                            }

                            var fileName = Path.GetFileName(visibleFile);
                            using var pinnedFile = directory.OpenExistingFile(
                                fileName,
                                requireDeleteAccess: false);
                            if (!pinnedFile.VisiblePathMatches())
                            {
                                RecordDirectoryGenerationChange(
                                    issues,
                                    logger,
                                    jobId,
                                    visibleFile);
                                continue;
                            }

                            if (FileUtils.IsAudioFile(visibleFile))
                            {
                                var canonicalFile =
                                    FileSystemPathIdentity.Canonicalize(
                                        pinnedFile.FullPath,
                                        semantics.Syntax);
                                localCandidates[canonicalFile] =
                                    pinnedFile.GetObjectIdentity();
                            }
                        }
                        catch (Exception exception) when (
                            IsFilesystemException(exception)
                            || exception is InvalidOperationException)
                        {
                            RecordEnumerationFailure(
                                issues,
                                logger,
                                jobId,
                                visibleFile,
                                exception);
                        }
                    }

                    foreach (var visibleChild in fileSystem
                        .EnumerateDirectories(directory.FullPath)
                        .ToList())
                    {
                        try
                        {
                            if (fileSystem.IsReparsePoint(visibleChild))
                            {
                                logger.LogWarning(
                                    "Skipped linked directory while scanning job {JobId}: {Dir}",
                                    jobId,
                                    LogRedaction.SanitizeFilePath(visibleChild));
                                issues.Add(new ScanDiscoveryIssue(
                                    ScanDiscoveryIssueKind.LinkSkipped,
                                    visibleChild,
                                    "Linked directories are not traversed."));
                                continue;
                            }

                            var childName = Path.GetFileName(
                                Path.TrimEndingDirectorySeparator(visibleChild));
                            var childAnchor = directory.OpenExistingChild(childName);
                            localChildren.Add(new DirectoryEnumerationAnchor(
                                childAnchor,
                                childAnchor.GetDirectoryObjectIdentity()));
                        }
                        catch (Exception exception) when (
                            IsFilesystemException(exception)
                            || exception is InvalidOperationException)
                        {
                            RecordEnumerationFailure(
                                issues,
                                logger,
                                jobId,
                                visibleChild,
                                exception);
                        }
                    }

                    if (!DirectoryIdentityMatches(directory, pending.ObjectIdentity))
                    {
                        foreach (var child in localChildren)
                        {
                            child.Anchor.Dispose();
                        }

                        RecordDirectoryGenerationChange(
                            issues,
                            logger,
                            jobId,
                            directory.FullPath);
                        continue;
                    }

                    foreach (var candidate in localCandidates)
                    {
                        candidates.Add(candidate.Key);
                        fileObjectIdentities[candidate.Key] = candidate.Value;
                    }

                    var canonicalDirectory =
                        FileSystemPathIdentity.Canonicalize(
                            directory.FullPath,
                            semantics.Syntax);
                    enumeratedDirectories.Add(canonicalDirectory);
                    directoryObjectIdentities[canonicalDirectory] =
                        pending.ObjectIdentity;
                    foreach (var child in localChildren)
                    {
                        directories.Push(child);
                    }
                }
                catch (Exception exception) when (
                    IsFilesystemException(exception)
                    || exception is InvalidOperationException)
                {
                    foreach (var child in localChildren)
                    {
                        child.Anchor.Dispose();
                    }

                    RecordEnumerationFailure(
                        issues,
                        logger,
                        jobId,
                        directory.FullPath,
                        exception);
                }
            }
        }
        finally
        {
            while (directories.TryPop(out var pending))
            {
                pending.Anchor.Dispose();
            }
        }

        return new EnumerationResult(
            candidates.OrderBy(path => path, semantics.Comparer).ToList(),
            enumeratedDirectories.OrderBy(path => path, semantics.Comparer).ToList(),
            directoryObjectIdentities,
            fileObjectIdentities,
            issues);
    }

    private static bool DirectoryIdentityMatches(
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory,
        string expectedIdentity) =>
        directory.VisiblePathMatches()
        && string.Equals(
            directory.GetDirectoryObjectIdentity(),
            expectedIdentity,
            StringComparison.Ordinal);

    private static bool IsFilesystemException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException
            or System.ComponentModel.Win32Exception;

    private static void RecordEnumerationFailure(
        ICollection<ScanDiscoveryIssue> issues,
        ILogger logger,
        Guid jobId,
        string path,
        Exception exception)
    {
        logger.LogWarning(
            exception,
            "Failed to enumerate path for scan job {JobId}: {Path}",
            jobId,
            LogRedaction.SanitizeFilePath(path));
        issues.Add(new ScanDiscoveryIssue(
            ScanDiscoveryIssueKind.EnumerationFailure,
            path,
            "The path could not be enumerated safely."));
    }

    private static void RecordDirectoryGenerationChange(
        ICollection<ScanDiscoveryIssue> issues,
        ILogger logger,
        Guid jobId,
        string path)
    {
        logger.LogWarning(
            "Directory or file generation changed while scanning job {JobId}: {Path}",
            jobId,
            LogRedaction.SanitizeFilePath(path));
        issues.Add(new ScanDiscoveryIssue(
            ScanDiscoveryIssueKind.DirectoryGenerationChanged,
            path,
            "A directory or file generation changed while it was being enumerated."));
    }

    private sealed record DirectoryEnumerationAnchor(
        PinnedDirectoryCreation.PinnedDirectoryAnchor Anchor,
        string ObjectIdentity);

    internal sealed record EnumerationResult(
        IReadOnlyList<string> Candidates,
        IReadOnlyList<string> EnumeratedDirectories,
        IReadOnlyDictionary<string, string> DirectoryObjectIdentities,
        IReadOnlyDictionary<string, string> FileObjectIdentities,
        IReadOnlyList<ScanDiscoveryIssue> Issues);
}
