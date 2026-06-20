/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Api.Dtos.ManualImport;
using Listenarr.Domain.Common;

namespace Listenarr.Api.Features.Downloads;

public sealed class ManualImportPathPlanner
{
    private readonly IFileNamingService _fileNamingService;

    public ManualImportPathPlanner(IFileNamingService fileNamingService)
    {
        _fileNamingService = fileNamingService;
    }

    public static string? DetermineScanPath(IReadOnlyList<string> destinationPaths)
    {
        return FileUtils.GetCommonDirectory(destinationPaths);
    }

    public async Task<string> GeneratePathAsync(
        Audiobook audiobook,
        AudioMetadata metadata,
        ManualImportItemDto item,
        List<RootFolder> rootFolders,
        ApplicationSettings settings,
        bool isMultiFile = false)
    {
        await Task.CompletedTask;

        var sourceFilePath = item.FullPath ?? string.Empty;
        var folderPattern = settings.FolderNamingPattern;
        var filePattern = isMultiFile ? settings.MultiFileNamingPattern : settings.FileNamingPattern;

        var basePath = string.IsNullOrWhiteSpace(audiobook.BasePath)
            ? string.Empty
            : FileUtils.NormalizeStoredPath(audiobook.BasePath);
        var configuredOutput = settings.OutputPath ?? string.Empty;
        var isCustomBasePath = IsCustomBasePath(basePath, configuredOutput, rootFolders);

        var extension = Path.GetExtension(sourceFilePath).ToLowerInvariant();
        if (string.IsNullOrEmpty(extension))
        {
            extension = ".m4b";
        }

        var variables = BuildNamingVariables(audiobook, metadata, item, folderPattern, filePattern, isMultiFile, out var stableSuffixNumber);

        string relativePath;
        var patternHasNumberTokens = !string.IsNullOrWhiteSpace(filePattern)
            && (filePattern.IndexOf("DiskNumber", StringComparison.OrdinalIgnoreCase) >= 0
                || filePattern.IndexOf("ChapterNumber", StringComparison.OrdinalIgnoreCase) >= 0);

        if (string.IsNullOrWhiteSpace(folderPattern))
        {
            var legacyPattern = string.IsNullOrWhiteSpace(filePattern)
                ? "{Author}/{Title}/{Title}"
                : filePattern;

            relativePath = _fileNamingService.ApplyNamingPattern(legacyPattern, variables, treatAsFilename: false);
        }
        else if (isCustomBasePath)
        {
            var effectiveFilePattern = string.IsNullOrWhiteSpace(filePattern) ? "{Title}" : filePattern;
            var patternAllowsSubfolders = PatternAllowsSubfolders(effectiveFilePattern);

            relativePath = _fileNamingService.ApplyNamingPattern(effectiveFilePattern, variables, treatAsFilename: !patternAllowsSubfolders);
        }
        else
        {
            var effectiveFilePattern = string.IsNullOrWhiteSpace(filePattern) ? "{Title}" : filePattern;
            var folderRelative = _fileNamingService.ApplyNamingPattern(folderPattern, variables, treatAsFilename: false);
            var patternAllowsSubfolders = PatternAllowsSubfolders(effectiveFilePattern);
            var fileRelative = _fileNamingService.ApplyNamingPattern(effectiveFilePattern, variables, treatAsFilename: !patternAllowsSubfolders);

            if (isMultiFile && !patternHasNumberTokens && stableSuffixNumber.HasValue)
                fileRelative = FileUtils.AppendSequenceSuffix(fileRelative, stableSuffixNumber.Value);

            relativePath = string.IsNullOrWhiteSpace(folderRelative)
                ? fileRelative
                : CombineWithOptionalBase(folderRelative, fileRelative);
        }

        if ((string.IsNullOrWhiteSpace(folderPattern) || isCustomBasePath)
            && isMultiFile
            && !patternHasNumberTokens
            && stableSuffixNumber.HasValue)
        {
            relativePath = FileUtils.AppendSequenceSuffix(relativePath, stableSuffixNumber.Value);
        }

        if (!relativePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            relativePath += extension;
        }

        return string.IsNullOrWhiteSpace(basePath)
            ? relativePath
            : CombineWithOptionalBase(basePath, relativePath);
    }

