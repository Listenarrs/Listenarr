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
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Listenarr.Api.Controllers;
using Listenarr.Domain.Models;
using Listenarr.Api.Services;
using Listenarr.Infrastructure.Models;
using Listenarr.Infrastructure.Repositories;
using Listenarr.Application.Repositories;

namespace Listenarr.Tests.Features.Api.Controllers
{
    public class LibraryController_BulkUpdateTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ListenArrDbContext _dbContext;

        public LibraryController_BulkUpdateTests()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(_connection)
                .Options;
            _dbContext = new ListenArrDbContext(options);
            _dbContext.Database.EnsureCreated();

            // Seed the quality profile that the bulk update test will reference via QualityProfileId = 42
            _dbContext.QualityProfiles.Add(new QualityProfile
            {
                Id = 42,
                Name = "Test Profile",
                Qualities = new List<QualityDefinition>(),
                PreferredFormats = new List<string>(),
                PreferredLanguages = new List<string>(),
                MustContain = new List<string>(),
                MustNotContain = new List<string>()
            });
            _dbContext.SaveChanges();
        }

        public void Dispose()
        {
            _dbContext.Dispose();
            _connection.Dispose();
        }

        [Fact]
        public async Task BulkUpdate_ApplyRootMonitoredQuality_ReturnsPerIdResultsAndPersistsChanges()
        {
            // Arrange
            var dbContext = _dbContext;
            // Create two audiobooks in DB
            var a1 = new Audiobook
            {
                Title = "Book A",
                Authors = new List<string> { "Author A" },
                Monitored = false,
                QualityProfileId = null
            };

            var a2 = new Audiobook
            {
                Title = "Book B",
                Authors = new List<string> { "Author B" },
                Monitored = false,
                QualityProfileId = null
            };

            await dbContext.Audiobooks.AddAsync(a1);
            await dbContext.Audiobooks.AddAsync(a2);
            await dbContext.SaveChangesAsync();

            // Mock repository to return our DB entries by id
            var mockRepo = new Mock<IAudiobookRepository>();
            mockRepo.Setup(r => r.GetByIdAsync(a1.Id)).ReturnsAsync(a1);
            mockRepo.Setup(r => r.GetByIdAsync(a2.Id)).ReturnsAsync(a2);

            var mockImageCache = new Mock<IImageCacheService>();
            var mockLogger = new Mock<ILogger<LibraryController>>();

            var mockFileNaming = new Mock<IFileNamingService>();
            mockFileNaming
                .Setup(f => f.ApplyNamingPattern(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), false))
                .Returns((string pattern, Dictionary<string, object> vars, bool sanitize) =>
                {
                    vars.TryGetValue("Author", out var authorObj);
                    vars.TryGetValue("Title", out var titleObj);
                    var author = authorObj?.ToString() ?? "Unknown";
                    var title = titleObj?.ToString() ?? "Unknown";
                    return Path.Join(author, title).Replace("\\", "/");
                });

            // Configuration service providing a FileNamingPattern (not strictly used by our mock but kept consistent)
            var tempRoot = Path.Join(Path.GetTempPath(), "listenarr-bulk-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);

            var mockConfigService = new Mock<IConfigurationService>();
            mockConfigService.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings { OutputPath = tempRoot, FileNamingPattern = "{Author}/{Title}" });

            var services = new ServiceCollection();
            services.AddSingleton<IConfigurationService>(mockConfigService.Object);
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            // Create controller instance
            var controller = new LibraryController(
                mockRepo.Object,
                mockImageCache.Object,
                mockLogger.Object,
                scopeFactory,
                new EfHistoryRepository(dbContext),
                new Mock<IAudiobookFileRepository>().Object,
                new Mock<IQualityProfileRepository>().Object,
                new Mock<IDownloadRepository>().Object,
                new Mock<IRootFolderRepository>().Object,
                mockFileNaming.Object);

            // Build request: update monitored + qualityProfileId + rootFolder (include a non-existent id)
            var request = new LibraryController.BulkUpdateRequest
            {
                Ids = new List<int> { a1.Id, 999999 },
                Updates = new Dictionary<string, object>
                {
                    { "monitored", true },
                    { "qualityProfileId", 42 },
                    { "rootFolder", tempRoot }
                }
            };

            // Act
            var actionResult = await controller.BulkUpdateAudiobooks(request);

            // Assert
            var ok = Assert.IsType<OkObjectResult>(actionResult);

            // Inspect returned JSON for per-id results
            var json = JsonSerializer.Serialize(ok.Value);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("results", out var resultsElem));
            Assert.Equal(2, resultsElem.GetArrayLength());

            // First result should be success for existing audiobook
            var first = resultsElem[0];
            Assert.Equal(a1.Id, first.GetProperty("id").GetInt32());
            Assert.True(first.GetProperty("success").GetBoolean());
            Assert.True(first.GetProperty("errors").GetArrayLength() == 0);

            // Second result should indicate not found
            var second = resultsElem[1];
            Assert.Equal(999999, second.GetProperty("id").GetInt32());
            Assert.False(second.GetProperty("success").GetBoolean());
            Assert.True(second.GetProperty("errors").GetArrayLength() >= 1);

            // Verify DB changes persisted for a1
            var storedA1 = await dbContext.Audiobooks.FindAsync(a1.Id);
            Assert.NotNull(storedA1);
            Assert.True(storedA1.Monitored);
            Assert.Equal(42, storedA1.QualityProfileId);
            Assert.False(string.IsNullOrWhiteSpace(storedA1.BasePath));
            Assert.StartsWith(tempRoot, storedA1.BasePath);
            Assert.Contains("Author A", storedA1.BasePath);
            Assert.Contains("Book A", storedA1.BasePath);

            // Verify history entry exists for the change
            var histories = dbContext.History.Where(h => h.AudiobookId == a1.Id).ToList();
            Assert.True(histories.Count >= 1);

            // Cleanup
            try { Directory.Delete(tempRoot, true); } catch (IOException ex) { _ = ex; } catch (UnauthorizedAccessException ex) { _ = ex; }
        }
    }
}
