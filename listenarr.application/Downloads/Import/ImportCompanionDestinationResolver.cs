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

namespace Listenarr.Application.Downloads.Import;

/// <summary>
/// Decides where a companion file belongs relative to an audiobook base path.
///
/// An import batch can span disjoint trees. Archive extraction puts the archive's contents
/// under a temporary directory and leaves the files the release shipped loose in the download
/// directory, so the batch's common directory climbs to whatever those two have in common.
/// On the stock container layout that is the filesystem root, and relativizing against it
/// hands back each companion's own absolute path with the root stripped off, which no
/// containment check can reject; the source tree is then recreated inside the library folder.
/// On a host where the temporary directory and the download directory share a parent it is
/// that parent, which produces a shorter tree in the same place.
///
/// The batch never had one source structure to reproduce, so this resolver does not look for
/// one. Each extraction directory is its own root, the files that stayed in the download
/// directory share theirs, and a companion is mirrored only under the root it actually came
/// from. A companion that belongs to no root falls back to travelling with the audio file
/// imported out of its own directory, and is refused if there is no such file.
///
/// The manual import path calls <see cref="TryResolveBesideImportedFile"/> directly, because it
/// has no source structure worth reproducing at all: its audio destinations are
/// built from naming patterns and never carry the source's shape, so mirroring a companion's
/// source position gives the sidecar a structure the file it accompanies has just lost. Every
/// companion it sweeps up sits in the directory of a selected file, so the fallback is the whole
/// rule there. Readarr and Sonarr place extras the same way, in the imported file's own folder
/// (<c>src/NzbDrone.Core/Extras/Files/ExtraFileManager.cs</c>, <c>ImportFile</c>, in both).
///
/// Deciding containment in one place is deliberate; the same rule written twice is how this
/// class of defect survives being fixed once.
/// </summary>
public static class ImportCompanionDestinationResolver
{
    /// <summary>
    /// The source roots of one import batch: one per archive that was extracted, plus the
    /// directory the files that stayed behind share. Resolved once per batch, because the
    /// answer does not depend on which companion is being placed.
    /// </summary>
    /// <param name="ExtractionRoots">Temporary directories archives were extracted into.</param>
    /// <param name="UnextractedCommonDirectory">
    /// The common directory of the batch files that came from no archive, or null when the
    /// batch has none.
    /// </param>
    public sealed record CompanionSourceRoots(
        IReadOnlyList<string> ExtractionRoots,
        string? UnextractedCommonDirectory);

    /// <summary>
    /// A file the batch has already published: where it was read from, and where it landed.
    /// The two import paths carry their results in different types, so the fallback below is
    /// given this rather than either of them.
    /// </summary>
    /// <param name="SourcePath">The file's absolute source path.</param>
    /// <param name="FinalPath">The absolute path it was published to.</param>
    public sealed record ImportedFilePlacement(string SourcePath, string FinalPath);

    /// <summary>
    /// Works out the batch's source roots. Call this once per batch and reuse the result for
    /// every companion in it.
    /// </summary>
    public static CompanionSourceRoots ResolveRoots(
        IEnumerable<string> batchFiles,
        IEnumerable<string> extractionRoots,
        FileSystemPathSemantics? sourceSemantics)
    {
        var roots = extractionRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .ToList();
        var candidates = batchFiles.Where(file => !string.IsNullOrWhiteSpace(file));
        // Without resolved source semantics there is no sound way to ask whether a file sits
        // under an extraction root, and guessing at host defaults is what the path-identity
        // rules exist to stop. A batch in that state has no files to place anyway.
        if (sourceSemantics.HasValue)
        {
            candidates = candidates.Where(file => !roots.Any(root =>
                FileSystemPathIdentity.IsSameOrInside(file, root, sourceSemantics.Value)));
        }

        return new CompanionSourceRoots(roots, FileUtils.GetCommonDirectory(candidates));
    }

