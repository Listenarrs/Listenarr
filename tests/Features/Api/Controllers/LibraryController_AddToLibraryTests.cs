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
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Listenarr.Api.Controllers;
using Listenarr.Domain.Models;
using Listenarr.Api.Services;
using Listenarr.Application.Repositories;
using Listenarr.Infrastructure.Models;

namespace Listenarr.Tests.Features.Api.Controllers
{
    public class LibraryController_AddToLibraryTests
    {
        private static void TryDeleteDirectory(string path)
        {
            try
            {
                Directory.Delete(path, true);
            }
            catch (IOException ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }
        }

        private static string BuildLibraryPath(Dictionary<string, object> vars)
        {
            vars.TryGetValue("Author", out var authorObj);
            vars.TryGetValue("Title", out var titleObj);

            var author = authorObj?.ToString() ?? "Unknown";
            var title = titleObj?.ToString() ?? "Unknown";

            return Path.Join(author, title).Replace("\\", "/");
        }

        [Fact]
        public async Task AddToLibrary_UsesLegacyAuthorField_PopulatesAuthorsAndBasePath()
        {
            // Arrange
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var dbContext = new ListenArrDbContext(options);

            var mockRepo = new Mock<IAudiobookRepository>();
            mockRepo.Setup(r => r.AddAsync(It.IsAny<Audiobook>()))
                .Returns<Audiobook>(async (ab) =>
                {
                    await dbContext.Audiobooks.AddAsync(ab);
                    await dbContext.SaveChangesAsync();
                    return ab;
                });

            var mockImageCache = new Mock<IImageCacheService>();
            var mockLogger = new Mock<ILogger<LibraryController>>();

            var mockFileNaming = new Mock<IFileNamingService>();
            mockFileNaming
                .Setup(f => f.ApplyNamingPattern(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), false))
                .Returns((string pattern, Dictionary<string, object> vars, bool t) => BuildLibraryPath(vars));

            // Configuration service providing an OutputPath root
            var tempRoot = Path.Join(Path.GetTempPath(), "listenarr-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);

            var mockConfigService = new Mock<IConfigurationService>();
            mockConfigService.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings { OutputPath = tempRoot, FileNamingPattern = "{Author}/{Title}" });

            var mockQualityProfile = new Mock<IQualityProfileService>();
            mockQualityProfile.Setup(q => q.GetDefaultAsync()).ReturnsAsync((QualityProfile?)null);

            var services = new ServiceCollection();
            services.AddSingleton<IConfigurationService>(mockConfigService.Object);
            services.AddSingleton<IQualityProfileService>(mockQualityProfile.Object);
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var controller = new LibraryController(
                mockRepo.Object,
                mockImageCache.Object,
                mockLogger.Object,
                scopeFactory,
                new Mock<IHistoryRepository>().Object,
                new Mock<IAudiobookFileRepository>().Object,
                new Mock<IQualityProfileRepository>().Object,
                new Mock<IDownloadRepository>().Object,
                new Mock<IRootFolderRepository>().Object,
                mockFileNaming.Object);