    public static string CombineWithOptionalBase(string? basePath, string candidatePath)
    {
        var normalizedPath = candidatePath.Trim();

        if (string.IsNullOrEmpty(normalizedPath))
        {
            return normalizedPath;
        }

        if (Path.IsPathRooted(normalizedPath) || string.IsNullOrWhiteSpace(basePath))
        {
            return normalizedPath;
        }

        var relativePath = normalizedPath.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Path.IsPathRooted(relativePath))
        {
            return relativePath;
        }

        var normalizedBasePath = basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.IsNullOrEmpty(normalizedBasePath)
            ? relativePath
            : normalizedBasePath + Path.DirectorySeparatorChar + relativePath;
    }

    public static List<ManualImportItemDto> BuildOrderedItems(IEnumerable<ManualImportItemDto> items)
    {
        var ordered = new List<ManualImportItemDto>();

        foreach (var validItems in items.GroupBy(i => i.MatchedAudiobookId).Select(g => g.Where(i => !string.IsNullOrWhiteSpace(i.FullPath)).ToList()))
        {
            if (validItems.Count == 0)
            {
                continue;
            }

            var plans = MultiFileImportPlanner.BuildPlans(validItems.Select(i => (i.FullPath!, string.IsNullOrWhiteSpace(i.RelativePath) ? null : i.RelativePath)));
            var itemLookup = validItems.ToDictionary(i => i.FullPath!, StringComparer.OrdinalIgnoreCase);
            var diskNumbersForNaming = MultiFileImportPlanner.BuildStableNamingNumbers(plans, p => p.DiskNumberHint);
            var chapterNumbersForNaming = MultiFileImportPlanner.BuildStableNamingNumbers(plans, p => p.ChapterNumberHint);

            ordered.AddRange(plans
                .Select(plan =>
                {
                    if (!itemLookup.TryGetValue(plan.FullPath, out var item))
                    {
                        return null;
                    }

                    item.SequenceNumberHint = plan.SequenceNumber;
                    item.DiskNumberHint = diskNumbersForNaming.TryGetValue(plan.FullPath, out var diskNumber) ? diskNumber : plan.DiskNumberHint;
                    item.ChapterNumberHint = chapterNumbersForNaming.TryGetValue(plan.FullPath, out var chapterNumber) ? chapterNumber : plan.ChapterNumberHint;
                    return item;
                })
                .Where(item => item != null)!
                .Cast<ManualImportItemDto>());
        }

        foreach (var invalidItem in items.Where(i => string.IsNullOrWhiteSpace(i.FullPath)))
        {
            ordered.Add(invalidItem);
        }

        return ordered;
    }

    private static bool IsCustomBasePath(string basePath, string configuredOutput, List<RootFolder> rootFolders)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(basePath))
            {
                return false;
            }

            var baseFull = FileUtils.NormalizeStoredPath(basePath);
            var configuredFull = string.IsNullOrWhiteSpace(configuredOutput) ? string.Empty : Path.GetFullPath(configuredOutput);
            var isCustomBasePath = !string.Equals(baseFull, configuredFull, StringComparison.OrdinalIgnoreCase);

            if (isCustomBasePath)
            {
                var isRootFolder = rootFolders.Any(r =>
                {
                    try { return string.Equals(FileUtils.NormalizeStoredPath(r.Path), baseFull, StringComparison.OrdinalIgnoreCase); }
                    catch (ArgumentException) { return false; }
                    catch (NotSupportedException) { return false; }
                    catch (PathTooLongException) { return false; }
                });
                if (isRootFolder) isCustomBasePath = false;
            }

            return isCustomBasePath;
        }
        catch (Exception caughtEx) when (caughtEx is not OperationCanceledException && caughtEx is not OutOfMemoryException && caughtEx is not StackOverflowException)
        {
            return !string.IsNullOrWhiteSpace(basePath) && !string.IsNullOrWhiteSpace(configuredOutput)
                && !string.Equals(basePath, configuredOutput, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<string, object> BuildNamingVariables(
        Audiobook audiobook,
        AudioMetadata metadata,
        ManualImportItemDto item,
        string? folderPattern,
        string? filePattern,
        bool isMultiFile,
        out int? stableSuffixNumber)
    {
        var variables = new Dictionary<string, object>();

        var author = audiobook.Authors?.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(author)) variables["Author"] = author;

        var narrator = audiobook.Narrators != null
            ? string.Join(", ", audiobook.Narrators.Where(n => !string.IsNullOrWhiteSpace(n)))
            : string.Empty;
        if (!string.IsNullOrWhiteSpace(narrator)) variables["Narrator"] = narrator;

        if (!string.IsNullOrWhiteSpace(audiobook.Publisher)) variables["Publisher"] = audiobook.Publisher;
        if (!string.IsNullOrWhiteSpace(audiobook.Language)) variables["Language"] = audiobook.Language;
        if (!string.IsNullOrWhiteSpace(audiobook.Asin)) variables["Asin"] = audiobook.Asin;
        if (!string.IsNullOrWhiteSpace(audiobook.Subtitle)) variables["Subtitle"] = audiobook.Subtitle;
        if (!string.IsNullOrWhiteSpace(audiobook.Edition)) variables["Edition"] = audiobook.Edition;

        var usesSubtitleToken = (!string.IsNullOrWhiteSpace(folderPattern) && folderPattern.IndexOf("Subtitle", StringComparison.OrdinalIgnoreCase) >= 0)
            || (!string.IsNullOrWhiteSpace(filePattern) && filePattern.IndexOf("Subtitle", StringComparison.OrdinalIgnoreCase) >= 0);

        var titleFull = !usesSubtitleToken
            && !string.IsNullOrWhiteSpace(audiobook.Subtitle)
            && !string.IsNullOrWhiteSpace(audiobook.Title)
            && !audiobook.Title.Contains(audiobook.Subtitle, StringComparison.OrdinalIgnoreCase)
            ? $"{audiobook.Title}: {audiobook.Subtitle}"
            : audiobook.Title;
        variables["Title"] = !string.IsNullOrWhiteSpace(titleFull) ? titleFull : "Unknown Title";

        if (!string.IsNullOrWhiteSpace(audiobook.Series)) variables["Series"] = audiobook.Series;
        if (!string.IsNullOrWhiteSpace(audiobook.PublishYear)) variables["Year"] = audiobook.PublishYear;

        var effectiveDiskNumber = item.DiskNumberHint
            ?? (metadata.DiscNumber.HasValue && metadata.DiscNumber.Value > 0 ? metadata.DiscNumber.Value : null);
        var effectiveChapterNumber = item.ChapterNumberHint
            ?? (metadata.TrackNumber.HasValue && metadata.TrackNumber.Value > 0 ? metadata.TrackNumber.Value : null);

        if (isMultiFile)
        {
            effectiveDiskNumber ??= effectiveChapterNumber;
            effectiveChapterNumber ??= effectiveDiskNumber;
        }

        if (effectiveDiskNumber.HasValue && effectiveDiskNumber.Value > 0) variables["DiskNumber"] = effectiveDiskNumber.Value;
        if (effectiveChapterNumber.HasValue && effectiveChapterNumber.Value > 0) variables["ChapterNumber"] = effectiveChapterNumber.Value;

        stableSuffixNumber = effectiveChapterNumber ?? effectiveDiskNumber ?? item.SequenceNumberHint;
        return variables;
    }

    private static bool PatternAllowsSubfolders(string effectiveFilePattern)
    {
        return effectiveFilePattern.IndexOf("DiskNumber", StringComparison.OrdinalIgnoreCase) >= 0
            || effectiveFilePattern.IndexOf("ChapterNumber", StringComparison.OrdinalIgnoreCase) >= 0
            || effectiveFilePattern.IndexOf('/') >= 0
            || effectiveFilePattern.IndexOf('\\') >= 0;
    }
}