    /// <summary>
    /// The audio files an automatic import batch has published so far, in the shape the
    /// fallback below wants. The audio filter is applied here rather than inside the resolver
    /// because the automatic batch's results carry its companions too, while the manual batch's
    /// carry only the items the caller selected.
    /// </summary>
    /// <remarks>
    /// Deferred on purpose. The fallback is not reached for most companions, and the caller's
    /// results grow as the batch is imported, so materialising this per companion costs work
    /// nobody asked for. Deferring it also keeps the projection inside the resolver's own
    /// exception guard rather than in an argument list outside it. It closes over
    /// <paramref name="results"/>, so enumerate it where you build it: a caller that stores it
    /// and reads it later sees whatever the list holds then, and one that reads it while the
    /// list is being appended to gets an <see cref="InvalidOperationException"/>.
    /// </remarks>
    public static IEnumerable<ImportedFilePlacement> ImportedAudioFrom(
        IEnumerable<ImportResult> results) =>
        results
            .Where(result => result.Success
                && !string.IsNullOrWhiteSpace(result.SourcePath)
                && !string.IsNullOrWhiteSpace(result.FinalPath)
                && FileUtils.IsAudioFile(result.SourcePath!))
            .Select(result => new ImportedFilePlacement(result.SourcePath!, result.FinalPath!));

    /// <summary>
    /// Resolves the companion's path relative to <paramref name="basePath"/>, or returns false
    /// when the companion cannot be placed without inventing structure for it.
    /// </summary>
    public static bool TryResolveRelativeDestination(
        CompanionSourceRoots sourceRoots,
        string companionFile,
        string? basePath,
        IReadOnlyCollection<ImportResult> results,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics destinationSemantics,
        out string relativePath) =>
        TryResolveRelativeDestination(
            sourceRoots,
            companionFile,
            basePath,
            ImportedAudioFrom(results),
            sourceSemantics,
            destinationSemantics,
            out relativePath);

    /// <summary>
    /// Resolves the companion's path relative to <paramref name="basePath"/>, or returns false
    /// when the companion cannot be placed without inventing structure for it.
    /// </summary>
    public static bool TryResolveRelativeDestination(
        CompanionSourceRoots sourceRoots,
        string companionFile,
        string? basePath,
        IEnumerable<ImportedFilePlacement> importedFiles,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics destinationSemantics,
        out string relativePath)
    {
        relativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(companionFile) || string.IsNullOrWhiteSpace(basePath))
        {
            return false;
        }

