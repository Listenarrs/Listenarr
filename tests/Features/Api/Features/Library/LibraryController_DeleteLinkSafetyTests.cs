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
    [WindowsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FilesystemDelete_FailureAfterCapture_ReleasesOriginalHandles(bool cancelled)
    {
        // Given: all original file handles were captured before preflight fails.
        Init();
        var root = FileService.GetTempDirectory("delete-proof-disposal");
        var folder = Path.Join(root, "Book");
        Directory.CreateDirectory(folder);
        var source = Path.Join(folder, "book.m4b");
        var displaced = Path.Join(root, "retained.m4b");
        await File.WriteAllTextAsync(source, "audio");
        await AddAuthorizedRootAsync(root);
        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Delete Proof Disposal")
            .WithBasePath(folder)
            .WithFilePath(source)
            .Build());
        await AddTrackedGenerationAsync(audiobook, source);
        var service = Assert.IsType<AudiobookFilesystemDeleteService>(
            _provider.GetRequiredService<IAudiobookFilesystemDeleteService>());
        using var cancellation = new CancellationTokenSource();
        service.AfterTrackedContentCaptureForTest = () =>
        {
            if (cancelled)
            {
                cancellation.Cancel();
            }
            else
            {
                throw new InvalidOperationException("Injected preflight failure.");
            }
        };

        // When: an exception or request cancellation aborts before any deletion.
        if (cancelled)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.DeleteAsync(audiobook, deleteFolder: false, cancellation.Token));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.DeleteAsync(audiobook, deleteFolder: false));
        }

        // Then: no retained Windows handle prevents the user from moving/writing the file.
        File.Move(source, displaced);
        Assert.Equal("audio", await File.ReadAllTextAsync(displaced));
        await File.WriteAllTextAsync(displaced, "changed");
        Assert.Equal("changed", await File.ReadAllTextAsync(displaced));
    }

    [Fact]
    public async Task FilesystemDelete_UnchangedLiveSource_CompletesCleanup()
    {
        Init();
        var root = FileService.GetTempDirectory("delete-original-live-source");
        var folder = Path.Join(root, "Book");
        Directory.CreateDirectory(folder);
        var source = Path.Join(folder, "book.m4b");
        await File.WriteAllTextAsync(source, "audio");
        await AddAuthorizedRootAsync(root);
        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Original Live Delete")
            .WithBasePath(folder)
            .WithFilePath(source)
            .Build());
        await AddTrackedGenerationAsync(audiobook, source);
        var service = _provider.GetRequiredService<IAudiobookFilesystemDeleteService>();

        var result = await service.DeleteAsync(audiobook, deleteFolder: false);

        Assert.True(result.TrackedFileCleanupComplete, string.Join("; ", result.Warnings));
        Assert.False(File.Exists(source));
        Assert.Equal(1, result.DeletedFiles);
    }

    [LinuxTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FilesystemDelete_SameBytesReplacementAfterContentCapture_RetainsBothFiles(
        bool deleteFolder)
    {
        // Given: the replacement has exactly the captured content, but is a new object.
        Init();
        var root = FileService.GetTempDirectory("delete-live-proof-replacement");
        var folder = Path.Join(root, "Book");
        Directory.CreateDirectory(folder);
        var source = Path.Join(folder, "book.m4b");
        var displaced = Path.Join(root, "original.m4b");
        await File.WriteAllTextAsync(source, "audio");
        await AddAuthorizedRootAsync(root);
        var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
            .WithTitle("Live Delete Proof")
            .WithBasePath(folder)
            .WithFilePath(source)
            .Build());
        await AddTrackedGenerationAsync(audiobook, source);
        var service = Assert.IsType<AudiobookFilesystemDeleteService>(
            _provider.GetRequiredService<IAudiobookFilesystemDeleteService>());
        service.AfterTrackedContentCaptureForTest = () =>
        {
            File.Move(source, displaced);
            File.WriteAllText(source, "audio");
        };

        // When: deletion continues after its original file was displaced.
        var result = await service.DeleteAsync(audiobook, deleteFolder);

        // Then: matching bytes alone cannot authorize deleting the replacement.
        Assert.True(File.Exists(source));
        Assert.Equal("audio", await File.ReadAllTextAsync(source));
        Assert.Equal("audio", await File.ReadAllTextAsync(displaced));
        Assert.Equal(0, result.DeletedFiles);
        Assert.False(result.TrackedFileCleanupComplete);
        Assert.False(result.DeletedFolder);
        Assert.NotEmpty(result.Warnings);
    }

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
        Assert.NotEmpty(result.Warnings);
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
            warning.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
            || warning.Contains("deletion", StringComparison.OrdinalIgnoreCase));

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
            warning.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
            || warning.Contains("deletion", StringComparison.OrdinalIgnoreCase));
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
