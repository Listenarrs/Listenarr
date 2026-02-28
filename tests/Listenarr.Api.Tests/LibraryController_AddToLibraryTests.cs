using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Listenarr.Api.Controllers;
using Listenarr.Domain.Models;
using Listenarr.Api.Services;
using Listenarr.Infrastructure.Models;

namespace Listenarr.Api.Tests
{
    public class LibraryController_AddToLibraryTests
    {
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
                });

            var mockImageCache = new Mock<IImageCacheService>();
            var mockLogger = new Mock<ILogger<LibraryController>>();

            var mockFileNaming = new Mock<IFileNamingService>();
            mockFileNaming
                .Setup(f => f.ApplyNamingPattern(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), false))
                .Returns((string pattern, Dictionary<string, object> vars, bool t) =>
                {
                    // Simulate FileNamingService producing an Author/Title relative path
                    var author = vars.ContainsKey("Author") ? vars["Author"]?.ToString() ?? "Unknown" : "Unknown";
                    var title = vars.ContainsKey("Title") ? vars["Title"]?.ToString() ?? "Unknown" : "Unknown";
                    return Path.Combine(author, title).Replace("\\", "/");
                });

            // Configuration service providing an OutputPath root
            var tempRoot = Path.Combine(Path.GetTempPath(), "listenarr-test-" + Guid.NewGuid().ToString("N"));
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
                dbContext,
                scopeFactory,
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
            try { Directory.Delete(tempRoot, true); } catch { }
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
                });

            var mockImageCache = new Mock<IImageCacheService>();
            var asin = "B000TEST01";
            var originalUrl = "http://example.com/a1.jpg";
            mockImageCache.Setup(m => m.MoveToLibraryStorageAsync(asin, originalUrl)).ReturnsAsync("config/cache/images/library/B000TEST01.jpg");

            var mockLogger = new Mock<ILogger<LibraryController>>();

            var mockFileNaming = new Mock<IFileNamingService>();
            mockFileNaming
                .Setup(f => f.ApplyNamingPattern(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), false))
                .Returns((string pattern, Dictionary<string, object> vars, bool t) =>
                {
                    var author = vars.ContainsKey("Author") ? vars["Author"]?.ToString() ?? "Unknown" : "Unknown";
                    var title = vars.ContainsKey("Title") ? vars["Title"]?.ToString() ?? "Unknown" : "Unknown";
                    return Path.Combine(author, title).Replace("\\", "/");
                });

            // Configuration service providing an OutputPath root
            var tempRoot = Path.Combine(Path.GetTempPath(), "listenarr-test-" + Guid.NewGuid().ToString("N"));
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
                dbContext,
                scopeFactory,
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
            try { Directory.Delete(tempRoot, true); } catch { }
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
                });

            var mockImageCache = new Mock<IImageCacheService>();
            var imageUrl = "http://example.com/a2.jpg";
            mockImageCache.Setup(m => m.MoveToLibraryStorageAsync(It.IsAny<string>(), imageUrl)).ReturnsAsync("config/cache/images/library/derived.jpg");

            var mockLogger = new Mock<ILogger<LibraryController>>();

            var mockFileNaming = new Mock<IFileNamingService>();
            mockFileNaming
                .Setup(f => f.ApplyNamingPattern(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), false))
                .Returns((string pattern, Dictionary<string, object> vars, bool t) =>
                {
                    var author = vars.ContainsKey("Author") ? vars["Author"]?.ToString() ?? "Unknown" : "Unknown";
                    var title = vars.ContainsKey("Title") ? vars["Title"]?.ToString() ?? "Unknown" : "Unknown";
                    return Path.Combine(author, title).Replace("\\", "/");
                });

            // Configuration service providing an OutputPath root
            var tempRoot = Path.Combine(Path.GetTempPath(), "listenarr-test-" + Guid.NewGuid().ToString("N"));
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
                dbContext,
                scopeFactory,
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
            try { Directory.Delete(tempRoot, true); } catch { }
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
                dbContext,
                scopeFactory,
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
            Assert.Equal(customPath, stored.BasePath);
        }
    }
}
