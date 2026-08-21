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

public sealed record ManualImportPathPlan(
    string DestinationPath,
    string AudiobookBasePath);

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

    public async Task<ManualImportPathPlan> GeneratePathAsync(
        Audiobook audiobook,
        AudioMetadata metadata,
        ManualImportItemDto item,
        string destinationBasePath,
        List<RootFolder> rootFolders,
        ApplicationSettings settings,
        FileSystemPathSemantics destinationSemantics,
        bool isMultiFile = false)
    {
        await Task.CompletedTask;

        var sourceFilePath = item.FullPath ?? string.Empty;
        var folderPattern = settings.FolderNamingPattern;
        var filePattern = isMultiFile ? settings.MultiFileNamingPattern : settings.FileNamingPattern;

        var basePath = string.IsNullOrWhiteSpace(destinationBasePath)
            ? string.Empty
            : FileUtils.NormalizeStoredPath(destinationBasePath);
        var configuredOutput = settings.OutputPath ?? string.Empty;
        var isCustomBasePath = IsCustomBasePath(basePath, configuredOutput, rootFolders, destinationSemantics);

        var extension = Path.GetExtension(sourceFilePath).ToLowerInvariant();
        if (string.IsNullOrEmpty(extension))
        {
            extension = ".m4b";
        }

        var variables = BuildNamingVariables(audiobook, metadata, item, folderPattern, filePattern, isMultiFile, out var stableSuffixNumber);

        string relativePath;
        string? plannedFolderRelative = null;
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
            plannedFolderRelative = folderRelative;
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

        var destinationPath = string.IsNullOrWhiteSpace(basePath)
            ? relativePath
            : CombineWithOptionalBase(basePath, relativePath);
        var audiobookBasePath = ResolveAudiobookBasePath(
            basePath,
            relativePath,
            plannedFolderRelative,
            string.IsNullOrWhiteSpace(folderPattern),
            isCustomBasePath);
        return new ManualImportPathPlan(destinationPath, audiobookBasePath);
    }

    private static string ResolveAudiobookBasePath(
        string basePath,
        string relativePath,
        string? plannedFolderRelative,
        bool usesLegacyPattern,
        bool isCustomBasePath)
    {
        if (isCustomBasePath || string.IsNullOrWhiteSpace(basePath))
        {
            return basePath;
        }

        var relativeDirectory = usesLegacyPattern
            ? Path.GetDirectoryName(relativePath)
            : plannedFolderRelative;
        return string.IsNullOrWhiteSpace(relativeDirectory)
            ? basePath
            : CombineWithOptionalBase(basePath, relativeDirectory);
    }

    public static string CombineWithOptionalBase(string? basePath, string candidatePath)
    {
        return FileUtils.CombineWithOptionalBase(basePath, candidatePath);
    }

    public static List<ManualImportItemDto> BuildOrderedItems(
        IEnumerable<ManualImportItemDto> items,
        StringComparer sourcePathComparer)
    {
        var ordered = new List<ManualImportItemDto>();

        foreach (var validItems in items.GroupBy(i => i.MatchedAudiobookId).Select(g => g.Where(i => !string.IsNullOrWhiteSpace(i.FullPath)).ToList()))
        {
            if (validItems.Count == 0)
            {
                continue;
            }

            var plans = MultiFileImportPlanner.BuildPlans(
                validItems.Select(i => (i.FullPath!, string.IsNullOrWhiteSpace(i.RelativePath) ? null : i.RelativePath)),
                sourcePathComparer);
            var itemLookup = validItems.ToDictionary(i => i.FullPath!, sourcePathComparer);
            var diskNumbersForNaming = MultiFileImportPlanner.BuildStableNamingNumbers(plans, p => p.DiskNumberHint, sourcePathComparer);
            var chapterNumbersForNaming = MultiFileImportPlanner.BuildStableNamingNumbers(plans, p => p.ChapterNumberHint, sourcePathComparer);

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

    private static bool IsCustomBasePath(
        string basePath,
        string configuredOutput,
        List<RootFolder> rootFolders,
        FileSystemPathSemantics destinationSemantics)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(basePath))
            {
                return false;
            }

            if (!FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    basePath,
                    out var baseFull,
                    out _))
            {
                return true;
            }

            var configuredFull = string.Empty;
            var hasConfiguredOutput = !string.IsNullOrWhiteSpace(configuredOutput)
                && FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                    configuredOutput,
                    out configuredFull,
                    out _);
            var isCustomBasePath = !hasConfiguredOutput
                || !FileSystemPathIdentity.AreEquivalent(
                    baseFull,
                    configuredFull,
                    destinationSemantics);

            if (isCustomBasePath)
            {
                var isRootFolder = rootFolders.Any(root =>
                    FileSystemPathIdentity.TryCanonicalizeUnambiguousStoredAbsolutePathForHost(
                        root.Path,
                        out var rootPath,
                        out _)
                    && FileSystemPathIdentity.AreEquivalent(
                        rootPath,
                        baseFull,
                        destinationSemantics));
                if (isRootFolder) isCustomBasePath = false;
            }

            return isCustomBasePath;
        }
        catch (Exception caughtEx) when (caughtEx is not OperationCanceledException && caughtEx is not OutOfMemoryException && caughtEx is not StackOverflowException)
        {
            // When identity comparison cannot be completed, avoid falling back to host rules.
            return !string.IsNullOrWhiteSpace(basePath);
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
        // Ordinal-ignore-case to match RenameService.BuildNamingVariables. The token regex in
        // FileNamingService is already case-insensitive, so a case-sensitive dictionary here means
        // a pattern written {series} resolves under rename and misses under manual import.
        var variables = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

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
        // SeriesNumber and Quality were absent from this table entirely while both are present
        // in the rename and library-add tables, so a pattern using either rendered under those
        // and silently lost the segment here. Inserted on the same terms as their neighbours:
        // present when known, absent when not, so the missing-variable cleanup still applies.
        if (!string.IsNullOrWhiteSpace(audiobook.SeriesNumber)) variables["SeriesNumber"] = audiobook.SeriesNumber;
        if (!string.IsNullOrWhiteSpace(audiobook.Quality)) variables["Quality"] = audiobook.Quality;
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
