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
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Models.Configurations;
using Listenarr.Infrastructure.Persistence;
using Listenarr.Application.Common;
using Listenarr.Application.Notification;

namespace Listenarr.Tests.Features.Api.Controllers
{
    public class LibraryController_MoveTests
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

        [Fact]
        public async Task MoveAudiobook_ReturnsBadRequest_WhenSourceDoesNotExist()
        {
            // Arrange
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var dbContext = new ListenArrDbContext(options);
            var mockRepo = new Mock<IAudiobookRepository>();
            // Return the audiobook from the in-memory DB when asked
            mockRepo.Setup(m => m.GetByIdAsync(It.IsAny<int>())).ReturnsAsync((int id) => dbContext.Audiobooks.Find(id));
            var mockImageCache = new Mock<IImageCacheService>();
            var mockLogger = new Mock<ILogger<LibraryController>>();
            var mockFileNaming = new Mock<IFileNamingService>();

            var services = new ServiceCollection();
            var mockConfig = new Mock<IConfigurationService>();
            mockConfig.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(new ApplicationSettings { OutputPath = Path.GetTempPath() });
            services.AddSingleton<IConfigurationService>(mockConfig.Object);
            // Provide a mock signalR hub context (with Clients.All mocked) to avoid exceptions during broadcast
            var mockHub = new Mock<Microsoft.AspNetCore.SignalR.IHubContext<DownloadHub>>();
            var mockClients = new Mock<Microsoft.AspNetCore.SignalR.IHubClients>();
            var mockClientProxy = new Mock<Microsoft.AspNetCore.SignalR.IClientProxy>();
            mockClientProxy.Setup(m => m.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), default)).Returns(System.Threading.Tasks.Task.CompletedTask);
            mockClients.SetupGet(c => c.All).Returns(mockClientProxy.Object);
            mockHub.SetupGet(h => h.Clients).Returns(mockClients.Object);
            services.AddSingleton(typeof(Microsoft.AspNetCore.SignalR.IHubContext<DownloadHub>), mockHub.Object);
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            // Ensure move queue exists for controller (prevent early NotFound responses in tests)
            var mockMoveQueue = new Mock<IMoveQueueService>();

            // Add an audiobook with a non-existent base path
            var ab = new Audiobook { Title = "Test", BasePath = Path.Join(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid().ToString("N")) };
            dbContext.Audiobooks.Add(ab);
            await dbContext.SaveChangesAsync();
            // Ensure repo returns the audiobook from the in-memory DB when asked
            mockRepo.Setup(m => m.GetByIdAsync(It.IsAny<int>())).ReturnsAsync((int id) => dbContext.Audiobooks.Find(id));

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
                mockFileNaming.Object,
                null,
                mockMoveQueue.Object,
                null);

            var request = new LibraryController.MoveRequest { DestinationPath = Path.Join(Path.GetTempPath(), "target") };

            // Act
            var result = await controller.EnqueueMove(ab.Id, request);

            // Assert: expect 400 Bad Request with 'Source path' message
            var badObj = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(400, badObj.StatusCode);
            Assert.Contains("Source path", badObj.Value?.ToString() ?? string.Empty);
        }

        [Fact]
        public async Task MoveAudiobook_EnqueuesJob_WhenSourceExists()
        {
            // Arrange
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var dbContext = new ListenArrDbContext(options);
            var mockRepo = new Mock<IAudiobookRepository>();
            var mockImageCache = new Mock<IImageCacheService>();
            var mockLogger = new Mock<ILogger<LibraryController>>();
            var mockFileNaming = new Mock<IFileNamingService>();

            var mockMoveQueue = new Mock<IMoveQueueService>();
            var expectedId = Guid.NewGuid();
            mockMoveQueue.Setup(m => m.EnqueueMoveAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(expectedId);

            var services = new ServiceCollection();
            var mockConfig = new Mock<IConfigurationService>();
            mockConfig.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(new ApplicationSettings { OutputPath = Path.GetTempPath() });
            services.AddSingleton<IConfigurationService>(mockConfig.Object);
            // Provide a mock hub context with Clients.All mocked
            var mockHub = new Mock<Microsoft.AspNetCore.SignalR.IHubContext<DownloadHub>>();
            var mockClients = new Mock<Microsoft.AspNetCore.SignalR.IHubClients>();
            var mockClientProxy = new Mock<Microsoft.AspNetCore.SignalR.IClientProxy>();
            mockClientProxy.Setup(m => m.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), default)).Returns(System.Threading.Tasks.Task.CompletedTask);
            mockClients.SetupGet(c => c.All).Returns(mockClientProxy.Object);
            mockHub.SetupGet(h => h.Clients).Returns(mockClients.Object);
            services.AddSingleton(typeof(Microsoft.AspNetCore.SignalR.IHubContext<DownloadHub>), mockHub.Object);
            services.AddSingleton<IMoveQueueService>(mockMoveQueue.Object);
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            // Create a real temporary source directory
            var tempSource = Path.Join(Path.GetTempPath(), "listenarr-move-src-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempSource);

            var ab = new Audiobook { Title = "Test", BasePath = tempSource };
            dbContext.Audiobooks.Add(ab);
            await dbContext.SaveChangesAsync();
            // Ensure repo returns the audiobook from the in-memory DB when asked
            mockRepo.Setup(m => m.GetByIdAsync(It.IsAny<int>())).ReturnsAsync((int id) => dbContext.Audiobooks.Find(id));

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
                mockFileNaming.Object,
                null,
                mockMoveQueue.Object,
                null);

            var target = Path.Join(Path.GetTempPath(), "listenarr-move-dst-" + Guid.NewGuid().ToString("N"));
            var request = new LibraryController.MoveRequest { DestinationPath = target };

            // Act
            var result = await controller.EnqueueMove(ab.Id, request);

            // Assert: expect 202 Accepted
            var acceptedObj = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(202, acceptedObj.StatusCode);
            Assert.NotNull(acceptedObj.Value);

            // Cleanup
            TryDeleteDirectory(tempSource);
            TryDeleteDirectory(target);
        }

        [Fact]
        public async Task MoveAudiobook_UpdatesBasePath_WhenMoveFilesFalse()
        {
            // Arrange
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var dbContext = new ListenArrDbContext(options);
            var mockRepo = new Mock<IAudiobookRepository>();
            var mockImageCache = new Mock<IImageCacheService>();
            var mockLogger = new Mock<ILogger<LibraryController>>();
            var mockFileNaming = new Mock<IFileNamingService>();

            var mockMoveQueue = new Mock<IMoveQueueService>();

            var services = new ServiceCollection();
            var mockConfig = new Mock<IConfigurationService>();
            mockConfig.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(new ApplicationSettings { OutputPath = Path.GetTempPath() });
            services.AddSingleton<IConfigurationService>(mockConfig.Object);
            // Provide a mock hub context with Clients.All mocked
            var mockHub = new Mock<Microsoft.AspNetCore.SignalR.IHubContext<DownloadHub>>();
            var mockClients = new Mock<Microsoft.AspNetCore.SignalR.IHubClients>();
            var mockClientProxy = new Mock<Microsoft.AspNetCore.SignalR.IClientProxy>();
            mockClientProxy.Setup(m => m.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), default)).Returns(System.Threading.Tasks.Task.CompletedTask);
            mockClients.SetupGet(c => c.All).Returns(mockClientProxy.Object);
            mockHub.SetupGet(h => h.Clients).Returns(mockClients.Object);
            services.AddSingleton(typeof(Microsoft.AspNetCore.SignalR.IHubContext<DownloadHub>), mockHub.Object);
            services.AddSingleton<IMoveQueueService>(mockMoveQueue.Object);
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var ab = new Audiobook { Title = "Test", BasePath = Path.Join(Path.GetTempPath(), "listenarr-move-src-" + Guid.NewGuid().ToString("N")) };
            dbContext.Audiobooks.Add(ab);
            await dbContext.SaveChangesAsync();
            // Ensure repo returns the audiobook from the in-memory DB when asked
            mockRepo.Setup(m => m.GetByIdAsync(It.IsAny<int>())).ReturnsAsync((int id) => dbContext.Audiobooks.Find(id));

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
                mockFileNaming.Object,
                null,
                mockMoveQueue.Object,
                null);

            var target = Path.Join(Path.GetTempPath(), "listenarr-move-dst-" + Guid.NewGuid().ToString("N"));
            var request = new LibraryController.MoveRequest { DestinationPath = target, MoveFiles = false };

            // Act
            var result = await controller.EnqueueMove(ab.Id, request);

            // Assert: expect 200 OK
            var okObj = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(200, okObj.StatusCode);
            Assert.NotNull(okObj.Value);

            // Ensure DB was updated
            var updated = await dbContext.Audiobooks.FindAsync(ab.Id);
            Assert.Equal(target, updated.BasePath);

            // Ensure move queue was NOT enqueued
            mockMoveQueue.Verify(m => m.EnqueueMoveAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }
    }
}
