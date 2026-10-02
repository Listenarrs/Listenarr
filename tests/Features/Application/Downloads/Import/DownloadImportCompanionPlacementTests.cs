/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using System.IO.Compression;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Downloads.Import;

[Trait("Name", "DownloadImportCompanionPlacementTests")]
[Trait("Category", "DownloadProcessingJob")]
public sealed class DownloadImportCompanionPlacementTests : BaseTests
{
    private readonly List<string> _directoriesOutsideTemp = [];

    public override async Task InitializeAsync()
    {
        Init();
        await AddAuthorizedRootAsync(FileService.GetTempPath());
    }

    public override async Task DisposeAsync()
    {
        foreach (var directory in _directoriesOutsideTemp)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
            catch (IOException)
            {
                // The assertions are already done; a leftover scratch directory under the test
                // output folder is not worth failing a run over.
            }
        }

        await base.DisposeAsync();
    }

    /// <summary>
    /// The reported defect, end to end. Archive extraction is on by default, so a release that
    /// ships an archive beside loose sidecars produces a batch spanning the extraction
    /// directory and the download directory. Relativizing every companion against what those
    /// two happen to have in common recreated their absolute paths inside the book folder.
    /// </summary>
    [Fact]
    public async Task ImportDownloadFilesAsync_ArchiveBesideLooseCompanions_PlacesThemAllBesideTheAudio()
    {
        await _applicationSettingsRepository.SaveAsync(
            new ApplicationSettingsBuilder()
                .WithCopyFileOnCompleted()
                .WithExtractArchive()
                .WithoutMetadataProcessing()
                .Build());

        var basePath = FileService.GetTempDirectory("archive-library");
        var releaseDirectory = FileService.GetTempDirectory(
            Path.Join("archive-downloads", "The.Release"));

        var stagingDirectory = FileService.GetTempDirectory("archive-staging");
        await FileService.GetFileAsync(stagingDirectory, "release.mp3");
        await FileService.GetFileAsync(stagingDirectory, "inner.nfo");
        var archivePath = Path.Join(releaseDirectory, "release.zip");
        ZipFile.CreateFromDirectory(stagingDirectory, archivePath);
        Directory.Delete(stagingDirectory, true);

        var looseNfo = await FileService.GetFileAsync(releaseDirectory, "book.nfo");
        var looseM3u = await FileService.GetFileAsync(releaseDirectory, "book.m3u");
        var looseNotes = await FileService.GetFileAsync(releaseDirectory, "reader-notes.txt");

        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithBasePath(basePath)
                .Build());

        var results = await _provider
            .GetRequiredService<IDownloadImportService>()
            .ImportDownloadFilesAsync(
                audiobook,
                [archivePath, looseNfo, looseM3u, looseNotes]);

        // MUST ARRIVE: without this a clean library folder would be indistinguishable from an
        // import that never ran.
        Assert.True(
            File.Exists(Path.Join(basePath, "release.mp3")),
            "The audio file inside the archive did not reach its pattern-named destination.");

        // The defect's signature: directories under the book folder mirroring the absolute
        // paths the batch came from.
        var strayDirectories = Directory.GetDirectories(basePath);
        Assert.True(
            strayDirectories.Length == 0,
            "The book folder gained directories mirroring the source tree: "
                + string.Join(", ", strayDirectories));

        // Every companion still arrives, from inside the archive and from beside it alike.
        foreach (var companion in new[] { "inner.nfo", "book.nfo", "book.m3u", "reader-notes.txt" })
        {
            Assert.True(
                File.Exists(Path.Join(basePath, companion)),
                $"{companion} was not placed beside the audio file.");
        }

        // A companion that cannot be placed is skipped rather than failed, because a failed
        // result fails the whole download-processing job. Nothing here should be either.
        Assert.DoesNotContain(results, result => !result.Success);
    }

    /// <summary>
    /// The backstop, for a batch that spans disjoint trees without any archive to explain it.
    /// The common directory is then the filesystem root, which describes no structure, so the
    /// companion travels with the audio imported from its own directory or is refused.
    /// </summary>
    [DisjointFilesystemRootsFact]
    public async Task ImportDownloadFilesAsync_BatchSpanningDisjointRoots_DoesNotMirrorTheSourceTree()
    {
        await _applicationSettingsRepository.SaveAsync(
            new ApplicationSettingsBuilder()
                .WithCopyFileOnCompleted()
                .WithoutExtractArchive()
                .WithoutMetadataProcessing()
                .Build());

        var basePath = FileService.GetTempDirectory("disjoint-library");
        var otherRootDirectory = CreateDirectoryOutsideTempRoot("unpacked");
        var looseDirectory = FileService.GetTempDirectory(
            Path.Join("disjoint-downloads", "The.Release"));

        var audioSource = await FileService.GetFileAsync(otherRootDirectory, "release.mp3");
        var neighbourCompanion = await FileService.GetFileAsync(otherRootDirectory, "inner.nfo");
        var strandedCompanion = await FileService.GetFileAsync(looseDirectory, "book.nfo");

        // The fixture only means something if the batch really has no common ancestor but the
        // filesystem root. Assert it rather than assume it.
        var commonDirectory = FileUtils.GetCommonDirectory(
            [audioSource, neighbourCompanion, strandedCompanion]);
        Assert.Equal(Path.GetPathRoot(audioSource), commonDirectory);

        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithBasePath(basePath)
                .Build());

        var results = await _provider
            .GetRequiredService<IDownloadImportService>()
            .ImportDownloadFilesAsync(
                audiobook,
                [audioSource, neighbourCompanion, strandedCompanion]);

        Assert.True(
            File.Exists(Path.Join(basePath, "release.mp3")),
            "The audio file did not reach its pattern-named destination.");

        var strayDirectories = Directory.GetDirectories(basePath);
        Assert.True(
            strayDirectories.Length == 0,
            "The book folder gained directories mirroring the source tree: "
                + string.Join(", ", strayDirectories));

        Assert.True(
            File.Exists(Path.Join(basePath, "inner.nfo")),
            "The companion beside the imported audio was not placed next to it.");

        // The companion from the other tree has no audio to travel with, so it is skipped,
        // with its name in the message, rather than failing the import.
        Assert.False(File.Exists(Path.Join(basePath, "book.nfo")));
        Assert.Contains(results, result =>
            result.Success
            && result.SourcePath == null
            && result.Message != null
            && result.Message.Contains("book.nfo", StringComparison.Ordinal));
        Assert.DoesNotContain(results, result => !result.Success);
    }

    /// <summary>
    /// The control that proves the fix did not simply refuse every companion: a batch that
    /// really does share one source directory still imports its companions, flat.
    /// </summary>
    [Fact]
    public async Task ImportDownloadFilesAsync_BatchUnderOneSourceDirectory_PlacesCompanionsBesideTheAudio()
    {
        await _applicationSettingsRepository.SaveAsync(
            new ApplicationSettingsBuilder()
                .WithCopyFileOnCompleted()
                .WithoutExtractArchive()
                .WithoutMetadataProcessing()
                .Build());

        var basePath = FileService.GetTempDirectory("single-root-library");
        var sourceDirectory = FileService.GetTempDirectory(
            Path.Join("single-root-downloads", "The.Release"));
        var audioSource = await FileService.GetFileAsync(sourceDirectory, "release.mp3");
        var coverSource = await FileService.GetFileAsync(sourceDirectory, "cover.jpg");
        var nfoSource = await FileService.GetFileAsync(sourceDirectory, "book.nfo");

        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithBasePath(basePath)
                .Build());

        await _provider
            .GetRequiredService<IDownloadImportService>()
            .ImportDownloadFilesAsync(
                audiobook,
                [audioSource, coverSource, nfoSource]);

        Assert.True(File.Exists(Path.Join(basePath, "release.mp3")));
        Assert.True(File.Exists(Path.Join(basePath, "cover.jpg")));
        Assert.True(File.Exists(Path.Join(basePath, "book.nfo")));
        Assert.Empty(Directory.GetDirectories(basePath));
    }

    /// <summary>
    /// The control that proves the fix does not flatten indiscriminately: a companion that
    /// genuinely sits one directory below the audio file keeps that relationship, because its
    /// source root is a real directory whose structure means something.
    /// </summary>
    [Fact]
    public async Task ImportDownloadFilesAsync_CompanionBelowTheAudioDirectory_KeepsItsSubdirectory()
    {
        await _applicationSettingsRepository.SaveAsync(
            new ApplicationSettingsBuilder()
                .WithCopyFileOnCompleted()
                .WithoutExtractArchive()
                .WithoutMetadataProcessing()
                .Build());

        var basePath = FileService.GetTempDirectory("nested-library");
        var sourceDirectory = FileService.GetTempDirectory(
            Path.Join("nested-downloads", "The.Release"));
        var extrasDirectory = FileService.GetTempDirectory(
            Path.Join("nested-downloads", "The.Release", "extras"));
        var audioSource = await FileService.GetFileAsync(sourceDirectory, "release.mp3");
        var nestedCompanion = await FileService.GetFileAsync(extrasDirectory, "cover.jpg");

        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithBasePath(basePath)
                .Build());

        await _provider
            .GetRequiredService<IDownloadImportService>()
            .ImportDownloadFilesAsync(
                audiobook,
                [audioSource, nestedCompanion]);

        Assert.True(File.Exists(Path.Join(basePath, "release.mp3")));
        Assert.True(
            File.Exists(Path.Join(basePath, "extras", "cover.jpg")),
            "A companion one directory below the audio file lost its subdirectory.");
    }

    /// <summary>
    /// Creates a scratch directory outside the temp tree the fixture uses, so that a batch can
    /// span two trees whose only common ancestor is the filesystem root.
    /// </summary>
    private string CreateDirectoryOutsideTempRoot(string name)
    {
        var directory = Path.Join(
            AppContext.BaseDirectory,
            $"companion-placement-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _directoriesOutsideTemp.Add(directory);
        return directory;
    }
}
