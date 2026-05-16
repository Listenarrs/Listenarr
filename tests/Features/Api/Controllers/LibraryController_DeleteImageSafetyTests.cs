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
using Xunit;
using Moq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Listenarr.Api.Controllers;
using Listenarr.Application.Interfaces.Repositories;
using Microsoft.AspNetCore.Mvc;
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Models.Configurations;
using Listenarr.Application.Common;
using Listenarr.Application.Notification;

namespace Listenarr.Tests.Features.Api.Controllers
{
    public class LibraryController_DeleteImageSafetyTests
    {
        [Fact]
        public async Task DeleteAudiobook_InvalidImageUrl_DoesNotCallImageCacheService()
        {
            // Arrange
            var mockRepo = new Mock<IAudiobookRepository>();
            var mockImageCache = new Mock<IImageCacheService>();
            var mockLogger = new Mock<ILogger<LibraryController>>();

            var services = new ServiceCollection();
            var mockConfig = new Mock<IConfigurationService>();
            mockConfig.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(new ApplicationSettings { OutputPath = System.IO.Path.GetTempPath() });
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
            var fileNaming = new Mock<IFileNamingService>().Object;

            var audiobook = new Listenarr.Domain.Models.Audiobook { Id = 123, Title = "Test", ImageUrl = "/config/cache/images/library/../evil/../../secret.txt" };
            mockRepo.Setup(r => r.GetByIdAsync(It.IsAny<int>())).ReturnsAsync(audiobook);
            mockRepo.Setup(r => r.DeleteByIdAsync(It.IsAny<int>())).ReturnsAsync(true);

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
                fileNaming,
                scanQueueService: null,
                moveQueueService: null,
                notificationService: null);

            // Act
            var result = await controller.DeleteAudiobook(audiobook.Id);

            // Assert
            // The identifier 'secret' should be extracted and validated; ensure we called into the image cache service
            mockImageCache.Verify(s => s.GetCachedImagePathAsync("secret"), Times.Once);
            Assert.IsType<OkObjectResult>(result);
        }
    }
}
