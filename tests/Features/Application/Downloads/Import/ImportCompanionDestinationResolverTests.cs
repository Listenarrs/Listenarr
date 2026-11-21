/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Downloads.Import;

[Trait("Name", "ImportCompanionDestinationResolverTests")]
[Trait("Category", "DownloadProcessingJob")]
public sealed class ImportCompanionDestinationResolverTests : BaseTests
{
    // Paths are built from the host's own filesystem root and joined with the host's
    // separator, because ResolveNativeAbsolutePath and Path.GetDirectoryName follow host
    // rules while FileSystemPathSemantics.Syntax is declared. Hard-coded Unix literals pass
    // on Linux and throw on Windows, which is the trap this avoids.
    private static readonly FileSystemPathSemantics HostSemantics = new(
        OperatingSystem.IsWindows()
            ? FileSystemPathSyntax.Windows
            : FileSystemPathSyntax.Unix,
        OperatingSystem.IsWindows()
            ? FileSystemCaseSensitivity.Insensitive
            : FileSystemCaseSensitivity.Sensitive);

    private static readonly string FilesystemRoot =
        Path.GetPathRoot(AppContext.BaseDirectory)
        ?? throw new InvalidOperationException("The host filesystem root is unavailable.");

    private static readonly string ExtractionRoot =
        Under("tmp", "listenarr-extract", "0e3cec09");
    private static readonly string DownloadDirectory =
        Under("data", "dl", "completed", "books", "The.Release");
    private static readonly string BasePath =
        Under("library", "Arthur Conan Doyle", "The Valley of Fear");

    private static string Under(params string[] segments) =>
        Path.Join([FilesystemRoot, .. segments]);

    private static ImportCompanionDestinationResolver.CompanionSourceRoots Roots(
        IReadOnlyCollection<string> batchFiles,
        IReadOnlyCollection<string> extractionRoots) =>
        ImportCompanionDestinationResolver.ResolveRoots(
            batchFiles,
            extractionRoots,
            HostSemantics);

    /// <summary>The batch a release with one archive and three loose sidecars produces.</summary>
    private static IReadOnlyCollection<string> ArchiveBatch() =>
    [
        Path.Join(ExtractionRoot, "release.m4b"),
        Path.Join(ExtractionRoot, "inner.nfo"),
        Path.Join(DownloadDirectory, "book.nfo"),
        Path.Join(DownloadDirectory, "book.m3u")
    ];

    /// <summary>The audio import, as the service records it once the file is published.</summary>
    private static IReadOnlyCollection<ImportResult> ExtractedAudioImported() =>
    [
        ImportResult.ImportSuccess(
            FileAction.Copy,
            Path.Join(ExtractionRoot, "release.m4b"),
            Path.Join(BasePath, "The Valley of Fear.m4b"))
    ];

    [Fact]
    public void CompanionFromAnArchive_IsMirroredUnderItsOwnExtractionRoot()
    {
        var resolved = ImportCompanionDestinationResolver.TryResolveRelativeDestination(
            Roots(ArchiveBatch(), [ExtractionRoot]),
            Path.Join(ExtractionRoot, "inner.nfo"),
            BasePath,
            ExtractedAudioImported(),
            HostSemantics,
            HostSemantics,
            out var relativePath);

        Assert.True(resolved);
        Assert.Equal("inner.nfo", relativePath);
    }

    /// <summary>
    /// The companion that stayed in the download directory. Relativizing it against what the
    /// extraction directory and the download directory have in common is what produced the
    /// stray tree; its own directory is the only root its position means anything under.
    /// </summary>
    [Fact]
    public void CompanionLeftInTheDownloadDirectory_IsMirroredUnderThatDirectory()
    {
        var resolved = ImportCompanionDestinationResolver.TryResolveRelativeDestination(
            Roots(ArchiveBatch(), [ExtractionRoot]),
            Path.Join(DownloadDirectory, "book.nfo"),
            BasePath,
            ExtractedAudioImported(),
            HostSemantics,
            HostSemantics,
            out var relativePath);

        Assert.True(resolved);
        Assert.Equal("book.nfo", relativePath);
    }

    [Fact]
    public void CompanionBelowTheDownloadDirectory_KeepsItsSubdirectory()
    {
        var companion = Path.Join(DownloadDirectory, "extras", "cover.jpg");
        var resolved = ImportCompanionDestinationResolver.TryResolveRelativeDestination(
            Roots([.. ArchiveBatch(), companion], [ExtractionRoot]),
            companion,
            BasePath,
            ExtractedAudioImported(),
            HostSemantics,
            HostSemantics,
            out var relativePath);

        Assert.True(resolved);
        Assert.Equal(Path.Join("extras", "cover.jpg"), relativePath);
    }

