/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Scanning;

internal static class ScanFileDiscovery
{
    public static List<string> FindMatchingAudioFiles(
        string scanRoot,
        Audiobook audiobook,
        Guid jobId,
        ILogger logger)
    {
        var candidates = CollectCandidates(scanRoot, jobId, logger);
        var titleToken = (audiobook.Title ?? string.Empty).Replace("\"", string.Empty).Trim();
        var authorToken = audiobook.Authors?.FirstOrDefault() ?? string.Empty;
        if (string.IsNullOrEmpty(titleToken) && string.IsNullOrEmpty(authorToken))
        {
            return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        var foundFiles = new List<string>();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in candidates.GroupBy(file => Path.GetDirectoryName(file) ?? string.Empty))
        {
            var directoryName = Path.GetFileName(group.Key) ?? string.Empty;
            var groupHasMatch = group.Any(file =>
                Matches(file, directoryName, titleToken, authorToken));
            if (groupHasMatch)
            {
                foundFiles.AddRange(group.Where(unique.Add));
                continue;
            }

            foreach (var file in group.Where(file =>
                         Matches(file, directoryName: string.Empty, titleToken, authorToken)))
            {
                if (unique.Add(file))
                {
                    foundFiles.Add(file);
                }
            }
        }

        return foundFiles;
    }

    private static List<string> CollectCandidates(string scanRoot, Guid jobId, ILogger logger)
    {
        var candidates = new List<string>();
        var directories = new Stack<string>();
        directories.Push(scanRoot);

        while (directories.Count > 0)
        {
            var directory = directories.Pop();
            try
            {
                var normalizedDirectory = Path.GetFullPath(directory);
                foreach (var file in Directory.EnumerateFiles(normalizedDirectory))
                {
                    try
                    {
                        if (FileUtils.IsAudioFile(file))
                        {
                            candidates.Add(file);
                        }
                    }
                    catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                    {
                        logger.LogDebug(exception, "Skipped file while scanning {Dir}", normalizedDirectory);
                    }
                }

                foreach (var child in Directory.EnumerateDirectories(normalizedDirectory))
                {
                    directories.Push(child);
                }
            }
            catch (IOException exception)
            {
                logger.LogWarning(exception, "IO error while enumerating directory for scan job {JobId}: {Dir}", jobId, directory);
            }
            catch (UnauthorizedAccessException exception)
            {
                logger.LogWarning(exception, "Access denied while enumerating directory for scan job {JobId}: {Dir}", jobId, directory);
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                logger.LogWarning(exception, "Unexpected error while enumerating directory for scan job {JobId}: {Dir}", jobId, directory);
            }
        }

        return candidates;
    }

    private static bool Matches(
        string file,
        string directoryName,
        string titleToken,
        string authorToken)
    {
        var fileNameMatchesTitle = !string.IsNullOrEmpty(titleToken)
            && Path.GetFileNameWithoutExtension(file)
                .Contains(titleToken, StringComparison.OrdinalIgnoreCase);
        var filePathMatchesAuthor = !string.IsNullOrEmpty(authorToken)
            && file.Contains(authorToken, StringComparison.OrdinalIgnoreCase);
        var directoryMatchesTitle = !string.IsNullOrEmpty(directoryName)
            && !string.IsNullOrEmpty(titleToken)
            && directoryName.Contains(titleToken, StringComparison.OrdinalIgnoreCase);
        return fileNameMatchesTitle || filePathMatchesAuthor || directoryMatchesTitle;
    }
}
