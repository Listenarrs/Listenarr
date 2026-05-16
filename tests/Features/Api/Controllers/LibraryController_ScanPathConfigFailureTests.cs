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
using Listenarr.Infrastructure.Persistence;
using Listenarr.Application.Common;
using Listenarr.Application.Notification;

namespace Listenarr.Tests.Features.Api.Controllers
{
    public class LibraryController_ScanPathConfigFailureTests
    {
        [Fact]
        public async Task ScanAudiobook_ConfigUnavailable_NoBasePath_Returns500()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var dbContext = new ListenArrDbContext(options);
            var mockRepo = new Mock<IAudiobookRepository>();
            var mockImageCache = new Mock<IImageCacheService>();
            var mockLogger = new Mock<ILogger<LibraryController>>();
            var mockFileNaming = new Mock<IFileNamingService>();

            var services = new ServiceCollection();
            // Configuration service that throws to simulate inability to load settings
            var mockConfig = new Mock<IConfigurationService>();
            mockConfig.Setup(c => c.GetApplicationSettingsAsync()).ThrowsAsync(new Exception("config failure"));
            services.AddSingleton<IConfigurationService>(mockConfig.Object);

            var mockHub = new Mock<Microsoft.AspNetCore.SignalR.IHubContext<DownloadHub>>();
            var mockClients = new Mock<Microsoft.AspNetCore.SignalR.IHubClients>();
            var mockClientProxy = new Mock<Microsoft.AspNetCore.SignalR.IClientProxy>();
            mockClientProxy.Setup(m => m.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), default)).Returns(Task.CompletedTask);
            mockClients.SetupGet(c => c.All).Returns(mockClientProxy.Object);
            mockHub.SetupGet(h => h.Clients).Returns(mockClients.Object);
            services.AddSingleton(typeof(Microsoft.AspNetCore.SignalR.IHubContext<DownloadHub>), mockHub.Object);

            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var ab = new Audiobook { Title = "Test", BasePath = null };
            dbContext.Audiobooks.Add(ab);
            await dbContext.SaveChangesAsync();

            mockRepo.Setup(m => m.GetByIdAsync(It.IsAny<int>())).ReturnsAsync((int id) => dbContext.Audiobooks.Find(id));

            var mockRootFolderSvc = new Mock<IRootFolderService>();
            mockRootFolderSvc.Setup(r => r.GetAllAsync()).ReturnsAsync(new System.Collections.Generic.List<RootFolder>());

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
                null,
                null,
                mockRootFolderSvc.Object);

            var request = new LibraryController.ScanRequest { Path = Path.Join(Path.GetTempPath(), "somepath") };

            var result = await controller.ScanAudiobookFiles(ab.Id, request);

            var obj = Assert.IsType<ObjectResult>(result);
            Assert.Equal(500, obj.StatusCode);
            Assert.Contains("Failed to determine a safe scan path", obj.Value?.ToString() ?? string.Empty);
        }
    }
}