            var request = new LibraryController.AddToLibraryRequest
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "Legacy Title",
                    Author = "Legacy Author"
                },
                Monitored = true
            };

            // Act
            var actionResult = await controller.AddToLibrary(request);

            // Assert
            Assert.IsType<OkObjectResult>(actionResult);

            var stored = await dbContext.Audiobooks.FirstOrDefaultAsync();
            Assert.NotNull(stored);
            Assert.NotNull(stored.Authors);
            Assert.Contains("Legacy Author", stored.Authors);
            // BasePath should only be set when a custom destination path is explicitly provided
            // When no custom path is given, ImportService uses the default file naming pattern from settings
            Assert.True(string.IsNullOrWhiteSpace(stored.BasePath), "BasePath should be null when no custom destination is provided");

            // Cleanup
            TryDeleteDirectory(tempRoot);
        }

        [Fact]
        public async Task AddToLibrary_PersistsEditableMetadataFields()
        {
            // Arrange
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var dbContext = new ListenArrDbContext(options);

            var mockRepo = new Mock<IAudiobookRepository>();
            mockRepo.Setup(r => r.AddAsync(It.IsAny<Audiobook>()))
                .Returns<Audiobook>(async (ab) =>
                {
                    await dbContext.Audiobooks.AddAsync(ab);
                    await dbContext.SaveChangesAsync();
                    return ab;
                });

            var mockImageCache = new Mock<IImageCacheService>();
            var mockLogger = new Mock<ILogger<LibraryController>>();

            var mockFileNaming = new Mock<IFileNamingService>();
            mockFileNaming
                .Setup(f => f.ApplyNamingPattern(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), false))
                .Returns((string pattern, Dictionary<string, object> vars, bool t) => BuildLibraryPath(vars));

            var tempRoot = Path.Join(Path.GetTempPath(), "listenarr-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);

            var mockConfigService = new Mock<IConfigurationService>();
            mockConfigService.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings { OutputPath = tempRoot, FileNamingPattern = "{Author}/{Title}" });

            var mockQualityProfile = new Mock<IQualityProfileService>();
            mockQualityProfile.Setup(q => q.GetDefaultAsync()).ReturnsAsync((QualityProfile?)null);

            var services = new ServiceCollection();
            services.AddSingleton<IConfigurationService>(mockConfigService.Object);
            services.AddSingleton<IQualityProfileService>(mockQualityProfile.Object);
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var controller = new LibraryController(
                mockRepo.Object,
                mockImageCache.Object,
                mockLogger.Object,
                scopeFactory,
                new Mock<IHistoryRepository>().Object,
                new Mock<IAudiobookFileRepository>().Object,
                new Mock<IQualityProfileRepository>().Object,
                new Mock<IDownloadRepository>().Object,
                new Mock<IRootFolderRepository>().Object,
                mockFileNaming.Object);

            var request = new LibraryController.AddToLibraryRequest
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "Editable Title",
                    Subtitle = "Editable Subtitle",
                    Authors = new List<string> { "Edited Author" },
                    Narrators = new List<string> { "Edited Narrator" },
                    Publisher = "Edited Publisher",
                    Language = "english",
                    Runtime = 615,
                    Edition = "Collector's Edition",
                    Version = "Audible Version",
                    Asin = "B00EDIT123",
                    Isbn = new List<string> { "9781234567890" },
                    OpenLibraryId = "OL12345M"
                },
                Monitored = true
            };

            // Act
            var actionResult = await controller.AddToLibrary(request);

            // Assert
            Assert.IsType<OkObjectResult>(actionResult);

            var stored = await dbContext.Audiobooks.FirstOrDefaultAsync();
            Assert.NotNull(stored);
            Assert.Equal("Editable Title", stored.Title);
            Assert.Equal("Editable Subtitle", stored.Subtitle);
            Assert.Equal("Edited Publisher", stored.Publisher);
            Assert.Equal("english", stored.Language);
            Assert.Equal(615, stored.Runtime);
            Assert.Equal("Collector's Edition", stored.Edition);
            Assert.Equal("Audible Version", stored.Version);
            Assert.Equal("B00EDIT123", stored.Asin);
            Assert.Equal("OL12345M", stored.OpenLibraryId);
            Assert.NotNull(stored.Authors);
            Assert.Contains("Edited Author", stored.Authors);
            Assert.NotNull(stored.Narrators);
            Assert.Contains("Edited Narrator", stored.Narrators);
            Assert.NotNull(stored.Isbn);
            Assert.Contains("9781234567890", stored.Isbn);

            // Cleanup
            try
            {
                Directory.Delete(tempRoot, true);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"Ignoring cleanup failure for '{tempRoot}': {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.Error.WriteLine($"Ignoring cleanup failure for '{tempRoot}': {ex.Message}");
            }
        }

        [Fact]
        public async Task AddToLibrary_WithAsin_MovesImageToLibraryStorage()
        {
            // Arrange
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var dbContext = new ListenArrDbContext(options);

            var mockRepo = new Mock<IAudiobookRepository>();
            mockRepo.Setup(r => r.AddAsync(It.IsAny<Audiobook>()))
                .Returns<Audiobook>(async (ab) =>
                {
                    await dbContext.Audiobooks.AddAsync(ab);
                    await dbContext.SaveChangesAsync();
                    return ab;
                });

            var mockImageCache = new Mock<IImageCacheService>();
            var asin = "B000TEST01";
            var originalUrl = "http://example.com/a1.jpg";
            mockImageCache.Setup(m => m.MoveToLibraryStorageAsync(asin, originalUrl)).ReturnsAsync("config/cache/images/library/B000TEST01.jpg");

            var mockLogger = new Mock<ILogger<LibraryController>>();

            var mockFileNaming = new Mock<IFileNamingService>();
            mockFileNaming
                .Setup(f => f.ApplyNamingPattern(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), false))
                .Returns((string pattern, Dictionary<string, object> vars, bool t) => BuildLibraryPath(vars));

            // Configuration service providing an OutputPath root
            var tempRoot = Path.Join(Path.GetTempPath(), "listenarr-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);

            var mockConfigService = new Mock<IConfigurationService>();
            mockConfigService.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings { OutputPath = tempRoot, FileNamingPattern = "{Author}/{Title}" });

            var mockQualityProfile = new Mock<IQualityProfileService>();
            mockQualityProfile.Setup(q => q.GetDefaultAsync()).ReturnsAsync((QualityProfile?)null);

            var services = new ServiceCollection();
            services.AddSingleton<IConfigurationService>(mockConfigService.Object);
            services.AddSingleton<IQualityProfileService>(mockQualityProfile.Object);
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var controller = new LibraryController(
                mockRepo.Object,
                mockImageCache.Object,
                mockLogger.Object,
                scopeFactory,
                new Mock<IHistoryRepository>().Object,
                new Mock<IAudiobookFileRepository>().Object,
                new Mock<IQualityProfileRepository>().Object,
                new Mock<IDownloadRepository>().Object,
                new Mock<IRootFolderRepository>().Object,
                mockFileNaming.Object);

            var request = new LibraryController.AddToLibraryRequest
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "Move Test",
                    Author = "A Uthor",
                    Asin = asin,
                    ImageUrl = originalUrl
                },
                Monitored = true
            };

            // Act
            var actionResult = await controller.AddToLibrary(request);

            // Assert
            Assert.IsType<OkObjectResult>(actionResult);

            var stored = await dbContext.Audiobooks.FirstOrDefaultAsync();
            Assert.NotNull(stored);
            Assert.Equal($"/config/cache/images/library/B000TEST01.jpg", stored.ImageUrl);
            mockImageCache.Verify(m => m.MoveToLibraryStorageAsync(asin, originalUrl), Times.Once);

            // Cleanup
            TryDeleteDirectory(tempRoot);
        }

        [Fact]
        public async Task AddToLibrary_WithoutAsin_UsesDerivedKey_AndMovesImageToLibraryStorage()
        {
            // Arrange
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var dbContext = new ListenArrDbContext(options);

            var mockRepo = new Mock<IAudiobookRepository>();
            mockRepo.Setup(r => r.AddAsync(It.IsAny<Audiobook>()))
                .Returns<Audiobook>(async (ab) =>
                {
                    await dbContext.Audiobooks.AddAsync(ab);
                    await dbContext.SaveChangesAsync();
                    return ab;
                });

            var mockImageCache = new Mock<IImageCacheService>();
            var imageUrl = "http://example.com/a2.jpg";
            mockImageCache.Setup(m => m.MoveToLibraryStorageAsync(It.IsAny<string>(), imageUrl)).ReturnsAsync("config/cache/images/library/derived.jpg");

            var mockLogger = new Mock<ILogger<LibraryController>>();

            var mockFileNaming = new Mock<IFileNamingService>();
            mockFileNaming
                .Setup(f => f.ApplyNamingPattern(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), false))
                .Returns((string pattern, Dictionary<string, object> vars, bool t) => BuildLibraryPath(vars));

            // Configuration service providing an OutputPath root
            var tempRoot = Path.Join(Path.GetTempPath(), "listenarr-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);

            var mockConfigService = new Mock<IConfigurationService>();
            mockConfigService.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings { OutputPath = tempRoot, FileNamingPattern = "{Author}/{Title}" });

            var mockQualityProfile = new Mock<IQualityProfileService>();
            mockQualityProfile.Setup(q => q.GetDefaultAsync()).ReturnsAsync((QualityProfile?)null);

            var services = new ServiceCollection();
            services.AddSingleton<IConfigurationService>(mockConfigService.Object);
            services.AddSingleton<IQualityProfileService>(mockQualityProfile.Object);
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var controller = new LibraryController(
                mockRepo.Object,
                mockImageCache.Object,
                mockLogger.Object,
                scopeFactory,
                new Mock<IHistoryRepository>().Object,
                new Mock<IAudiobookFileRepository>().Object,
                new Mock<IQualityProfileRepository>().Object,
                new Mock<IDownloadRepository>().Object,
                new Mock<IRootFolderRepository>().Object,
                mockFileNaming.Object);

            var request = new LibraryController.AddToLibraryRequest
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "Derived Test",
                    Author = "Some Author",
                    ImageUrl = imageUrl
                },
                Monitored = true
            };

            // Act
            var actionResult = await controller.AddToLibrary(request);

            // Assert
            Assert.IsType<OkObjectResult>(actionResult);

            var stored = await dbContext.Audiobooks.FirstOrDefaultAsync();
            Assert.NotNull(stored);
            Assert.Equal($"/config/cache/images/library/derived.jpg", stored.ImageUrl);
            mockImageCache.Verify(m => m.MoveToLibraryStorageAsync(It.IsAny<string>(), imageUrl), Times.Once);

            // Cleanup
            TryDeleteDirectory(tempRoot);
        }

        [Fact]
        public async Task AddToLibrary_WithCustomPath_StoresCustomPathAsBasePath()
        {
            // Arrange
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var dbContext = new ListenArrDbContext(options);

            var mockRepo = new Mock<IAudiobookRepository>();
            mockRepo.Setup(r => r.AddAsync(It.IsAny<Audiobook>()))
                .Returns<Audiobook>(async (ab) =>
                {
                    await dbContext.Audiobooks.AddAsync(ab);
                    await dbContext.SaveChangesAsync();
                    return ab;
                });

            var mockImageCache = new Mock<IImageCacheService>();
            var mockLogger = new Mock<ILogger<LibraryController>>();
            var mockFileNaming = new Mock<IFileNamingService>();

            var mockConfigService = new Mock<IConfigurationService>();
            mockConfigService.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings { OutputPath = "/default/path", FileNamingPattern = "{Author}/{Title}" });

            var mockQualityProfile = new Mock<IQualityProfileService>();
            mockQualityProfile.Setup(q => q.GetDefaultAsync()).ReturnsAsync((QualityProfile?)null);

            var services = new ServiceCollection();
            services.AddSingleton<IConfigurationService>(mockConfigService.Object);
            services.AddSingleton<IQualityProfileService>(mockQualityProfile.Object);
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var controller = new LibraryController(
                mockRepo.Object,
                mockImageCache.Object,
                mockLogger.Object,
                scopeFactory,
                new Mock<IHistoryRepository>().Object,
                new Mock<IAudiobookFileRepository>().Object,
                new Mock<IQualityProfileRepository>().Object,
                new Mock<IDownloadRepository>().Object,
                new Mock<IRootFolderRepository>().Object,
                mockFileNaming.Object);

            var customPath = "/custom/audiobooks/Author/Series/Title";
            var request = new LibraryController.AddToLibraryRequest
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "Custom Path Test",
                    Author = "Custom Author"
                },
                Monitored = true,
                DestinationPath = customPath  // Custom path provided
            };

            // Act
            var actionResult = await controller.AddToLibrary(request);

            // Assert
            Assert.IsType<OkObjectResult>(actionResult);

            var stored = await dbContext.Audiobooks.FirstOrDefaultAsync();
            Assert.NotNull(stored);
            // NormalizeStoredPath calls Path.GetFullPath which is platform-dependent:
            // on Windows "/custom/..." becomes "C:\custom\...", on Linux it stays "/custom/..."
            var expectedPath = Path.GetFullPath(customPath);
            Assert.Equal(expectedPath, stored.BasePath);
        }
    }
}
