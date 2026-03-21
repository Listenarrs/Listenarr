using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Listenarr.Api.Controllers;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Listenarr.Api.Tests
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
                BasePath = @"C:\library\Slim Book",
                FilePath = @"C:\library\Slim Book\book.m4b",
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
                DownloadPath = @"C:\downloads",
                FinalPath = book.FilePath ?? string.Empty,
                StartedAt = DateTime.UtcNow,
                Status = DownloadStatus.Downloading
            });
            await db.SaveChangesAsync();

            using var provider = new ServiceCollection().BuildServiceProvider();
            var controller = new LibraryController(
                Mock.Of<IAudiobookRepository>(),
                Mock.Of<IImageCacheService>(),
                NullLogger<LibraryController>.Instance,
                db,
                provider.GetRequiredService<IServiceScopeFactory>(),
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
            Assert.Equal(book.FilePath, item.GetProperty("filePath").GetString());
            Assert.Equal(book.FileSize, item.GetProperty("fileSize").GetInt64());
            Assert.Equal(1, item.GetProperty("fileCount").GetInt32());

            Assert.False(item.TryGetProperty("files", out _));
            Assert.False(item.TryGetProperty("description", out _));
            Assert.False(item.TryGetProperty("subtitle", out _));
            // basePath is intentionally included in the slim payload so the EditAudiobookModal
            // can show the correct destination folder when opened from the audiobooks list page.
            Assert.True(item.TryGetProperty("basePath", out _));
        }
    }
}
