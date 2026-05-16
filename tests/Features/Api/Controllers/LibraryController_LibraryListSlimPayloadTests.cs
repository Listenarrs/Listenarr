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
using Listenarr.Api.Controllers;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Domain.Models;
using Listenarr.Domain.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Listenarr.Application.Interfaces;
using Listenarr.Infrastructure.Persistence;
using Listenarr.Application.Common;

namespace Listenarr.Tests.Features.Api.Controllers
{
    public class LibraryController_LibraryListSlimPayloadTests
    {
        [Fact]
        public async Task GetAll_ReturnsSlimPayload_WithServerComputedStatus()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);

            var book = new Audiobook
            {
                Title = "Slim Book",
                Authors = new System.Collections.Generic.List<string> { "Author One" },
                Genres = new System.Collections.Generic.List<string> { "Fantasy", "Adventure" },
                Monitored = true,
                Description = "Detail-only field",
                Subtitle = "Detail Subtitle",
                BasePath = FileUtils.GetAbsolutePath("library", "Slim Book"),
                FilePath = FileUtils.GetAbsolutePath("library", "Slim Book", "book.m4b"),
                FileSize = 12345,
                OpenLibraryId = "OL123",
                AuthorAsins = new System.Collections.Generic.List<string> { "AUTHORASIN1" }
            };
            db.Audiobooks.Add(book);
            await db.SaveChangesAsync();

            db.AudiobookFiles.Add(new AudiobookFile
            {
                AudiobookId = book.Id,
                Path = book.FilePath,
                Size = book.FileSize,
                Format = "m4b",
                CreatedAt = DateTime.UtcNow
            });
            db.Downloads.Add(new Download
            {
                AudiobookId = book.Id,
                Title = book.Title ?? string.Empty,
                Artist = "Author One",
                Album = book.Title ?? string.Empty,
                DownloadClientId = "TEST",
                OriginalUrl = "https://example.invalid",
                DownloadPath = FileUtils.GetAbsolutePath("downloads"),
                FinalPath = book.FilePath ?? string.Empty,
                StartedAt = DateTime.UtcNow,
                Status = DownloadStatus.Downloading
            });
            await db.SaveChangesAsync();

            var allBooks = db.Audiobooks.ToList();
            var allFiles = db.AudiobookFiles.ToList();
            var allDownloads = db.Downloads.ToList();

            var mockRepo = new Mock<IAudiobookRepository>();
            mockRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(allBooks);

            var mockAudioFileRepo = new Mock<IAudiobookFileRepository>();
            mockAudioFileRepo.Setup(r => r.GetAllAsync(default)).ReturnsAsync(allFiles);

            var mockDownloadRepo = new Mock<IDownloadRepository>();
            mockDownloadRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(allDownloads);
            mockDownloadRepo.Setup(r => r.GetActiveAudiobookIdsAsync(It.IsAny<IEnumerable<DownloadStatus>>()))
                .Returns((IEnumerable<DownloadStatus> statuses) =>
                {
                    var s = statuses.ToHashSet();
                    return Task.FromResult(allDownloads
                        .Where(d => d.AudiobookId.HasValue && s.Contains(d.Status))
                        .Select(d => d.AudiobookId!.Value)
                        .Distinct()
                        .ToList());
                });

            using var provider = new ServiceCollection().BuildServiceProvider();
            var controller = new LibraryController(
                mockRepo.Object,
                Mock.Of<IImageCacheService>(),
                NullLogger<LibraryController>.Instance,
                provider.GetRequiredService<IServiceScopeFactory>(),
                Mock.Of<IHistoryRepository>(),
                mockAudioFileRepo.Object,
                Mock.Of<IQualityProfileRepository>(),
                mockDownloadRepo.Object,
                Mock.Of<IRootFolderRepository>(),
                Mock.Of<IFileNamingService>());

            var actionResult = await controller.GetAll();
            var ok = Assert.IsType<OkObjectResult>(actionResult);

            var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var doc = JsonDocument.Parse(json);
            var item = doc.RootElement
                .EnumerateArray()
                .Single(element => element.GetProperty("id").GetInt32() == book.Id);

            Assert.Equal("downloading", item.GetProperty("status").GetString());
            Assert.False(item.GetProperty("wanted").GetBoolean());
            Assert.True(item.TryGetProperty("genres", out var genres));
            Assert.Equal(2, genres.GetArrayLength());
            Assert.Contains(genres.EnumerateArray().Select(g => g.GetString()), value => value == "Fantasy");
            Assert.True(item.TryGetProperty("openLibraryId", out var openLibraryId));
            Assert.Equal("OL123", openLibraryId.GetString());
            Assert.Equal(book.BasePath, item.GetProperty("basePath").GetString());
            Assert.Equal(book.FilePath, item.GetProperty("filePath").GetString());
            Assert.Equal(book.FileSize, item.GetProperty("fileSize").GetInt64());
            Assert.Equal(1, item.GetProperty("fileCount").GetInt32());

            Assert.False(item.TryGetProperty("files", out _));
            Assert.False(item.TryGetProperty("description", out _));
            Assert.False(item.TryGetProperty("subtitle", out _));
        }
    }
}