    /// <summary>
    /// The control for the archive cases: with no extraction in the batch at all, the whole
    /// batch shares one directory and the behaviour is what it always was.
    /// </summary>
    [Fact]
    public void NoArchiveInTheBatch_CompanionUsesTheOneSourceDirectory()
    {
        var resolved = ImportCompanionDestinationResolver.TryResolveRelativeDestination(
            Roots([Path.Join(DownloadDirectory, "release.m4b"), Path.Join(DownloadDirectory, "book.nfo")], []),
            Path.Join(DownloadDirectory, "book.nfo"),
            BasePath,
            ExtractedAudioImported(),
            HostSemantics,
            HostSemantics,
            out var relativePath);

        Assert.True(resolved);
        Assert.Equal("book.nfo", relativePath);
    }

    /// <summary>
    /// A batch that spans disjoint trees with no archive to account for it. The common
    /// directory is the filesystem root, which describes nothing, so the companion travels
    /// with the audio imported out of its own directory instead.
    /// </summary>
    [Fact]
    public void BatchWithNoCommonDirectoryButTheFilesystemRoot_UsesTheImportedAudioDestination()
    {
        var resolved = ImportCompanionDestinationResolver.TryResolveRelativeDestination(
            Roots(ArchiveBatch(), []),
            Path.Join(ExtractionRoot, "inner.nfo"),
            BasePath,
            ExtractedAudioImported(),
            HostSemantics,
            HostSemantics,
            out var relativePath);

        Assert.True(resolved);
        Assert.Equal("inner.nfo", relativePath);
    }

    [Fact]
    public void BatchWithNoCommonDirectoryButTheFilesystemRoot_CompanionWithNoNeighbour_IsRefused()
    {
        var resolved = ImportCompanionDestinationResolver.TryResolveRelativeDestination(
            Roots(ArchiveBatch(), []),
            Path.Join(DownloadDirectory, "book.nfo"),
            BasePath,
            ExtractedAudioImported(),
            HostSemantics,
            HostSemantics,
            out var relativePath);

        Assert.False(resolved);
        Assert.Equal(string.Empty, relativePath);
    }

    /// <summary>
    /// The fallback finds its neighbour by path identity, not by string comparison. Both of
    /// today's callers canonicalise their source paths before the results are built, so this is
    /// the guarantee the resolver makes on its own account rather than a defect either of them
    /// can currently reach; it is public API, and a string comparison here would refuse a
    /// companion over a <c>.</c> segment somebody else's caller put in a path.
    /// </summary>
    [Fact]
    public void BesideImportedFile_MatchesTheNeighbourByPathIdentityNotSpelling()
    {
        var companion = Path.Join(DownloadDirectory, "book.nfo");
        var audio = Path.Join(DownloadDirectory, "release.m4b");
        var withDotSegment = Path.Join(
            Path.GetDirectoryName(DownloadDirectory)!,
            ".",
            Path.GetFileName(DownloadDirectory),
            "release.m4b");
        var elsewhere = Path.Join(ExtractionRoot, "release.m4b");

        (bool Resolved, string RelativePath) Resolve(string neighbourSourcePath)
        {
            var resolved = ImportCompanionDestinationResolver.TryResolveBesideImportedFile(
                companion,
                BasePath,
                [new ImportCompanionDestinationResolver.ImportedFilePlacement(
                    neighbourSourcePath,
                    Path.Join(BasePath, "The Valley of Fear.m4b"))],
                HostSemantics,
                HostSemantics,
                out var relativePath);
            return (resolved, relativePath);
        }

        Assert.NotEqual(audio, withDotSegment);
        Assert.Equal((true, "book.nfo"), Resolve(withDotSegment));
        Assert.Equal((true, "book.nfo"), Resolve(audio));
        Assert.Equal((false, string.Empty), Resolve(elsewhere));
    }

