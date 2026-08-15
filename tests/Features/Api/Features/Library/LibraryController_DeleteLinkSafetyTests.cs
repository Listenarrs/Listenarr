/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Diagnostics;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Features.Library;

[Trait("Area", "LibraryApi")]
[Trait("Name", "LibraryController_DeleteLinkSafetyTests")]
[Trait("Category", "LibraryController")]
public class LibraryController_DeleteLinkSafetyTests : BaseTests
{
    [DirectoryLinkFact]
    public async Task FilesystemDelete_LinkedDirectoryDoesNotDeleteExternalFiles()
    {
        var tempRoot = FileService.GetTempDirectory("listenarr-delete-link-root");
        var bookFolder = Path.Join(tempRoot, "Book");
        var externalFolder = FileService.GetTempDirectory("listenarr-delete-link-external");
        var localFile = Path.Join(bookFolder, "book.m4b");
        var externalFile = Path.Join(externalFolder, "external.txt");
        var linkedDirectory = Path.Join(bookFolder, "linked");
        Directory.CreateDirectory(bookFolder);
        await File.WriteAllTextAsync(localFile, "audio");
        await File.WriteAllTextAsync(externalFile, "external");
        await AddAuthorizedRootAsync(tempRoot);

        Assert.True(
            TryCreateDirectoryLink(linkedDirectory, externalFolder),
            "The required directory link could not be created.");

        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Linked Book")
            .WithBasePath(bookFolder)
            .WithFilePath(localFile)
            .Build());
        await AddTrackedGenerationAsync(audiobook, localFile);

        var service = _provider.GetRequiredService<IAudiobookFilesystemDeleteService>();
        var result = await service.DeleteAsync(audiobook, deleteFolder: true);

