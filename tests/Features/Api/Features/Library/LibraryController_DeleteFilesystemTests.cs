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
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    [Trait("Area", "LibraryApi")]
    [Trait("Name", "LibraryController_DeleteFilesystemTests")]
    [Trait("Category", "LibraryController")]
    public class LibraryController_DeleteFilesystemTests : BaseTests
    {
        private async Task AddAuthorizedRootAsync(RootFolder root)
        {
            var identity = await _provider
                .GetRequiredService<IDirectoryObjectIdentityResolver>()
                .ResolveAsync(root.Path);
            Assert.True(identity.IsAvailable, identity.UnavailableReason);
            root.ResolvedCaseSensitivity =
                FileSystemPathSemantics.CurrentHostDefault.CaseSensitivity;
            root.PathIdentityState = PathIdentityState.Valid;
            root.DirectoryObjectIdentityVersion = identity.Version;
            root.DirectoryObjectIdentity = identity.Value;
            root.DirectoryObjectIdentityUnavailableReason =
                identity.UnavailableReason;
            await _rootFolderRepository.AddAsync(root);
        }

        [Fact]
        public async Task DeleteAudiobook_DatabaseFailure_PreservesCachedImage()
        {
            var audiobook = new Audiobook
            {
                Id = 9901,
                Title = "Delete Commit Failure",
                Asin = "B000DELETE"
            };
            var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
            repository.Setup(service => service.GetByIdAsync(audiobook.Id))
                .ReturnsAsync(audiobook);
            repository.Setup(service => service.DeleteByIdAsync(audiobook.Id))
                .ReturnsAsync(false);
            var imageCache = new Mock<IImageCacheService>(MockBehavior.Strict);
            var filesystemDelete = new Mock<IAudiobookFilesystemDeleteService>(
                MockBehavior.Strict);
            var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
            Init(services => services
                .WithSingleton<IAudiobookRepository>(repository.Object)
                .WithSingleton<IImageCacheService>(imageCache.Object)
                .WithSingleton<IAudiobookFilesystemDeleteService>(filesystemDelete.Object)
                .WithSingleton<IFileSystem>(fileSystem.Object));

            var result = await _provider.GetRequiredService<LibraryController>()
                .DeleteAudiobook(
                    audiobook.Id,
                    deleteFiles: false,
                    deleteFolder: false);

            var failure = Assert.IsType<ObjectResult>(result);
            Assert.Equal(500, failure.StatusCode);
            repository.Verify(service => service.GetByIdAsync(audiobook.Id), Times.Once);
            repository.Verify(service => service.DeleteByIdAsync(audiobook.Id), Times.Once);
            imageCache.VerifyNoOtherCalls();
            filesystemDelete.VerifyNoOtherCalls();
            fileSystem.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task DeleteAudiobook_DatabaseFailure_DoesNotDeleteFiles()
        {
            var audiobook = new Audiobook
            {
                Id = 9902,
                Title = "Delete Files Commit Failure"
            };
            var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
            repository.Setup(service => service.GetByIdAsync(audiobook.Id))
                .ReturnsAsync(audiobook);
            repository.Setup(service => service.DeleteByIdAsync(audiobook.Id))
                .ReturnsAsync(false);
            var imageCache = new Mock<IImageCacheService>(MockBehavior.Strict);
            var filesystemDelete = new Mock<IAudiobookFilesystemDeleteService>(
                MockBehavior.Strict);
            var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
            Init(services => services
                .WithSingleton<IAudiobookRepository>(repository.Object)
                .WithSingleton<IImageCacheService>(imageCache.Object)
                .WithSingleton<IAudiobookFilesystemDeleteService>(filesystemDelete.Object)
                .WithSingleton<IFileSystem>(fileSystem.Object));

            var result = await _provider.GetRequiredService<LibraryController>()
                .DeleteAudiobook(
                    audiobook.Id,
                    deleteFiles: true,
                    deleteFolder: true);

            var failure = Assert.IsType<ObjectResult>(result);
            Assert.Equal(500, failure.StatusCode);
            filesystemDelete.VerifyNoOtherCalls();
            imageCache.VerifyNoOtherCalls();
            fileSystem.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task DeleteAudiobook_CanceledImageCleanupAfterCommit_RemainsSuccessful()
        {
            var audiobook = new Audiobook
            {
                Id = 9904,
                Title = "Delete Image Cleanup Cancellation",
                Asin = "B000CANCEL"
            };
            var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
            repository.Setup(service => service.GetByIdAsync(audiobook.Id))
                .ReturnsAsync(audiobook);
            repository.Setup(service => service.DeleteByIdAsync(audiobook.Id))
                .ReturnsAsync(true);
            var imageCache = new Mock<IImageCacheService>(MockBehavior.Strict);
            imageCache.Setup(service => service.GetCachedImagePathAsync(audiobook.Asin))
                .ThrowsAsync(new TaskCanceledException("Injected image cleanup cancellation."));
            var filesystemDelete = new Mock<IAudiobookFilesystemDeleteService>(
                MockBehavior.Strict);
            var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
            Init(services => services
                .WithSingleton<IAudiobookRepository>(repository.Object)
                .WithSingleton<IImageCacheService>(imageCache.Object)
                .WithSingleton<IAudiobookFilesystemDeleteService>(filesystemDelete.Object)
                .WithSingleton<IFileSystem>(fileSystem.Object));

            var result = await _provider.GetRequiredService<LibraryController>()
                .DeleteAudiobook(
                    audiobook.Id,
                    deleteFiles: false,
                    deleteFolder: false);

            Assert.IsType<OkObjectResult>(result);
            repository.Verify(service => service.DeleteByIdAsync(audiobook.Id), Times.Once);
            imageCache.Verify(service => service.GetCachedImagePathAsync(audiobook.Asin), Times.Once);
            filesystemDelete.VerifyNoOtherCalls();
            fileSystem.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task DeleteAudiobook_FilesystemFailureAfterCommit_ReturnsSuccessWithWarning()
        {
            var audiobook = new Audiobook
            {
                Id = 9903,
                Title = "Delete Cleanup Failure"
            };
            var deleteCommitted = false;
            var repository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
            repository.Setup(service => service.GetByIdAsync(audiobook.Id))
                .ReturnsAsync(audiobook);
            repository.Setup(service => service.DeleteByIdAsync(audiobook.Id))
                .ReturnsAsync(() =>
                {
                    deleteCommitted = true;
                    return true;
                });
            var imageCache = new Mock<IImageCacheService>(MockBehavior.Strict);
            var filesystemDelete = new Mock<IAudiobookFilesystemDeleteService>(
                MockBehavior.Strict);
            filesystemDelete.Setup(service => service.DeleteAsync(
                    audiobook,
                    true,
                    CancellationToken.None))
                .Returns(() =>
                {
                    Assert.True(deleteCommitted);
                    return Task.FromException<AudiobookFilesystemDeleteResult>(
                        new IOException("Injected cleanup failure."));
                });
            var fileSystem = new Mock<IFileSystem>(MockBehavior.Strict);
            Init(services => services
                .WithSingleton<IAudiobookRepository>(repository.Object)
                .WithSingleton<IImageCacheService>(imageCache.Object)
                .WithSingleton<IAudiobookFilesystemDeleteService>(filesystemDelete.Object)
                .WithSingleton<IFileSystem>(fileSystem.Object));

            var result = await _provider.GetRequiredService<LibraryController>()
                .DeleteAudiobook(
                    audiobook.Id,
                    deleteFiles: true,
                    deleteFolder: true);

            var ok = Assert.IsType<OkObjectResult>(result);
            var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
            Assert.Contains("could not be fully deleted", json, StringComparison.Ordinal);
            repository.Verify(service => service.DeleteByIdAsync(audiobook.Id), Times.Once);
            filesystemDelete.Verify(service => service.DeleteAsync(
                audiobook,
                true,
                CancellationToken.None), Times.Once);
            imageCache.VerifyNoOtherCalls();
            fileSystem.VerifyNoOtherCalls();
        }

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
            await AddAuthorizedRootAsync(new RootFolderBuilder()
                .WithId(50)
                .WithPath(tempRoot)
                .Build());

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

        [WindowsFact]
        public async Task DeleteAudiobook_ForeignTrackedPathUnderProtectedRoot_PreservesWindowsAlias()
        {
            var tempRoot = FileService.GetTempDirectory("listenarr-delete-foreign-tracked");
            var bookFolder = Path.Join(tempRoot, "Foreign Book");
            var audioPath = Path.Join(bookFolder, "track.m4b");
            Directory.CreateDirectory(bookFolder);
            await File.WriteAllTextAsync(audioPath, "audio");
            var driveRoot = Path.GetPathRoot(audioPath)!;
            var foreignAudioPath = "/" + audioPath[driveRoot.Length..].Replace('\\', '/');
            Assert.Equal(
                Path.GetFullPath(audioPath),
                Path.GetFullPath(foreignAudioPath),
                StringComparer.OrdinalIgnoreCase);
            await AddAuthorizedRootAsync(new RootFolderBuilder()
                .WithId(500)
                .WithPath(tempRoot)
                .Build());
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithId(500)
                .WithTitle("Foreign Book")
                .WithBasePath(tempRoot)
                .Build());
            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(foreignAudioPath)
                .Build());

            var result = await _provider.GetRequiredService<LibraryController>()
                .DeleteAudiobook(
                    audiobook.Id,
                    deleteFiles: true,
                    deleteFolder: false);

            Assert.IsType<OkObjectResult>(result);
            Assert.True(File.Exists(audioPath));
            Assert.True(Directory.Exists(tempRoot));
        }

        [WindowsFact]
        public async Task DeleteAudiobook_ForeignTrackedPathUnderBookFolder_DoesNotDeleteAliasedContent()
        {
            var tempRoot = FileService.GetTempDirectory("listenarr-delete-foreign-book-folder");
            var bookFolder = Path.Join(tempRoot, "Foreign Book Folder");
            var audioPath = Path.Join(bookFolder, "track.m4b");
            Directory.CreateDirectory(bookFolder);
            await File.WriteAllTextAsync(audioPath, "audio");
            var driveRoot = Path.GetPathRoot(audioPath)!;
            var foreignAudioPath = "/" + audioPath[driveRoot.Length..].Replace('\\', '/');
            Assert.Equal(
                Path.GetFullPath(audioPath),
                Path.GetFullPath(foreignAudioPath),
                StringComparer.OrdinalIgnoreCase);
            await AddAuthorizedRootAsync(new RootFolderBuilder()
                .WithId(504)
                .WithPath(tempRoot)
                .Build());
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithId(504)
                .WithTitle("Foreign Book Folder")
                .WithBasePath(bookFolder)
                .Build());
            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(foreignAudioPath)
                .Build());

            var result = await _provider.GetRequiredService<LibraryController>()
                .DeleteAudiobook(
                    audiobook.Id,
                    deleteFiles: true,
                    deleteFolder: false);

            var ok = Assert.IsType<OkObjectResult>(result);
            var warnings = ok.Value?.GetType()
                .GetProperty("warnings")?
                .GetValue(ok.Value) as IEnumerable<string>;
            Assert.True(File.Exists(audioPath));
            Assert.True(Directory.Exists(bookFolder));
            Assert.Contains(warnings ?? [], warning =>
                warning.Contains("unavailable", StringComparison.OrdinalIgnoreCase));
        }

        [WindowsFact]
        public async Task DeleteAudiobook_ForeignBasePath_DoesNotAuthorizeWindowsAliasFolderContents()
        {
            var tempRoot = FileService.GetTempDirectory("listenarr-delete-foreign-base");
            var bookFolder = Path.Join(tempRoot, "Foreign Base Book");
            var audioPath = Path.Join(bookFolder, "track.m4b");
            var sidecarPath = Path.Join(bookFolder, "notes.txt");
            Directory.CreateDirectory(bookFolder);
            await File.WriteAllTextAsync(audioPath, "audio");
            await File.WriteAllTextAsync(sidecarPath, "notes");
            var driveRoot = Path.GetPathRoot(bookFolder)!;
            var foreignBasePath = "/" + bookFolder[driveRoot.Length..].Replace('\\', '/');
            Assert.Equal(
                Path.GetFullPath(bookFolder),
                Path.GetFullPath(foreignBasePath),
                StringComparer.OrdinalIgnoreCase);
            await AddAuthorizedRootAsync(new RootFolderBuilder()
                .WithId(505)
                .WithPath(tempRoot)
                .Build());
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithId(505)
                .WithTitle("Foreign Base Book")
                .WithBasePath(foreignBasePath)
                .WithFilePath(audioPath)
                .Build());
            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(audioPath)
                .Build());

            var result = await _provider.GetRequiredService<LibraryController>()
                .DeleteAudiobook(
                    audiobook.Id,
                    deleteFiles: true,
                    deleteFolder: false);

            Assert.IsType<OkObjectResult>(result);
            Assert.True(File.Exists(audioPath));
            Assert.True(File.Exists(sidecarPath));
            Assert.True(Directory.Exists(bookFolder));
        }

        [WindowsFact]
        public async Task DeleteAudiobook_AmbiguousPersistedBasePath_DoesNotProbeWindowsDeviceAlias()
        {
            var probedBoundaries = new List<string>();
            var semanticsResolver = new FileSystemSemanticsResolver
            {
                BeforeProbeForTest = path => probedBoundaries.Add(Path.GetFullPath(path))
            };
            Init(builder => builder.WithSingleton<IFileSystemSemanticsResolver>(semanticsResolver));
            var tempRoot = FileService.GetTempDirectory("listenarr-delete-ambiguous-base");
            var bookFolder = Path.Join(tempRoot, "Ambiguous Base Book");
            var audioPath = Path.Join(bookFolder, "track.m4b");
            var sidecarPath = Path.Join(bookFolder, "notes.txt");
            Directory.CreateDirectory(bookFolder);
            await File.WriteAllTextAsync(audioPath, "audio");
            await File.WriteAllTextAsync(sidecarPath, "notes");
            var ambiguousBasePath = "//?/" + Path.GetFullPath(bookFolder).Replace('\\', '/');
            Assert.False(FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                ambiguousBasePath,
                out _));
            Assert.True(Directory.Exists(ambiguousBasePath));
            await AddAuthorizedRootAsync(new RootFolderBuilder()
                .WithId(509)
                .WithPath(tempRoot)
                .Build());
            probedBoundaries.Clear();
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithId(509)
                .WithTitle("Ambiguous Base Book")
                .WithBasePath(ambiguousBasePath)
                .WithFilePath(audioPath)
                .Build());
            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(audioPath)
                .Build());

            var result = await _provider.GetRequiredService<LibraryController>()
                .DeleteAudiobook(
                    audiobook.Id,
                    deleteFiles: true,
                    deleteFolder: true);

            Assert.IsType<OkObjectResult>(result);
            Assert.True(File.Exists(audioPath));
            Assert.True(File.Exists(sidecarPath));
            Assert.True(Directory.Exists(bookFolder));
            var ambiguousNativeAlias = Path.GetFullPath(ambiguousBasePath);
            Assert.DoesNotContain(probedBoundaries, path => string.Equals(
                path,
                ambiguousNativeAlias,
                StringComparison.OrdinalIgnoreCase));
        }

        [WindowsFact]
        public async Task DeleteAudiobook_ForeignConfiguredOutputPath_DoesNotProtectWindowsAliasFolder()
        {
            var tempRoot = FileService.GetTempDirectory("listenarr-delete-foreign-output-root");
            var bookFolder = Path.Join(tempRoot, "Native Book");
            var audioPath = Path.Join(bookFolder, "track.m4b");
            Directory.CreateDirectory(bookFolder);
            await File.WriteAllTextAsync(audioPath, "audio");
            var driveRoot = Path.GetPathRoot(bookFolder)!;
            var foreignOutputPath = "/" + bookFolder[driveRoot.Length..].Replace('\\', '/');
            Assert.Equal(
                Path.GetFullPath(bookFolder),
                Path.GetFullPath(foreignOutputPath),
                StringComparer.OrdinalIgnoreCase);
            await _applicationSettingsRepository.SaveAsync(
                new ApplicationSettingsBuilder()
                    .WithOutputPath(foreignOutputPath)
                    .Build());
            await AddAuthorizedRootAsync(new RootFolderBuilder()
                .WithId(506)
                .WithPath(tempRoot)
                .Build());
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithId(506)
                .WithTitle("Native Book")
                .WithBasePath(bookFolder)
                .WithFilePath(audioPath)
                .Build());
            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(audioPath)
                .Build());

            var result = await _provider.GetRequiredService<LibraryController>()
                .DeleteAudiobook(
                    audiobook.Id,
                    deleteFiles: true,
                    deleteFolder: true);

            Assert.IsType<OkObjectResult>(result);
            Assert.False(File.Exists(audioPath));
            Assert.False(Directory.Exists(bookFolder));
        }

        [WindowsFact]
        public async Task DeleteAudiobook_ForeignPersistedRoot_DoesNotProtectWindowsAliasFolder()
        {
            var tempRoot = FileService.GetTempDirectory("listenarr-delete-foreign-protected-root");
            var bookFolder = Path.Join(tempRoot, "Native Root Book");
            var audioPath = Path.Join(bookFolder, "track.m4b");
            Directory.CreateDirectory(bookFolder);
            await File.WriteAllTextAsync(audioPath, "audio");
            var driveRoot = Path.GetPathRoot(bookFolder)!;
            var foreignRootPath = "/" + bookFolder[driveRoot.Length..].Replace('\\', '/');
            Assert.Equal(
                Path.GetFullPath(bookFolder),
                Path.GetFullPath(foreignRootPath),
                StringComparer.OrdinalIgnoreCase);
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithId(507)
                .WithPath(foreignRootPath)
                .Build());
            await AddAuthorizedRootAsync(new RootFolderBuilder()
                .WithId(508)
                .WithPath(tempRoot)
                .Build());
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithId(507)
                .WithTitle("Native Root Book")
                .WithBasePath(bookFolder)
                .WithFilePath(audioPath)
                .Build());
            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(audioPath)
                .Build());

            var result = await _provider.GetRequiredService<LibraryController>()
                .DeleteAudiobook(
                    audiobook.Id,
                    deleteFiles: true,
                    deleteFolder: true);

            Assert.IsType<OkObjectResult>(result);
            Assert.False(File.Exists(audioPath));
            Assert.False(Directory.Exists(bookFolder));
        }

        [Fact]
        public async Task DeleteAudiobook_RelativeTrackedPathUnderProtectedRoot_DeletesResolvedFileOnly()
        {
            var tempRoot = FileService.GetTempDirectory("listenarr-delete-relative");
            var bookFolder = Path.Join(tempRoot, "Relative Book");
            var audioPath = Path.Join(bookFolder, "track.m4b");
            Directory.CreateDirectory(bookFolder);
            await File.WriteAllTextAsync(audioPath, "audio");
            await AddAuthorizedRootAsync(new RootFolderBuilder()
                .WithId(501)
                .WithPath(tempRoot)
                .Build());
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithId(501)
                .WithTitle("Relative Book")
                .WithBasePath(tempRoot)
                .Build());
            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(Path.Join("Relative Book", "track.m4b"))
                .Build());

            var result = await _provider.GetRequiredService<LibraryController>()
                .DeleteAudiobook(
                    audiobook.Id,
                    deleteFiles: true,
                    deleteFolder: false);

            Assert.IsType<OkObjectResult>(result);
            Assert.False(File.Exists(audioPath));
            Assert.True(Directory.Exists(tempRoot));
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
            await AddAuthorizedRootAsync(new RootFolderBuilder()
                .WithId(51)
                .WithPath(tempRoot)
                .Build());

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

        [WindowsFact]
        public async Task DeleteAudiobook_DeleteFolder_ForeignOtherAudiobookPathBlocksRecursiveDelete()
        {
            var tempRoot = FileService.GetTempDirectory("listenarr-delete-foreign-other");
            var sharedFolder = Path.Join(tempRoot, "Shared");
            var currentAudioPath = Path.Join(sharedFolder, "current.mp3");
            var otherAudioPath = Path.Join(sharedFolder, "other.mp3");
            Directory.CreateDirectory(sharedFolder);
            await File.WriteAllTextAsync(currentAudioPath, "current");
            await File.WriteAllTextAsync(otherAudioPath, "other");
            await AddAuthorizedRootAsync(new RootFolderBuilder()
                .WithId(502)
                .WithPath(tempRoot)
                .Build());

            var current = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithId(502)
                .WithTitle("Current")
                .WithBasePath(sharedFolder)
                .WithFilePath(currentAudioPath)
                .Build());
            var driveRoot = Path.GetPathRoot(sharedFolder)!;
            var foreignSharedFolder = "/" + sharedFolder[driveRoot.Length..].Replace('\\', '/');
            var foreignOtherPath = foreignSharedFolder + "/other.mp3";
            Assert.Equal(
                Path.GetFullPath(otherAudioPath),
                Path.GetFullPath(foreignOtherPath),
                StringComparer.OrdinalIgnoreCase);
            var other = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithId(503)
                .WithTitle("Other")
                .WithBasePath(foreignSharedFolder)
                .WithFilePath(foreignOtherPath)
                .Build());
            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(current)
                .WithPath(currentAudioPath)
                .Build());
            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(other)
                .WithPath(foreignOtherPath)
                .Build());

            var result = await _provider.GetRequiredService<LibraryController>()
                .DeleteAudiobook(
                    current.Id,
                    deleteFiles: true,
                    deleteFolder: true);

            var ok = Assert.IsType<OkObjectResult>(result);
            var warnings = ok.Value?.GetType()
                .GetProperty("warnings")?
                .GetValue(ok.Value) as IEnumerable<string>;
            Assert.False(File.Exists(currentAudioPath));
            Assert.True(File.Exists(otherAudioPath));
            Assert.True(Directory.Exists(sharedFolder));
            Assert.Contains(warnings ?? [], warning =>
                warning.Contains("unresolved", StringComparison.OrdinalIgnoreCase));
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

            await AddAuthorizedRootAsync(new RootFolder
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
        [Trait("Scenario", "DeleteFolder_PreservesUnownedEmptyAuthorFolder")]
        public async Task DeleteAudiobook_DeleteFolder_PreservesUnownedEmptyAuthorFolder()
        {
            var tempRoot = FileService.GetTempDirectory("listenarr-delete-unowned-parent");
            var authorFolder = Path.Join(tempRoot, "Roger Zelazny");
            var bookFolder = Path.Join(authorFolder, "Jack of Shadows");
            var audioPath = Path.Join(bookFolder, "Jack of Shadows.mp3");
            Directory.CreateDirectory(bookFolder);
            await File.WriteAllTextAsync(audioPath, "audio");
            await AddAuthorizedRootAsync(new RootFolder
            {
                Name = "Library",
                Path = tempRoot,
                IsDefault = true
            });
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithId(11)
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

            var result = await controller.DeleteAudiobook(
                audiobook.Id,
                deleteFiles: true,
                deleteFolder: true);

            var ok = Assert.IsType<OkObjectResult>(result);
            var deletedParentFolderValue = ok.Value?.GetType()
                .GetProperty("deletedParentFolder")?.GetValue(ok.Value);
            Assert.False(File.Exists(audioPath));
            Assert.False(Directory.Exists(bookFolder));
            Assert.True(Directory.Exists(authorFolder));
            Assert.False(deletedParentFolderValue is true);
        }

        [Fact]
        [Trait("Method", "DeleteAudiobook")]
        [Trait("Scenario", "DeleteFolder_RemovesOwnedEmptyAuthorFolder")]
        public async Task DeleteAudiobook_DeleteFolder_RemovesOwnedEmptyAuthorFolder()
        {
            var tempRoot = FileService.GetTempDirectory("listenarr-delete-owned-parent");
            var authorFolder = Path.Join(tempRoot, "Roger Zelazny");
            var bookFolder = Path.Join(authorFolder, "Jack of Shadows");
            var audioPath = Path.Join(bookFolder, "Jack of Shadows.mp3");
            Directory.CreateDirectory(bookFolder);
            await File.WriteAllTextAsync(audioPath, "audio");
            await AddAuthorizedRootAsync(new RootFolder
            {
                Name = "Library",
                Path = tempRoot,
                IsDefault = true
            });
            var ownershipStore = _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>();
            await ownershipStore.RecordCreatedAsync(
                new LibraryDirectoryOwnershipClaim(
                    authorFolder,
                    FileSystemPathSemantics.CurrentHostDefault,
                    "test-fixture",
                    Guid.NewGuid()));
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

            var result = await controller.DeleteAudiobook(
                audiobook.Id,
                deleteFiles: true,
                deleteFolder: true);

            var ok = Assert.IsType<OkObjectResult>(result);
            var deletedParentFolderValue = ok.Value?.GetType()
                .GetProperty("deletedParentFolder")?.GetValue(ok.Value);
            Assert.False(File.Exists(audioPath));
            Assert.False(Directory.Exists(bookFolder));
            Assert.False(Directory.Exists(authorFolder));
            Assert.True(Directory.Exists(tempRoot));
            Assert.True(deletedParentFolderValue is true);
        }

        [WindowsFact]
        public async Task DeleteAudiobook_DeleteFolder_PreservesOwnedAuthorFolderWhenOtherAudiobookBasePathIsAmbiguous()
        {
            var tempRoot = FileService.GetTempDirectory("listenarr-delete-owned-parent-ambiguous-peer");
            var authorFolder = Path.Join(tempRoot, "Roger Zelazny");
            var bookFolder = Path.Join(authorFolder, "Jack of Shadows");
            var audioPath = Path.Join(bookFolder, "Jack of Shadows.mp3");
            var unrelatedFolder = Path.Join(tempRoot, "Other Author", "Other Book");
            Directory.CreateDirectory(bookFolder);
            Directory.CreateDirectory(unrelatedFolder);
            await File.WriteAllTextAsync(audioPath, "audio");
            await AddAuthorizedRootAsync(new RootFolder
            {
                Name = "Library",
                Path = tempRoot,
                IsDefault = true
            });
            var ownershipStore = _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>();
            await ownershipStore.RecordCreatedAsync(
                new LibraryDirectoryOwnershipClaim(
                    authorFolder,
                    FileSystemPathSemantics.CurrentHostDefault,
                    "test-fixture",
                    Guid.NewGuid()));
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithId(1201)
                .WithTitle("Jack of Shadows")
                .WithAuthor("Roger Zelazny")
                .WithBasePath(bookFolder)
                .WithFilePath(audioPath)
                .Build());
            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(audioPath)
                .Build());
            var ambiguousPeerBasePath = "//?/" + Path.GetFullPath(unrelatedFolder).Replace('\\', '/');
            Assert.False(FileSystemPathIdentity.TryDetectAbsoluteSyntax(
                ambiguousPeerBasePath,
                out _));
            Assert.True(Directory.Exists(ambiguousPeerBasePath));
            var peer = new AudiobookBuilder()
                .WithId(1202)
                .WithTitle("Other Book")
                .WithAuthor("Other Author")
                .WithBasePath(ambiguousPeerBasePath)
                .Build();
            var audiobookRepository = new Mock<IAudiobookRepository>();
            audiobookRepository
                .SetupSequence(repository => repository.GetAllAsync())
                .ReturnsAsync([audiobook])
                .ReturnsAsync([audiobook, peer]);
            var service = new AudiobookFilesystemDeleteService(
                audiobookRepository.Object,
                _provider.GetRequiredService<IAudiobookFileRepository>(),
                _provider.GetRequiredService<IRootFolderService>(),
                _provider.GetRequiredService<IConfigurationService>(),
                _provider.GetRequiredService<IFileSystemSemanticsResolver>(),
                ownershipStore,
                _provider.GetRequiredService<ILogger<AudiobookFilesystemDeleteService>>(),
                _provider.GetRequiredService<LibraryDirectoryOwnershipBoundaryAuthorizer>());

            var result = await service.DeleteAsync(audiobook, deleteFolder: true);

            var deletedParentFolderValue = result.DeletedParentFolder;
            Assert.False(File.Exists(audioPath));
            Assert.False(Directory.Exists(bookFolder));
            Assert.True(Directory.Exists(authorFolder));
            Assert.False(deletedParentFolderValue is true);
        }

        [Fact]
        public async Task DeleteAudiobook_DeleteFolderRetiresOwnedNestedHierarchy()
        {
            var tempRoot = FileService.GetTempDirectory("listenarr-delete-owned-hierarchy");
            var authorFolder = Path.Join(tempRoot, "Author");
            var bookFolder = Path.Join(authorFolder, "Book");
            var discFolder = Path.Join(bookFolder, "Disc 1");
            var audioPath = Path.Join(discFolder, "book.mp3");
            Directory.CreateDirectory(discFolder);
            await File.WriteAllTextAsync(audioPath, "audio");
            await AddAuthorizedRootAsync(new RootFolder
            {
                Name = "Library",
                Path = tempRoot,
                IsDefault = true
            });
            var ownershipStore = _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>();
            var operationId = Guid.NewGuid();
            var ownerships = new List<LibraryDirectoryOwnership>();
            foreach (var directory in new[] { authorFolder, bookFolder, discFolder })
            {
                ownerships.Add(await ownershipStore.RecordCreatedAsync(
                    new LibraryDirectoryOwnershipClaim(
                        directory,
                        FileSystemPathSemantics.CurrentHostDefault,
                        "test-fixture",
                        operationId)));
            }
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithId(13)
                .WithTitle("Book")
                .WithAuthor("Author")
                .WithBasePath(bookFolder)
                .WithFilePath(audioPath)
                .Build());
            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(audioPath)
                .Build());
            var controller = _provider.GetRequiredService<LibraryController>();

            var result = await controller.DeleteAudiobook(
                audiobook.Id,
                deleteFiles: true,
                deleteFolder: true);

            Assert.IsType<OkObjectResult>(result);
            Assert.False(Directory.Exists(discFolder));
            Assert.False(Directory.Exists(bookFolder));
            Assert.False(Directory.Exists(authorFolder));
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var ownershipIds = ownerships.Select(item => item.Id).ToList();
            var persisted = await db.LibraryDirectoryOwnerships.AsNoTracking()
                .Where(candidate => ownershipIds.Contains(candidate.Id))
                .ToListAsync();
            Assert.Equal(3, persisted.Count);
            Assert.All(persisted, ownership =>
            {
                Assert.Equal(LibraryDirectoryOwnershipState.Removed, ownership.State);
                Assert.Null(ownership.PathOwnershipKey);
            });
            Assert.All(ownerships, ownership =>
                Assert.All(
                    LibraryDirectoryOwnershipMarker.GetMarkerPaths(ownership),
                    markerPath => Assert.False(File.Exists(markerPath))));
        }

        [LinuxFact]
        public async Task FilesystemDelete_NativeCaseSensitiveCaseDistinctPath_DoesNotBlockDelete()
        {
            var tempRoot = FileService.GetTempDirectory(
                "listenarr-delete-native-sensitive");
            var bookFolder = Path.Join(tempRoot, "CaseBook");
            var alternateCasePath = Path.Join(tempRoot, "casebook");
            var audioPath = Path.Join(bookFolder, "book.mp3");
            Directory.CreateDirectory(bookFolder);
            await File.WriteAllTextAsync(audioPath, "audio");
            await AddAuthorizedRootAsync(new RootFolderBuilder()
                .WithName("Library")
                .WithPath(tempRoot)
                .WithCaseSensitivityMode(FileSystemCaseSensitivityMode.Auto)
                .WithIsDefault()
                .Build());
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithId(901)
                .WithTitle("Case Book")
                .WithBasePath(bookFolder)
                .WithFilePath(audioPath)
                .Build());
            await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithId(902)
                .WithTitle("Other Case Book")
                .WithBasePath(alternateCasePath)
                .Build());

            var service = _provider.GetRequiredService<IAudiobookFilesystemDeleteService>();
            var result = await service.DeleteAsync(audiobook, deleteFolder: true);

            Assert.True(result.DeletedFolder, string.Join("; ", result.Warnings));
            Assert.False(Directory.Exists(bookFolder));
            Assert.False(File.Exists(audioPath));
        }

        [WindowsFact]
        public async Task FilesystemDelete_NativeCaseInsensitivePhysicalAlias_BlocksDelete()
        {
            var tempRoot = FileService.GetTempDirectory(
                "listenarr-delete-native-insensitive");
            var bookFolder = Path.Join(tempRoot, "CaseBook");
            var alternateCasePath = Path.Join(tempRoot, "casebook");
            var audioPath = Path.Join(bookFolder, "book.mp3");
            Directory.CreateDirectory(bookFolder);
            await File.WriteAllTextAsync(audioPath, "audio");
            await AddAuthorizedRootAsync(new RootFolderBuilder()
                .WithName("Library")
                .WithPath(tempRoot)
                .WithCaseSensitivityMode(FileSystemCaseSensitivityMode.Auto)
                .WithIsDefault()
                .Build());
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithId(901)
                .WithTitle("Case Book")
                .WithBasePath(bookFolder)
                .WithFilePath(audioPath)
                .Build());
            await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithId(902)
                .WithTitle("Other Case Book")
                .WithBasePath(alternateCasePath)
                .Build());
            var identityResolver = _provider
                .GetRequiredService<IDirectoryObjectIdentityResolver>();
            var originalIdentity = await identityResolver.ResolveAsync(bookFolder);
            var aliasIdentity = await identityResolver.ResolveAsync(alternateCasePath);
            Assert.True(originalIdentity.IsAvailable, originalIdentity.UnavailableReason);
            Assert.True(aliasIdentity.IsAvailable, aliasIdentity.UnavailableReason);
            Assert.Equal(originalIdentity.Value, aliasIdentity.Value);

            var service = _provider.GetRequiredService<IAudiobookFilesystemDeleteService>();
            var result = await service.DeleteAsync(audiobook, deleteFolder: true);

            Assert.False(result.DeletedFolder);
            Assert.Contains(
                result.Warnings,
                warning => warning.Contains(
                    "another audiobook references that location",
                    StringComparison.OrdinalIgnoreCase));
            Assert.True(Directory.Exists(bookFolder));
            Assert.False(File.Exists(audioPath));
        }

        [LinuxFact]
        public async Task FilesystemDelete_NestedRootUsesMostSpecificSemantics()
        {

            var outerRoot = FileService.GetTempDirectory("listenarr-delete-nested-outer");
            var innerRoot = Path.Join(outerRoot, "Sensitive Library");
            var bookFolder = Path.Join(innerRoot, "CaseBook");
            var audioPath = Path.Join(bookFolder, "book.mp3");
            Directory.CreateDirectory(bookFolder);
            await File.WriteAllTextAsync(audioPath, "audio");
            await AddAuthorizedRootAsync(new RootFolderBuilder()
                .WithName("A Outer")
                .WithPath(outerRoot)
                .WithCaseSensitivityMode(FileSystemCaseSensitivityMode.Insensitive)
                .Build());
            await AddAuthorizedRootAsync(new RootFolderBuilder()
                .WithName("Z Inner")
                .WithPath(innerRoot)
                .WithCaseSensitivityMode(FileSystemCaseSensitivityMode.Sensitive)
                .Build());
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithId(903)
                .WithTitle("Nested Case Book")
                .WithBasePath(bookFolder)
                .WithFilePath(audioPath)
                .Build());
            await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithId(904)
                .WithTitle("Other Nested Case Book")
                .WithBasePath(Path.Join(innerRoot, "casebook"))
                .Build());

            var service = _provider.GetRequiredService<IAudiobookFilesystemDeleteService>();
            var result = await service.DeleteAsync(audiobook, deleteFolder: true);

            Assert.True(result.DeletedFolder, string.Join("; ", result.Warnings));
            Assert.False(Directory.Exists(bookFolder));
            Assert.False(File.Exists(audioPath));
        }

        [Fact]
        public async Task FilesystemDelete_ParentMarkRemovedFailure_IsRecoveredOnRetry()
        {
            var tempRoot = FileService.GetTempDirectory("listenarr-delete-parent-state-retry");
            var authorFolder = Path.Join(tempRoot, "Author");
            var bookFolder = Path.Join(authorFolder, "Book");
            var audioPath = Path.Join(bookFolder, "book.mp3");
            Directory.CreateDirectory(bookFolder);
            await File.WriteAllTextAsync(audioPath, "audio");
            await AddAuthorizedRootAsync(new RootFolder
            {
                Name = "Library",
                Path = tempRoot,
                IsDefault = true
            });
            var ownershipStore = _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>();
            var authorOwnership = await ownershipStore.RecordCreatedAsync(
                new LibraryDirectoryOwnershipClaim(
                    authorFolder,
                    FileSystemPathSemantics.CurrentHostDefault,
                    "test-fixture"));
            var bookOwnership = await ownershipStore.RecordCreatedAsync(
                new LibraryDirectoryOwnershipClaim(
                    bookFolder,
                    FileSystemPathSemantics.CurrentHostDefault,
                    "test-fixture"));
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithId(904)
                .WithTitle("Book")
                .WithAuthor("Author")
                .WithBasePath(bookFolder)
                .WithFilePath(audioPath)
                .Build());
            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(audioPath)
                .Build());
            var failingService = new AudiobookFilesystemDeleteService(
                _provider.GetRequiredService<IAudiobookRepository>(),
                _provider.GetRequiredService<IAudiobookFileRepository>(),
                _provider.GetRequiredService<IRootFolderService>(),
                _provider.GetRequiredService<IConfigurationService>(),
                _provider.GetRequiredService<IFileSystemSemanticsResolver>(),
                new FailingNthMarkRemovedOwnershipStore(ownershipStore, failOnCall: 2),
                _provider.GetRequiredService<ILogger<AudiobookFilesystemDeleteService>>(),
                _provider.GetRequiredService<LibraryDirectoryOwnershipBoundaryAuthorizer>());
            var authorSiblingMarker = LibraryDirectoryOwnershipMarker
                .GetMarkerPaths(authorOwnership)
                .Single(path => !FileSystemPathIdentity.IsSameOrInside(
                    path,
                    authorFolder,
                    FileSystemPathSemantics.CurrentHostDefault));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                failingService.DeleteAsync(audiobook, deleteFolder: true));

            Assert.False(Directory.Exists(bookFolder));
            Assert.False(Directory.Exists(authorFolder));
            Assert.True(File.Exists(authorSiblingMarker));
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using (var interruptedDb = await factory.CreateDbContextAsync())
            {
                var persistedBook = await interruptedDb.LibraryDirectoryOwnerships.AsNoTracking()
                    .SingleAsync(candidate => candidate.Id == bookOwnership.Id);
                var persistedAuthor = await interruptedDb.LibraryDirectoryOwnerships.AsNoTracking()
                    .SingleAsync(candidate => candidate.Id == authorOwnership.Id);
                Assert.Equal(LibraryDirectoryOwnershipState.Removed, persistedBook.State);
                Assert.Equal(LibraryDirectoryOwnershipState.Removing, persistedAuthor.State);
                Assert.NotNull(persistedAuthor.PathOwnershipKey);
            }

            var normalService = _provider.GetRequiredService<IAudiobookFilesystemDeleteService>();
            await normalService.DeleteAsync(audiobook, deleteFolder: true);

            Assert.False(File.Exists(authorSiblingMarker));
            await using var recoveredDb = await factory.CreateDbContextAsync();
            var recoveredAuthor = await recoveredDb.LibraryDirectoryOwnerships.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == authorOwnership.Id);
            Assert.Equal(LibraryDirectoryOwnershipState.Removed, recoveredAuthor.State);
            Assert.Null(recoveredAuthor.PathOwnershipKey);
        }

        [Fact]
        public async Task FilesystemDelete_RequestCancelledAfterAuthorizationBeforeMutation_DoesNotDelete()
        {
            var tempRoot = FileService.GetTempDirectory("listenarr-delete-pre-mutation-cancel");
            var bookFolder = Path.Join(tempRoot, "Book");
            var audioPath = Path.Join(bookFolder, "book.mp3");
            Directory.CreateDirectory(bookFolder);
            await File.WriteAllTextAsync(audioPath, "audio");
            await AddAuthorizedRootAsync(new RootFolder
            {
                Name = "Library",
                Path = tempRoot,
                IsDefault = true
            });
            var ownershipStore = _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>();
            var ownership = await ownershipStore.RecordCreatedAsync(
                new LibraryDirectoryOwnershipClaim(
                    bookFolder,
                    FileSystemPathSemantics.CurrentHostDefault,
                    "test-fixture"));
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Pre Mutation Cancellation")
                .WithBasePath(bookFolder)
                .WithFilePath(audioPath)
                .Build());
            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(audioPath)
                .Build());
            using var cancellation = new CancellationTokenSource();
            var cancelled = false;
            using var hook = ExclusiveDirectoryCreator.PushBeforeOpenParentHook(path =>
            {
                if (cancelled || !string.Equals(path, bookFolder, StringComparison.Ordinal))
                {
                    return;
                }

                cancelled = true;
                cancellation.Cancel();
            });

            var service = _provider.GetRequiredService<IAudiobookFilesystemDeleteService>();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.DeleteAsync(audiobook, deleteFolder: true, cancellation.Token));

            Assert.True(cancelled);
            Assert.True(File.Exists(audioPath));
            Assert.True(Directory.Exists(bookFolder));
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var persisted = await db.LibraryDirectoryOwnerships.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == ownership.Id);
            Assert.Equal(LibraryDirectoryOwnershipState.Owned, persisted.State);
        }

        [Fact]
        public async Task FilesystemDelete_RequestCancelledAfterMutationBeginsCompletesOwnershipRetirement()
        {
            var tempRoot = FileService.GetTempDirectory("listenarr-delete-post-mutation-cancel");
            var bookFolder = Path.Join(tempRoot, "Book");
            var audioPath = Path.Join(bookFolder, "book.mp3");
            Directory.CreateDirectory(bookFolder);
            await File.WriteAllTextAsync(audioPath, "audio");
            await AddAuthorizedRootAsync(new RootFolder
            {
                Name = "Library",
                Path = tempRoot,
                IsDefault = true
            });
            var ownershipStore = _provider.GetRequiredService<ILibraryDirectoryOwnershipStore>();
            var ownership = await ownershipStore.RecordCreatedAsync(
                new LibraryDirectoryOwnershipClaim(
                    bookFolder,
                    FileSystemPathSemantics.CurrentHostDefault,
                    "test-fixture"));
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithId(908)
                .WithTitle("Post Mutation Cancellation")
                .WithBasePath(bookFolder)
                .WithFilePath(audioPath)
                .Build());
            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(audioPath)
                .Build());
            using var cancellation = new CancellationTokenSource();
            var service = new AudiobookFilesystemDeleteService(
                _provider.GetRequiredService<IAudiobookRepository>(),
                _provider.GetRequiredService<IAudiobookFileRepository>(),
                _provider.GetRequiredService<IRootFolderService>(),
                _provider.GetRequiredService<IConfigurationService>(),
                _provider.GetRequiredService<IFileSystemSemanticsResolver>(),
                new CancelOnBeginRemovalOwnershipStore(
                    ownershipStore,
                    cancellation),
                _provider.GetRequiredService<ILogger<AudiobookFilesystemDeleteService>>(),
                _provider.GetRequiredService<LibraryDirectoryOwnershipBoundaryAuthorizer>());

            var result = await service.DeleteAsync(
                audiobook,
                deleteFolder: true,
                cancellation.Token);

            Assert.True(cancellation.IsCancellationRequested);
            Assert.True(result.DeletedFolder);
            Assert.False(File.Exists(audioPath));
            Assert.False(Directory.Exists(bookFolder));
            var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var persisted = await db.LibraryDirectoryOwnerships.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == ownership.Id);
            Assert.Equal(LibraryDirectoryOwnershipState.Removed, persisted.State);
            Assert.Null(persisted.PathOwnershipKey);
        }

        [Fact]
        public async Task DeleteAudiobook_AcquiresGlobalFilesystemBoundaryBeforeAudiobookBoundary()
        {
            var events = new List<string>();
            var filesystemCoordinator = new Mock<IFilesystemMutationCoordinator>();
            filesystemCoordinator
                .Setup(coordinator => coordinator.ExecuteExclusiveAsync(
                    It.IsAny<Func<CancellationToken, Task<IActionResult>>>(),
                    It.IsAny<CancellationToken>()))
                .Returns<Func<CancellationToken, Task<IActionResult>>, CancellationToken>(async (operation, cancellationToken) =>
                {
                    events.Add("global-enter");
                    var result = await operation(cancellationToken);
                    events.Add("global-exit");
                    return result;
                });
            var audiobookCoordinator = new Mock<IAudiobookOperationCoordinator>();
            audiobookCoordinator
                .Setup(coordinator => coordinator.ExecuteExclusiveAsync(
                    It.IsAny<int>(),
                    It.IsAny<Func<CancellationToken, Task<IActionResult>>>(),
                    It.IsAny<CancellationToken>()))
                .Returns<int, Func<CancellationToken, Task<IActionResult>>, CancellationToken>(async (_, operation, cancellationToken) =>
                {
                    events.Add("audiobook-enter");
                    var result = await operation(cancellationToken);
                    events.Add("audiobook-exit");
                    return result;
                });
            Init(builder => builder
                .WithSingleton(filesystemCoordinator.Object)
                .WithSingleton(audiobookCoordinator.Object));
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithId(905)
                .WithTitle("Coordinated Delete")
                .Build());
            var controller = _provider.GetRequiredService<LibraryController>();

            var result = await controller.DeleteAudiobook(audiobook.Id);

            Assert.IsType<OkObjectResult>(result);
            Assert.Equal(
                ["global-enter", "audiobook-enter", "audiobook-exit", "global-exit"],
                events);
        }

        [Fact]
        public async Task DeleteAudiobook_WaitsForExistingFilesystemMutation()
        {
            var folder = FileService.GetTempDirectory("listenarr-delete-coordination");
            var audioPath = Path.Join(folder, "book.mp3");
            await File.WriteAllTextAsync(audioPath, "audio");
            await AddAuthorizedRootAsync(new RootFolderBuilder()
                .WithId(52)
                .WithPath(Path.GetDirectoryName(folder)!)
                .Build());
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithId(906)
                .WithTitle("Blocked Delete")
                .WithBasePath(folder)
                .WithFilePath(audioPath)
                .Build());
            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(audioPath)
                .Build());
            var filesystemCoordinator = _provider.GetRequiredService<IFilesystemMutationCoordinator>();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var holder = filesystemCoordinator.ExecuteExclusiveAsync(async _ =>
            {
                entered.SetResult();
                await release.Task;
            });
            await entered.Task;
            var controller = _provider.GetRequiredService<LibraryController>();

            var delete = controller.DeleteAudiobook(
                audiobook.Id,
                deleteFiles: true,
                deleteFolder: true);
            await Task.Delay(100);

            Assert.False(delete.IsCompleted);
            Assert.True(File.Exists(audioPath));
            release.SetResult();
            await holder;
            var result = await delete;
            Assert.IsType<OkObjectResult>(result);
            Assert.False(File.Exists(audioPath));
        }

        [Fact]
        public async Task DeleteAudiobook_CanceledWhileWaitingForFilesystemMutation_DoesNotDelete()
        {
            var folder = FileService.GetTempDirectory("listenarr-delete-canceled-wait");
            var audioPath = Path.Join(folder, "book.mp3");
            await File.WriteAllTextAsync(audioPath, "audio");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithId(907)
                .WithTitle("Canceled Delete")
                .WithBasePath(folder)
                .WithFilePath(audioPath)
                .Build());
            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(audioPath)
                .Build());
            var filesystemCoordinator = _provider.GetRequiredService<IFilesystemMutationCoordinator>();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var holder = filesystemCoordinator.ExecuteExclusiveAsync(async _ =>
            {
                entered.SetResult();
                await release.Task;
            });
            await entered.Task;
            using var cancellation = new CancellationTokenSource();
            var controller = _provider.GetRequiredService<LibraryController>();
            var delete = controller.DeleteAudiobook(
                audiobook.Id,
                deleteFiles: true,
                deleteFolder: true,
                cancellationToken: cancellation.Token);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delete);

            Assert.True(File.Exists(audioPath));
            Assert.NotNull(await _audiobookRepository.GetByIdAsync(audiobook.Id));
            release.SetResult();
            await holder;
        }

        [Fact]
        public async Task FilesystemDelete_RefusesWhenSemanticsCannotBeResolved()
        {
            var resolver = new Mock<IFileSystemSemanticsResolver>();
            resolver.Setup(r => r.ResolveAsync(
                    It.IsAny<string>(),
                    It.IsAny<FileSystemCaseSensitivityMode>(),
                    It.IsAny<CancellationToken>()))
                .Returns<string, FileSystemCaseSensitivityMode, CancellationToken>((path, _, _) =>
                    ValueTask.FromResult(new FileSystemSemanticsResolution(
                        new FileSystemPathSemantics(
                            FileSystemPathSemantics.CurrentHostDefault.Syntax,
                            FileSystemCaseSensitivity.Unknown),
                        PathIdentityState.Unavailable,
                        path,
                        "probe failed")));
            Init(builder => builder.WithSingleton(resolver.Object));

            var bookFolder = FileService.GetTempDirectory("listenarr-delete-unresolved");
            var audioPath = Path.Join(bookFolder, "book.mp3");
            await File.WriteAllTextAsync(audioPath, "audio");
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Blocked Book")
                .WithBasePath(bookFolder)
                .WithFilePath(audioPath)
                .Build());
            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(audioPath)
                .Build());

            var service = _provider.GetRequiredService<IAudiobookFilesystemDeleteService>();
            var result = await service.DeleteAsync(audiobook, deleteFolder: true);

            Assert.True(File.Exists(audioPath));
            Assert.True(Directory.Exists(bookFolder));
            Assert.Contains(result.Warnings, warning => warning.Contains("case sensitivity", StringComparison.OrdinalIgnoreCase));
        }

        private sealed class CancelOnBeginRemovalOwnershipStore(
            ILibraryDirectoryOwnershipStore inner,
            CancellationTokenSource cancellation) : ILibraryDirectoryOwnershipStore
        {
            public Task<LibraryDirectoryOwnership> RecordCreatedAsync(
                LibraryDirectoryOwnershipClaim claim,
                CancellationToken cancellationToken = default) =>
                inner.RecordCreatedAsync(claim, cancellationToken);

            public Task<IReadOnlyList<LibraryDirectoryOwnership>> EnsureCreatedHierarchyAsync(
                string destinationDirectory,
                string managedBoundary,
                FileSystemPathSemantics semantics,
                string creationWorkflow,
                Guid? creationOperationId = null,
                int? audiobookId = null,
                CancellationToken cancellationToken = default) =>
                inner.EnsureCreatedHierarchyAsync(
                    destinationDirectory,
                    managedBoundary,
                    semantics,
                    creationWorkflow,
                    creationOperationId,
                    audiobookId,
                    cancellationToken);

            public Task<LibraryDirectoryOwnershipResolution> ResolveOwnedAsync(
                string path,
                FileSystemPathSemantics semantics,
                CancellationToken cancellationToken = default) =>
                inner.ResolveOwnedAsync(path, semantics, cancellationToken);

            public Task<IReadOnlyList<LibraryDirectoryOwnership>> GetOwnedWithinAsync(
                string basePath,
                FileSystemPathSemantics semantics,
                CancellationToken cancellationToken = default) =>
                inner.GetOwnedWithinAsync(basePath, semantics, cancellationToken);

            public Task BeginRemovalAsync(
                long ownershipId,
                string expectedOwnershipKey,
                CancellationToken cancellationToken = default)
            {
                cancellation.Cancel();
                Assert.False(cancellationToken.CanBeCanceled);
                return inner.BeginRemovalAsync(
                    ownershipId,
                    expectedOwnershipKey,
                    cancellationToken);
            }

            public Task RetainAsync(
                long ownershipId,
                string expectedOwnershipKey,
                string? reason = null,
                CancellationToken cancellationToken = default) =>
                inner.RetainAsync(
                    ownershipId,
                    expectedOwnershipKey,
                    reason,
                    cancellationToken);

            public Task MarkRemovedAsync(
                long ownershipId,
                string expectedOwnershipKey,
                CancellationToken cancellationToken = default) =>
                inner.MarkRemovedAsync(
                    ownershipId,
                    expectedOwnershipKey,
                    cancellationToken);
        }

        private sealed class FailingNthMarkRemovedOwnershipStore(
            ILibraryDirectoryOwnershipStore inner,
            int failOnCall) : ILibraryDirectoryOwnershipStore
        {
            private int _markRemovedCalls;

            public Task<LibraryDirectoryOwnership> RecordCreatedAsync(
                LibraryDirectoryOwnershipClaim claim,
                CancellationToken cancellationToken = default) =>
                inner.RecordCreatedAsync(claim, cancellationToken);

            public Task<IReadOnlyList<LibraryDirectoryOwnership>> EnsureCreatedHierarchyAsync(
                string destinationDirectory,
                string managedBoundary,
                FileSystemPathSemantics semantics,
                string creationWorkflow,
                Guid? creationOperationId = null,
                int? audiobookId = null,
                CancellationToken cancellationToken = default) =>
                inner.EnsureCreatedHierarchyAsync(
                    destinationDirectory,
                    managedBoundary,
                    semantics,
                    creationWorkflow,
                    creationOperationId,
                    audiobookId,
                    cancellationToken);

            public Task<LibraryDirectoryOwnershipResolution> ResolveOwnedAsync(
                string path,
                FileSystemPathSemantics semantics,
                CancellationToken cancellationToken = default) =>
                inner.ResolveOwnedAsync(path, semantics, cancellationToken);

            public Task<IReadOnlyList<LibraryDirectoryOwnership>> GetOwnedWithinAsync(
                string basePath,
                FileSystemPathSemantics semantics,
                CancellationToken cancellationToken = default) =>
                inner.GetOwnedWithinAsync(basePath, semantics, cancellationToken);

            public Task BeginRemovalAsync(
                long ownershipId,
                string expectedOwnershipKey,
                CancellationToken cancellationToken = default) =>
                inner.BeginRemovalAsync(
                    ownershipId,
                    expectedOwnershipKey,
                    cancellationToken);

            public Task RetainAsync(
                long ownershipId,
                string expectedOwnershipKey,
                string? reason = null,
                CancellationToken cancellationToken = default) =>
                inner.RetainAsync(
                    ownershipId,
                    expectedOwnershipKey,
                    reason,
                    cancellationToken);

            public Task MarkRemovedAsync(
                long ownershipId,
                string expectedOwnershipKey,
                CancellationToken cancellationToken = default)
            {
                _markRemovedCalls++;
                return _markRemovedCalls == failOnCall
                    ? Task.FromException(new InvalidOperationException(
                        "Injected ownership-state persistence failure."))
                    : inner.MarkRemovedAsync(
                        ownershipId,
                        expectedOwnershipKey,
                        cancellationToken);
            }
        }
    }
}
