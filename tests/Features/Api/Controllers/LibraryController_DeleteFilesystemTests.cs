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
using Listenarr.Application.Common;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Domain.Models;
using Listenarr.Domain.Models.Configurations;
using Listenarr.Infrastructure.Persistence;
using Listenarr.Infrastructure.Persistence.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Controllers
{
    public class LibraryController_DeleteFilesystemTests
    {
        [Fact]
        public async Task DeleteAudiobook_DeleteFiles_RemovesAllFilesInFolderButPreservesDirectory()
        {
            var tempRoot = Path.Join(Path.GetTempPath(), "listenarr-delete-" + Guid.NewGuid().ToString("N"));
            var bookFolder = Path.Join(tempRoot, "Jack of Shadows");
            var extrasFolder = Path.Join(bookFolder, "Extras");
            var audioPath = Path.Join(bookFolder, "Jack of Shadows.mp3");
            var sidecarPath = Path.Join(bookFolder, "cover.jpg");
            var notePath = Path.Join(extrasFolder, "notes.txt");

            Directory.CreateDirectory(extrasFolder);
            await File.WriteAllTextAsync(audioPath, "audio");
            await File.WriteAllTextAsync(sidecarPath, "cover");
            await File.WriteAllTextAsync(notePath, "notes");

            try
            {
                await using var dbContext = CreateDbContext();
                var audiobook = new Audiobook
                {
                    Id = 50,
                    Title = "Jack of Shadows",
                    BasePath = bookFolder,
                    FilePath = audioPath,
                    Files = new List<AudiobookFile>
                    {
                        new AudiobookFile
                        {
                            Id = 51,
                            AudiobookId = 50,
                            Path = audioPath
                        }
                    }
                };

                dbContext.Audiobooks.Add(audiobook);
                dbContext.AudiobookFiles.AddRange(audiobook.Files);
                await dbContext.SaveChangesAsync();

                var controller = CreateController(dbContext);

                var result = await controller.DeleteAudiobook(audiobook.Id, deleteFiles: true, deleteFolder: false);

                var ok = Assert.IsType<OkObjectResult>(result);
                Assert.Equal(200, ok.StatusCode ?? 200);
                Assert.False(File.Exists(audioPath));
                Assert.False(File.Exists(sidecarPath));
                Assert.False(File.Exists(notePath));
                Assert.True(Directory.Exists(bookFolder));
                Assert.False(Directory.Exists(extrasFolder));
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public async Task DeleteAudiobook_DeleteFilesAndFolder_RemovesTrackedFilesAndDirectory()
        {
            var tempRoot = Path.Join(Path.GetTempPath(), "listenarr-delete-" + Guid.NewGuid().ToString("N"));
            var bookFolder = Path.Join(tempRoot, "Jack of Shadows");
            var audioPath = Path.Join(bookFolder, "Jack of Shadows.mp3");
            var sidecarPath = Path.Join(bookFolder, "cover.jpg");

            Directory.CreateDirectory(bookFolder);
            await File.WriteAllTextAsync(audioPath, "audio");
            await File.WriteAllTextAsync(sidecarPath, "cover");

            try
            {
                await using var dbContext = CreateDbContext();
                var audiobook = new Audiobook
                {
                    Id = 1,
                    Title = "Jack of Shadows",
                    BasePath = bookFolder,
                    FilePath = audioPath,
                    Files = new List<AudiobookFile>
                    {
                        new AudiobookFile
                        {
                            Id = 11,
                            AudiobookId = 1,
                            Path = audioPath
                        }
                    }
                };

                dbContext.Audiobooks.Add(audiobook);
                dbContext.AudiobookFiles.AddRange(audiobook.Files);
                await dbContext.SaveChangesAsync();

                var controller = CreateController(dbContext);

                var result = await controller.DeleteAudiobook(audiobook.Id, deleteFiles: true, deleteFolder: true);

                var ok = Assert.IsType<OkObjectResult>(result);
                Assert.Equal(200, ok.StatusCode ?? 200);
                Assert.False(File.Exists(audioPath));
                Assert.False(Directory.Exists(bookFolder));
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public async Task DeleteAudiobook_DeleteFolder_PreservesSharedDirectoryWhenAnotherAudiobookUsesIt()
        {
            var tempRoot = Path.Join(Path.GetTempPath(), "listenarr-delete-" + Guid.NewGuid().ToString("N"));
            var sharedFolder = Path.Join(tempRoot, "Shared");
            var currentAudioPath = Path.Join(sharedFolder, "current.mp3");
            var otherAudioPath = Path.Join(sharedFolder, "other.mp3");

            Directory.CreateDirectory(sharedFolder);
            await File.WriteAllTextAsync(currentAudioPath, "audio");
            await File.WriteAllTextAsync(otherAudioPath, "audio");

            try
            {
                await using var dbContext = CreateDbContext();
                var current = new Audiobook
                {
                    Id = 1,
                    Title = "Current",
                    BasePath = sharedFolder,
                    FilePath = currentAudioPath,
                    Files = new List<AudiobookFile>
                    {
                        new AudiobookFile
                        {
                            Id = 21,
                            AudiobookId = 1,
                            Path = currentAudioPath
                        }
                    }
                };
                var other = new Audiobook
                {
                    Id = 2,
                    Title = "Other",
                    BasePath = sharedFolder,
                    FilePath = otherAudioPath,
                    Files = new List<AudiobookFile>
                    {
                        new AudiobookFile
                        {
                            Id = 22,
                            AudiobookId = 2,
                            Path = otherAudioPath
                        }
                    }
                };

                dbContext.Audiobooks.AddRange(current, other);
                dbContext.AudiobookFiles.AddRange(current.Files.Concat(other.Files));
                await dbContext.SaveChangesAsync();

                var controller = CreateController(dbContext);

                var result = await controller.DeleteAudiobook(current.Id, deleteFiles: true, deleteFolder: true);

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
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public async Task DeleteAudiobook_DeleteFolder_UsesTrackedFileCommonDirectoryWhenBasePathIsProtectedRoot()
        {
            var tempRoot = Path.Join(Path.GetTempPath(), "listenarr-delete-" + Guid.NewGuid().ToString("N"));
            var bookFolder = Path.Join(tempRoot, "Roger Zelazny", "Jack of Shadows");
            var discFolder = Path.Join(bookFolder, "Disc 01");
            var audioPath = Path.Join(discFolder, "Jack of Shadows-01.mp3");

            Directory.CreateDirectory(discFolder);
            await File.WriteAllTextAsync(audioPath, "audio");

            try
            {
                await using var dbContext = CreateDbContext();
                dbContext.RootFolders.Add(new RootFolder
                {
                    Id = 500,
                    Name = "Library",
                    Path = tempRoot,
                    IsDefault = true
                });

                var audiobook = new Audiobook
                {
                    Id = 10,
                    Title = "Jack of Shadows",
                    BasePath = tempRoot,
                    FilePath = audioPath,
                    Files = new List<AudiobookFile>
                    {
                        new AudiobookFile
                        {
                            Id = 31,
                            AudiobookId = 10,
                            Path = audioPath
                        }
                    }
                };

                dbContext.Audiobooks.Add(audiobook);
                dbContext.AudiobookFiles.AddRange(audiobook.Files);
                await dbContext.SaveChangesAsync();

                var controller = CreateController(dbContext);

                var result = await controller.DeleteAudiobook(audiobook.Id, deleteFiles: true, deleteFolder: true);

                var ok = Assert.IsType<OkObjectResult>(result);
                var deletedFolderValue = ok.Value?.GetType().GetProperty("deletedFolder")?.GetValue(ok.Value);
                var deletedFolder = deletedFolderValue is bool flag ? flag : (bool?)null;

                Assert.False(File.Exists(audioPath));
                Assert.False(Directory.Exists(bookFolder));
                Assert.True(Directory.Exists(tempRoot));
                Assert.True(deletedFolder ?? false);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        [Fact]
        public async Task DeleteAudiobook_DeleteFolder_RemovesEmptyAuthorFolder()
        {
            var tempRoot = Path.Join(Path.GetTempPath(), "listenarr-delete-" + Guid.NewGuid().ToString("N"));
            var authorFolder = Path.Join(tempRoot, "Roger Zelazny");
            var bookFolder = Path.Join(authorFolder, "Jack of Shadows");
            var audioPath = Path.Join(bookFolder, "Jack of Shadows.mp3");

            Directory.CreateDirectory(bookFolder);
            await File.WriteAllTextAsync(audioPath, "audio");

            try
            {
                await using var dbContext = CreateDbContext();
                dbContext.RootFolders.Add(new RootFolder
                {
                    Id = 600,
                    Name = "Library",
                    Path = tempRoot,
                    IsDefault = true
                });

                var audiobook = new Audiobook
                {
                    Id = 12,
                    Title = "Jack of Shadows",
                    Authors = new List<string> { "Roger Zelazny" },
                    BasePath = bookFolder,
                    FilePath = audioPath,
                    Files = new List<AudiobookFile>
                    {
                        new AudiobookFile
                        {
                            Id = 41,
                            AudiobookId = 12,
                            Path = audioPath
                        }
                    }
                };

                dbContext.Audiobooks.Add(audiobook);
                dbContext.AudiobookFiles.AddRange(audiobook.Files);
                await dbContext.SaveChangesAsync();

                var controller = CreateController(dbContext);

                var result = await controller.DeleteAudiobook(audiobook.Id, deleteFiles: true, deleteFolder: true);

                var ok = Assert.IsType<OkObjectResult>(result);
                var deletedParentFolderValue = ok.Value?.GetType().GetProperty("deletedParentFolder")?.GetValue(ok.Value);
                var deletedParentFolder = deletedParentFolderValue is bool flag ? flag : (bool?)null;

                Assert.False(File.Exists(audioPath));
                Assert.False(Directory.Exists(bookFolder));
                Assert.False(Directory.Exists(authorFolder));
                Assert.True(Directory.Exists(tempRoot));
                Assert.True(deletedParentFolder ?? false);
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
            }
        }

        private static ListenArrDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            return new ListenArrDbContext(options);
        }

        private static LibraryController CreateController(ListenArrDbContext dbContext)
        {
            var repo = new Mock<IAudiobookRepository>();
            repo.Setup(r => r.GetByIdAsync(It.IsAny<int>()))
                .ReturnsAsync((int id) => dbContext.Audiobooks.Include(a => a.Files).FirstOrDefault(a => a.Id == id));
            repo.Setup(r => r.GetAllAsync())
                .ReturnsAsync(() => dbContext.Audiobooks.ToList());
            repo.Setup(r => r.DeleteByIdAsync(It.IsAny<int>()))
                .ReturnsAsync((int id) =>
                {
                    var audiobook = dbContext.Audiobooks.FirstOrDefault(a => a.Id == id);
                    if (audiobook == null)
                    {
                        return false;
                    }

                    dbContext.Audiobooks.Remove(audiobook);
                    dbContext.SaveChanges();
                    return true;
                });

            var imageCache = new Mock<IImageCacheService>();
            var logger = new Mock<ILogger<LibraryController>>();
            var fileNaming = new Mock<IFileNamingService>();

            var services = new ServiceCollection();
            var config = new Mock<IConfigurationService>();
            config.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings { OutputPath = Path.GetTempPath() });
            services.AddSingleton<IConfigurationService>(config.Object);

            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            return new LibraryController(
                repo.Object,
                imageCache.Object,
                logger.Object,
                scopeFactory,
                new Mock<IHistoryRepository>().Object,
                new EfAudiobookFileRepository(dbContext),
                new Mock<IQualityProfileRepository>().Object,
                new Mock<IDownloadRepository>().Object,
                CreateRootFolderRepo(dbContext),
                fileNaming.Object,
                scanQueueService: null,
                moveQueueService: null,
                notificationService: null,
                rootFolderService: null);
        }

        private static IRootFolderRepository CreateRootFolderRepo(ListenArrDbContext dbContext)
        {
            var mock = new Mock<IRootFolderRepository>();
            mock.Setup(r => r.GetAllAsync())
                .ReturnsAsync(() => dbContext.RootFolders.ToList());
            return mock.Object;
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _ = ex;
            }
        }
    }
}