        Assert.True(File.Exists(externalFile));
        Assert.True(File.Exists(localFile));
        Assert.True(Directory.Exists(bookFolder));
        Assert.False(result.DeletedFolder);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("symbolic link", StringComparison.OrdinalIgnoreCase)
            || warning.Contains("reparse point", StringComparison.OrdinalIgnoreCase));
        Directory.Delete(linkedDirectory, recursive: false);
    }

    [FileLinkFact]
    public async Task FilesystemDelete_LinkedFileDoesNotDeleteExternalFile()
    {
        var tempRoot = FileService.GetTempDirectory("listenarr-delete-file-link-root");
        var bookFolder = Path.Join(tempRoot, "Book");
        var externalFolder = FileService.GetTempDirectory("listenarr-delete-file-link-external");
        var localFile = Path.Join(bookFolder, "book.m4b");
        var externalFile = Path.Join(externalFolder, "external.txt");
        var linkedFile = Path.Join(bookFolder, "linked.txt");
        Directory.CreateDirectory(bookFolder);
        await File.WriteAllTextAsync(localFile, "audio");
        await File.WriteAllTextAsync(externalFile, "external");
        await AddAuthorizedRootAsync(tempRoot);

        try
        {
            File.CreateSymbolicLink(linkedFile, externalFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new Xunit.Sdk.XunitException(
                $"This native filesystem test requires symbolic-link support: {exception.Message}");
        }

        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Linked File Book")
            .WithBasePath(bookFolder)
            .WithFilePath(localFile)
            .Build());
        await AddTrackedGenerationAsync(audiobook, localFile);

        var service = _provider.GetRequiredService<IAudiobookFilesystemDeleteService>();
        var result = await service.DeleteAsync(audiobook, deleteFolder: true);

        Assert.True(File.Exists(externalFile));
        Assert.True(File.Exists(localFile));
        Assert.True(File.Exists(linkedFile));
        Assert.False(result.DeletedFolder);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("symbolic link", StringComparison.OrdinalIgnoreCase)
            || warning.Contains("reparse point", StringComparison.OrdinalIgnoreCase));
    }

    [DirectoryLinkFact]
    public async Task FilesystemDelete_ParentReplacedAfterValidation_PreservesBothGenerations()
    {
        var tempRoot = FileService.GetTempDirectory("listenarr-delete-parent-race");
        var bookFolder = Path.Join(tempRoot, "Book");
        var displacedFolder = Path.Join(tempRoot, "Book-displaced");
        var externalFolder = FileService.GetTempDirectory("listenarr-delete-parent-race-external");
        var localFile = Path.Join(bookFolder, "book.m4b");
        var displacedFile = Path.Join(displacedFolder, "book.m4b");
        var externalFile = Path.Join(externalFolder, "book.m4b");
        Directory.CreateDirectory(bookFolder);
        await File.WriteAllTextAsync(localFile, "owned audio");
        await AddAuthorizedRootAsync(tempRoot);
        await File.WriteAllTextAsync(externalFile, "external audio");

        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Replacement Race Book")
            .WithBasePath(bookFolder)
            .WithFilePath(localFile)
            .Build());
        await AddTrackedGenerationAsync(audiobook, localFile);

        var replaced = false;
        using var hook = ExclusiveDirectoryCreator.PushBeforeOpenParentHook(path =>
        {
            if (replaced || !string.Equals(path, bookFolder, StringComparison.Ordinal))
            {
                return;
            }

            Directory.Move(bookFolder, displacedFolder);
            if (!TryCreateDirectoryLink(bookFolder, externalFolder))
            {
                Directory.Move(displacedFolder, bookFolder);
                Assert.Fail("The required directory link could not be created.");
            }

            replaced = true;
        });

        var service = _provider.GetRequiredService<IAudiobookFilesystemDeleteService>();
        var result = await service.DeleteAsync(audiobook, deleteFolder: true);

        Assert.True(
            replaced,
            "The filesystem deletion path did not reach the replacement hook.");
        Assert.True(File.Exists(displacedFile));
        Assert.Equal("owned audio", await File.ReadAllTextAsync(displacedFile));
        Assert.True(File.Exists(externalFile));
        Assert.Equal("external audio", await File.ReadAllTextAsync(externalFile));
        Assert.False(result.DeletedFolder);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("delete", StringComparison.OrdinalIgnoreCase));
        Directory.Delete(bookFolder, recursive: false);
        Directory.Move(displacedFolder, bookFolder);
    }

    [Fact]
    public async Task FilesystemDelete_AuthorizedRootReplacedAfterEnumeration_PreservesReplacementTree()
    {
        var tempRoot = FileService.GetTempDirectory(
            "listenarr-delete-generation-race");
        var bookFolder = Path.Join(tempRoot, "Book");
        var displacedFolder = Path.Join(tempRoot, "Book-displaced");
        var localFile = Path.Join(bookFolder, "book.m4b");
        var displacedFile = Path.Join(displacedFolder, "book.m4b");
        Directory.CreateDirectory(bookFolder);
        await File.WriteAllTextAsync(localFile, "owned audio");
        await AddAuthorizedRootAsync(tempRoot);

        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Generation Race Book")
                .WithBasePath(bookFolder)
                .WithFilePath(localFile)
                .Build());
        await AddTrackedGenerationAsync(audiobook, localFile);

        var replaced = false;
        using var hook = ExclusiveDirectoryCreator.PushBeforeOpenParentHook(path =>
        {
            if (replaced
                || !string.Equals(path, localFile, StringComparison.Ordinal))
            {
                return;
            }

            Directory.Move(bookFolder, displacedFolder);
            Directory.CreateDirectory(bookFolder);
            File.WriteAllText(localFile, "replacement audio");
            replaced = true;
        });

        var service =
            _provider.GetRequiredService<IAudiobookFilesystemDeleteService>();
        var result = await service.DeleteAsync(audiobook, deleteFolder: true);

        Assert.True(replaced);
        Assert.Equal("owned audio", await File.ReadAllTextAsync(displacedFile));
        Assert.Equal("replacement audio", await File.ReadAllTextAsync(localFile));
        Assert.False(result.DeletedFolder);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("generation", StringComparison.OrdinalIgnoreCase)
            || warning.Contains("delete", StringComparison.OrdinalIgnoreCase));

        Directory.Delete(bookFolder, recursive: true);
        Directory.Move(displacedFolder, bookFolder);
    }

    [Fact]
    public async Task FilesystemDelete_FolderReplacedByFile_DoesNotReportSuccess()
    {
        var tempRoot = FileService.GetTempDirectory("listenarr-delete-folder-file-race");
        var bookFolder = Path.Join(tempRoot, "Book");
        var displacedFolder = Path.Join(tempRoot, "Book-displaced");
        var localFile = Path.Join(bookFolder, "book.m4b");
        var displacedFile = Path.Join(displacedFolder, "book.m4b");
        Directory.CreateDirectory(bookFolder);
        await File.WriteAllTextAsync(localFile, "owned audio");
        await AddAuthorizedRootAsync(tempRoot);

        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Folder File Race Book")
            .WithBasePath(bookFolder)
            .WithFilePath(localFile)
            .Build());
        await AddTrackedGenerationAsync(audiobook, localFile);

        var replaced = false;
        using var hook = ExclusiveDirectoryCreator.PushBeforeOpenParentHook(path =>
        {
            if (replaced || !string.Equals(path, bookFolder, StringComparison.Ordinal))
            {
                return;
            }

            Directory.Move(bookFolder, displacedFolder);
            File.WriteAllText(bookFolder, "replacement");
            replaced = true;
        });

        var service = _provider.GetRequiredService<IAudiobookFilesystemDeleteService>();
        var result = await service.DeleteAsync(audiobook, deleteFolder: true);

        Assert.True(replaced);
        Assert.True(File.Exists(displacedFile));
        Assert.Equal("owned audio", await File.ReadAllTextAsync(displacedFile));
        Assert.True(File.Exists(bookFolder));
        Assert.Equal("replacement", await File.ReadAllTextAsync(bookFolder));
        Assert.False(result.DeletedFolder);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("delete", StringComparison.OrdinalIgnoreCase));
        File.Delete(bookFolder);
        Directory.Move(displacedFolder, bookFolder);
    }

    private async Task AddTrackedGenerationAsync(
        Audiobook audiobook,
        string path)
    {
        var tracked = new AudiobookFileBuilder()
            .WithAudiobook(audiobook)
            .WithPath(path)
            .Build();
        using (var lease = PinnedAudiobookFileRegistrationLease.Open(path))
        {
            tracked.ApplyPhysicalObjectIdentity(
                lease.PhysicalObjectIdentity,
                DateTime.UtcNow);
        }

        await _audiobookFileRepository.AddAsync(tracked);
    }

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /c mklink /J \"{linkPath}\" \"{targetPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            process?.WaitForExit();
            return process?.ExitCode == 0 && Directory.Exists(linkPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