        try
        {
            var companion = FileSystemPathIdentity.ResolveNativeAbsolutePath(companionFile);
            var sourceRoot = SelectSourceRoot(sourceRoots, companion, sourceSemantics);
            if (TryMirrorUnderSourceRoot(
                    sourceRoot,
                    companion,
                    sourceSemantics,
                    destinationSemantics,
                    out relativePath))
            {
                return true;
            }

            return TryPlaceBesideImportedFile(
                companion,
                basePath,
                importedFiles,
                sourceSemantics,
                destinationSemantics,
                out relativePath);
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            relativePath = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Places the companion beside the file imported out of its own source directory, without
    /// asking whether any source structure should be reproduced first. This is the whole rule for
    /// a caller whose destinations carry none of the source's shape, which is the manual import
    /// path; it asks for this by name rather than declaring an empty set of roots to make the
    /// mirror branch decline.
    /// </summary>
    public static bool TryResolveBesideImportedFile(
        string companionFile,
        string? basePath,
        IEnumerable<ImportedFilePlacement> importedFiles,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics destinationSemantics,
        out string relativePath)
    {
        relativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(companionFile) || string.IsNullOrWhiteSpace(basePath))
        {
            return false;
        }

        try
        {
            return TryPlaceBesideImportedFile(
                FileSystemPathIdentity.ResolveNativeAbsolutePath(companionFile),
                basePath,
                importedFiles,
                sourceSemantics,
                destinationSemantics,
                out relativePath);
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            relativePath = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// The root whose structure this companion's position is meaningful against: the archive
    /// it was extracted from, or the directory the batch's un-extracted files share.
    /// </summary>
    private static string? SelectSourceRoot(
        CompanionSourceRoots sourceRoots,
        string companion,
        FileSystemPathSemantics sourceSemantics)
    {
        return sourceRoots.ExtractionRoots.FirstOrDefault(root =>
                   FileSystemPathIdentity.IsSameOrInside(companion, root, sourceSemantics))
            ?? sourceRoots.UnextractedCommonDirectory;
    }

    /// <summary>
    /// Mirrors the companion's position under its own source root. A root that is a bare
    /// filesystem root describes no structure and is refused rather than mirrored. That is what
    /// <see cref="FileUtils.GetCommonDirectory(IEnumerable{string})"/> yields for inputs in
    /// disjoint Unix trees; on Windows the same inputs yield the first directory instead,
    /// because <see cref="FileUtils.GetCommonPathForDirectories(IEnumerable{string})"/> returns
    /// null across two drives and the caller substitutes one of them. So this check is a Unix
    /// backstop rather than a general one.
    /// </summary>
    private static bool TryMirrorUnderSourceRoot(
        string? sourceRootPath,
        string companion,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics destinationSemantics,
        out string relativePath)
    {
        relativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(sourceRootPath)
            || FileUtils.IsFilesystemRoot(sourceRootPath, sourceSemantics))
        {
            return false;
        }

        if (!FileSystemPathIdentity.TryGetRelativePathWithinBase(
                FileSystemPathIdentity.ResolveNativeAbsolutePath(sourceRootPath),
                companion,
                sourceSemantics,
                out var withinRoot)
            || string.IsNullOrEmpty(withinRoot))
        {
            return false;
        }

        relativePath = FileSystemPathIdentity.ConvertRelativePathSyntax(
            withinRoot,
            sourceSemantics.Syntax,
            destinationSemantics.Syntax);
        return true;
    }

    /// <summary>
    /// Whether an already-imported file was read out of the companion's own directory.
    ///
    /// Compared by path identity rather than by string, because the two sides reach here from
    /// different places and nothing in this class makes them agree on spelling. A companion's
    /// directory descends from <c>Path.GetFullPath</c>; an imported file's source path is
    /// whatever its caller put in the results, and
    /// <see cref="FileSystemPathIdentity.ResolveNativeAbsolutePath"/> hands back an
    /// already-qualified path untouched, so a <c>.</c> segment survives it and a string
    /// comparison calls two spellings of one directory different.
    ///
    /// Both of today's callers happen to be safe, and for a structural reason rather than a
    /// hygienic one: each draws both sides of this comparison from one list of source paths, so
    /// whatever spelling that list uses appears on both sides and still matches. Divergence would
    /// need one batch to spell one directory two ways. That is the callers' property, not this
    /// method's, and this method is public.
    ///
    /// A malformed entry is skipped rather than abandoning the search: canonicalisation throws on
    /// a path that does not fit the declared syntax, and one such entry should not cost the rest
    /// of the batch its companions.
    /// </summary>
    private static bool CameFromTheSameDirectory(
        ImportedFilePlacement imported,
        string companionDirectory,
        FileSystemPathSemantics sourceSemantics)
    {
        try
        {
            var importedDirectory = Path.GetDirectoryName(
                FileSystemPathIdentity.ResolveNativeAbsolutePath(imported.SourcePath));
            return !string.IsNullOrEmpty(importedDirectory)
                && FileSystemPathIdentity.AreEquivalent(
                    importedDirectory,
                    companionDirectory,
                    sourceSemantics);
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            return false;
        }
    }

    /// <summary>
    /// Places the companion, by name only, in the destination directory of a file that was
    /// imported out of the same source directory. A companion with no such neighbour has
    /// nowhere to go and is refused.
    /// </summary>
    private static bool TryPlaceBesideImportedFile(
        string companion,
        string basePath,
        IEnumerable<ImportedFilePlacement> importedFiles,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics destinationSemantics,
        out string relativePath)
    {
        relativePath = string.Empty;
        var companionDirectory = Path.GetDirectoryName(companion);
        if (string.IsNullOrWhiteSpace(companionDirectory))
        {
            return false;
        }

        var neighbour = importedFiles.FirstOrDefault(imported =>
            CameFromTheSameDirectory(imported, companionDirectory, sourceSemantics));
        if (neighbour == null)
        {
            return false;
        }

        var neighbourDirectory = Path.GetDirectoryName(
            FileSystemPathIdentity.ResolveNativeAbsolutePath(neighbour.FinalPath));
        if (string.IsNullOrWhiteSpace(neighbourDirectory)
            || !FileSystemPathIdentity.TryGetRelativePathWithinBase(
                basePath,
                neighbourDirectory,
                destinationSemantics,
                out var relativeDirectory))
        {
            return false;
        }

        var fileName = Path.GetFileName(companion);
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        var separator = destinationSemantics.Syntax == FileSystemPathSyntax.Windows ? '\\' : '/';
        relativePath = string.IsNullOrEmpty(relativeDirectory)
            ? fileName
            : relativeDirectory.TrimEnd('/', '\\') + separator + fileName;
        return true;
    }
}
