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
using Listenarr.Application.Common;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Controllers
{
    public class LibraryController_WantedFlagRegressionTests
    {
        [Fact]
        public async Task GetAll_TreatsDbFileRecordAsNotWanted_EvenIfPathIsMissing()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);

            var book = new Audiobook
            {
                Title = "Controller Book",
                Monitored = true
            };
            db.Audiobooks.Add(book);
            await db.SaveChangesAsync();

            var audioFile = new AudiobookFile
            {
                AudiobookId = book.Id,
                Path = $@"Z:\definitely-missing\{Guid.NewGuid():N}.m4b",
                Size = 1024,
                CreatedAt = DateTime.UtcNow
            };
            db.AudiobookFiles.Add(audioFile);
            await db.SaveChangesAsync();

            var allBooks = db.Audiobooks.ToList();
            var allFiles = db.AudiobookFiles.ToList();
            var allDownloads = new List<Download>();

            var mockRepo = new Mock<IAudiobookRepository>();
            mockRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(allBooks);

            var mockAudioFileRepo = new Mock<IAudiobookFileRepository>();
            mockAudioFileRepo.Setup(r => r.GetAllAsync(default)).ReturnsAsync(allFiles);

            var mockDownloadRepo = new Mock<IDownloadRepository>();
            mockDownloadRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(allDownloads);
            mockDownloadRepo.Setup(r => r.GetActiveAudiobookIdsAsync(It.IsAny<IEnumerable<DownloadStatus>>()))
                .ReturnsAsync(new List<int>());

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
            var wanted = doc.RootElement
                .EnumerateArray()
                .Single(item => item.GetProperty("id").GetInt32() == book.Id)
                .GetProperty("wanted")
                .GetBoolean();

            Assert.False(wanted);
        }

        [Fact]
        public async Task GetAll_TreatsLegacyFilePathAsNotWanted_WhenNoFileRowsExist()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);

            var book = new Audiobook
            {
                Title = "Legacy FilePath Book",
                Monitored = true,
                FilePath = @"C:\legacy\book.m4b",
                FileSize = 2048
            };
            db.Audiobooks.Add(book);
            await db.SaveChangesAsync();

            var allBooks = db.Audiobooks.ToList();

            var mockRepo = new Mock<IAudiobookRepository>();
            mockRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(allBooks);

            var mockAudioFileRepo = new Mock<IAudiobookFileRepository>();
            mockAudioFileRepo.Setup(r => r.GetAllAsync(default)).ReturnsAsync(new List<AudiobookFile>());

            var mockDownloadRepo = new Mock<IDownloadRepository>();
            mockDownloadRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Download>());
            mockDownloadRepo.Setup(r => r.GetActiveAudiobookIdsAsync(It.IsAny<IEnumerable<DownloadStatus>>()))
                .ReturnsAsync(new List<int>());

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

            Assert.False(item.GetProperty("wanted").GetBoolean());
            Assert.Equal("quality-match", item.GetProperty("status").GetString());
        }
    }
}