    /// <summary>
    /// The automatic batch's results carry its already-imported companions as well as its audio,
    /// and a companion is not something to place another companion beside. The manual path has
    /// no such entries, which is why the filter lives in this projection rather than in the
    /// fallback both paths share.
    /// </summary>
    [Fact]
    public void ImportedAudioFrom_KeepsOnlyTheSuccessfulAudioImports()
    {
        IReadOnlyCollection<ImportResult> results =
        [
            ImportResult.ImportSuccess(
                FileAction.Copy,
                Path.Join(DownloadDirectory, "release.m4b"),
                Path.Join(BasePath, "The Valley of Fear.m4b")),
            ImportResult.ImportSuccess(
                FileAction.Copy,
                Path.Join(DownloadDirectory, "book.nfo"),
                Path.Join(BasePath, "book.nfo")),
            ImportResult.ImportFailure(
                FileAction.Copy,
                Path.Join(DownloadDirectory, "failed.m4b"),
                BasePath)
        ];

        var imported = ImportCompanionDestinationResolver.ImportedAudioFrom(results);

        Assert.Equal(
            [Path.Join(DownloadDirectory, "release.m4b")],
            imported.Select(placement => placement.SourcePath));
    }

    /// <summary>
    /// A companion outside every root in the batch is refused outright rather than described
    /// with a traversing relative path. <c>Path.GetRelativePath</c>, which this replaced, is a
    /// total function: it answered this case with <c>../../../etc/passwd</c> and left the
    /// refusal to the destination guard, which is the guard that the filesystem-root case
    /// slipped past.
    /// </summary>
    [Fact]
    public void CompanionOutsideEveryRootInTheBatch_IsRefused()
    {
        var resolved = ImportCompanionDestinationResolver.TryResolveRelativeDestination(
            Roots([Path.Join(DownloadDirectory, "release.m4b"), Path.Join(DownloadDirectory, "book.nfo")], []),
            Under("etc", "passwd"),
            BasePath,
            ExtractedAudioImported(),
            HostSemantics,
            HostSemantics,
            out var relativePath);

        Assert.False(resolved);
        Assert.Equal(string.Empty, relativePath);
    }

    [Fact]
    public void ImportedAudioOutsideTheBasePath_DoesNotDragTheCompanionOutWithIt()
    {
        IReadOnlyCollection<ImportResult> results =
        [
            ImportResult.ImportSuccess(
                FileAction.Copy,
                Path.Join(ExtractionRoot, "release.m4b"),
                Under("elsewhere", "The Valley of Fear.m4b"))
        ];

        var resolved = ImportCompanionDestinationResolver.TryResolveRelativeDestination(
            Roots(ArchiveBatch(), []),
            Path.Join(ExtractionRoot, "inner.nfo"),
            BasePath,
            results,
            HostSemantics,
            HostSemantics,
            out var relativePath);

        Assert.False(resolved);
        Assert.Equal(string.Empty, relativePath);
    }

    /// <summary>
    /// The audio destination sits in a subdirectory of the base path whenever the file naming
    /// pattern carries a disk or chapter number. A companion travelling with it follows it
    /// there rather than being dropped at the base path.
    /// </summary>
    [Fact]
    public void ImportedAudioInASubdirectory_TakesTheCompanionWithIt()
    {
        IReadOnlyCollection<ImportResult> results =
        [
            ImportResult.ImportSuccess(
                FileAction.Copy,
                Path.Join(ExtractionRoot, "release.m4b"),
                Path.Join(BasePath, "Disc 1", "The Valley of Fear.m4b"))
        ];

        var resolved = ImportCompanionDestinationResolver.TryResolveRelativeDestination(
            Roots(ArchiveBatch(), []),
            Path.Join(ExtractionRoot, "inner.nfo"),
            BasePath,
            results,
            HostSemantics,
            HostSemantics,
            out var relativePath);

        Assert.True(resolved);
        Assert.Equal(Path.Join("Disc 1", "inner.nfo"), relativePath);
    }

    /// <summary>
    /// Whatever the resolver returns still passes through the destination guard. This is the
    /// control showing the guard is untouched and still rejects the traversal the resolver now
    /// declines to produce.
    /// </summary>
    [Fact]
    public void DestinationGuard_StillRejectsATraversingRelativePath()
    {
        var planner = new ImportDestinationPlanner(
            Mock.Of<IFileSystem>(),
            Mock.Of<IFilePublicationSourceCapability>());
        var unix = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);

        Assert.False(planner.TryResolve("/library/Book", "../escaped.nfo", unix, out _));
        Assert.False(planner.TryResolve("/library/Book", "/etc/passwd", unix, out _));
        Assert.True(planner.TryResolve("/library/Book", "inner.nfo", unix, out var allowed));
        Assert.Equal("/library/Book/inner.nfo", allowed);
    }
}
