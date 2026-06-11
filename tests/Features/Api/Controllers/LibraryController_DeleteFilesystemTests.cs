/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using Listenarr.Api.Controllers;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Tests.Features.Api.Controllers
{
    [Trait("Area", "LibraryApi")]
    [Trait("Name", "LibraryController_DeleteFilesystemTests")]
    [Trait("Category", "LibraryController")]
    public class LibraryController_DeleteFilesystemTests : BaseTests
    {
        [Fact]
        [Trait("Method", "DeleteAudiobook")]
        [Trait("Scenario", "DeleteFiles_RemovesAllFilesInFolderButPreservesDirectory")]
        public async Task DeleteAudiobook_DeleteFiles_RemovesAllFilesInFolderButPreservesDirectory()
        {
            // Given
            var tempRoot = FileService.GetTempDirectory("listenarr-delete");
            var bookFolder = Path.Join(tempRoot, "Jack of Shadows");
            var extrasFolder = Path.Join(bookFolder, "Extras");
            var audioPath = Path.Join(bookFolder, "Jack of Shadows.mp3");
            var sidecarPath = Path.Join(bookFolder, "cover.jpg");
            var notePath = Path.Join(extrasFolder, "notes.txt");

            Directory.CreateDirectory(extrasFolder);
            await File.WriteAllTextAsync(audioPath, "audio");
            await File.WriteAllTextAsync(sidecarPath, "cover");
            await File.WriteAllTextAsync(notePath, "notes");

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithId(50)
                .WithTitle("Jack of Shadows")
                .WithBasePath(bookFolder)
                .WithFilePath(audioPath)
                .Build());

            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(audioPath)
                .Build());

            var controller = _provider.GetRequiredService<LibraryController>();

            // When
            var result = await controller.DeleteAudiobook(audiobook.Id, deleteFiles: true, deleteFolder: false);

            // Then
            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Equal(200, ok.StatusCode ?? 200);
            Assert.False(File.Exists(audioPath));
            Assert.False(File.Exists(sidecarPath));
            Assert.False(File.Exists(notePath));
            Assert.True(Directory.Exists(bookFolder));
            Assert.False(Directory.Exists(extrasFolder));
        }

        [Fact]
        [Trait("Method", "DeleteAudiobook")]
        [Trait("Scenario", "DeleteFilesAndFolder_RemovesTrackedFilesAndDirectory")]
        public async Task DeleteAudiobook_DeleteFilesAndFolder_RemovesTrackedFilesAndDirectory()
        {
            // Given
            var tempRoot = FileService.GetTempDirectory("listenarr-delete");
            var bookFolder = Path.Join(tempRoot, "Jack of Shadows");
            var audioPath = Path.Join(bookFolder, "Jack of Shadows.mp3");
            var sidecarPath = Path.Join(bookFolder, "cover.jpg");

            Directory.CreateDirectory(bookFolder);
            await File.WriteAllTextAsync(audioPath, "audio");
            await File.WriteAllTextAsync(sidecarPath, "cover");

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithId(1)
                .WithTitle("Jack of Shadows")
                .WithBasePath(bookFolder)
                .WithFilePath(audioPath)
                .Build());

            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(audioPath)
                .Build());

            var controller = _provider.GetRequiredService<LibraryController>();

            // When
            var result = await controller.DeleteAudiobook(audiobook.Id, deleteFiles: true, deleteFolder: true);

            // Then
            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Equal(200, ok.StatusCode ?? 200);
            Assert.False(File.Exists(audioPath));
            Assert.False(Directory.Exists(bookFolder));
        }

        [Fact]
        [Trait("Method", "DeleteAudiobook")]
        [Trait("Scenario", "DeleteFolder_PreservesSharedDirectoryWhenAnotherAudiobookUsesIt")]
        public async Task DeleteAudiobook_DeleteFolder_PreservesSharedDirectoryWhenAnotherAudiobookUsesIt()
        {
            // Given
            var tempRoot = FileService.GetTempDirectory("listenarr-delete");
            var sharedFolder = Path.Join(tempRoot, "Shared");
            var currentAudioPath = Path.Join(sharedFolder, "current.mp3");
            var otherAudioPath = Path.Join(sharedFolder, "other.mp3");

            Directory.CreateDirectory(sharedFolder);
            await File.WriteAllTextAsync(currentAudioPath, "audio");
            await File.WriteAllTextAsync(otherAudioPath, "audio");

            var current = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithId(1)
                .WithTitle("Current")
                .WithBasePath(sharedFolder)
                .WithFilePath(currentAudioPath)
                .Build());
            var other = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithId(2)
                .WithTitle("Other")
                .WithBasePath(sharedFolder)
                .WithFilePath(otherAudioPath)
                .Build());

            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(current)
                .WithPath(currentAudioPath)
                .Build());
            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(other)
                .WithPath(otherAudioPath)
                .Build());

            var controller = _provider.GetRequiredService<LibraryController>();

            // When
            var result = await controller.DeleteAudiobook(current.Id, deleteFiles: true, deleteFolder: true);

            // Then
            var ok = Assert.IsType<OkObjectResult>(result);
            var deletedFolderValue = ok.Value?.GetType().GetProperty("deletedFolder")?.GetValue(ok.Value);
            var deletedFolder = deletedFolderValue is bool flag ? flag : (bool?)null;
            var warnings = ok.Value?.GetType().GetProperty("warnings")?.GetValue(ok.Value) as IEnumerable<string>;

            Assert.False(File.Exists(currentAudioPath));
            Assert.True(File.Exists(otherAudioPath));
            Assert.True(Directory.Exists(sharedFolder));
            Assert.False(deletedFolder ?? true);
            Assert.NotNull(warnings);
            Assert.NotEmpty(warnings!);
        }

        [Fact]
        [Trait("Method", "DeleteAudiobook")]
        [Trait("Scenario", "DeleteFolder_UsesTrackedFileCommonDirectoryWhenBasePathIsProtectedRoot")]
        public async Task DeleteAudiobook_DeleteFolder_UsesTrackedFileCommonDirectoryWhenBasePathIsProtectedRoot()
        {
            // Given
            var tempRoot = FileService.GetTempDirectory("listenarr-delete");
            var bookFolder = Path.Join(tempRoot, "Roger Zelazny", "Jack of Shadows");
            var discFolder = Path.Join(bookFolder, "Disc 01");
            var audioPath = Path.Join(discFolder, "Jack of Shadows-01.mp3");

            Directory.CreateDirectory(discFolder);
            await File.WriteAllTextAsync(audioPath, "audio");

            await _rootFolderRepository.AddAsync(new RootFolder
            {
                Name = "Library",
                Path = tempRoot,
                IsDefault = true
            });

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithId(10)
                .WithTitle("Jack of Shadows")
                .WithBasePath(tempRoot)
                .WithFilePath(audioPath)
                .Build());

            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(audioPath)
                .Build());

            var controller = _provider.GetRequiredService<LibraryController>();

            // When
            var result = await controller.DeleteAudiobook(audiobook.Id, deleteFiles: true, deleteFolder: true);

            // Then
            var ok = Assert.IsType<OkObjectResult>(result);
            var deletedFolderValue = ok.Value?.GetType().GetProperty("deletedFolder")?.GetValue(ok.Value);
            var deletedFolder = deletedFolderValue is bool flag ? flag : (bool?)null;

            Assert.False(File.Exists(audioPath));
            Assert.False(Directory.Exists(bookFolder));
            Assert.True(Directory.Exists(tempRoot));
            Assert.True(deletedFolder ?? false);
        }

        [Fact]
        [Trait("Method", "DeleteAudiobook")]
        [Trait("Scenario", "DeleteFolder_RemovesEmptyAuthorFolder")]
        public async Task DeleteAudiobook_DeleteFolder_RemovesEmptyAuthorFolder()
        {
            // Given
            var tempRoot = FileService.GetTempDirectory("listenarr-delete");
            var authorFolder = Path.Join(tempRoot, "Roger Zelazny");
            var bookFolder = Path.Join(authorFolder, "Jack of Shadows");
            var audioPath = Path.Join(bookFolder, "Jack of Shadows.mp3");

            Directory.CreateDirectory(bookFolder);
            await File.WriteAllTextAsync(audioPath, "audio");

            await _rootFolderRepository.AddAsync(new RootFolder
            {
                Name = "Library",
                Path = tempRoot,
                IsDefault = true
            });

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithId(12)
                .WithTitle("Jack of Shadows")
                .WithAuthor("Roger Zelazny")
                .WithBasePath(bookFolder)
                .WithFilePath(audioPath)
                .Build());

            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(audioPath)
                .Build());

            var controller = _provider.GetRequiredService<LibraryController>();

            // When
            var result = await controller.DeleteAudiobook(audiobook.Id, deleteFiles: true, deleteFolder: true);

            // Then
            var ok = Assert.IsType<OkObjectResult>(result);
            var deletedParentFolderValue = ok.Value?.GetType().GetProperty("deletedParentFolder")?.GetValue(ok.Value);
            var deletedParentFolder = deletedParentFolderValue is bool flag ? flag : (bool?)null;

            Assert.False(File.Exists(audioPath));
            Assert.False(Directory.Exists(bookFolder));
            Assert.False(Directory.Exists(authorFolder));
            Assert.True(Directory.Exists(tempRoot));
            Assert.True(deletedParentFolder ?? false);
        }

    }
}
