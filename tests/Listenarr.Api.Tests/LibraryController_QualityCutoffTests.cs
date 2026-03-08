using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Listenarr.Api.Controllers;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Listenarr.Api.Tests
{
    public class LibraryController_QualityCutoffTests
    {
        [Fact]
        [Trait("Scenario", "ImportPendingDownloadCountsAsActiveForQualityCutoff")]
        public async Task IsQualityCutoffMetAsync_ImportPendingDownload_ReturnsTrue()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var dbContext = new ListenArrDbContext(options);
            dbContext.Downloads.Add(new Download
            {
                Id = "dl-1",
                AudiobookId = 1,
                Status = DownloadStatus.ImportPending,
                Title = "Dune"
            });
            await dbContext.SaveChangesAsync();

            var audiobook = new Audiobook
            {
                Id = 1,
                Title = "Dune",
                QualityProfile = new QualityProfile
                {
                    CutoffQuality = "MP3",
                    Qualities = new List<QualityDefinition>
                    {
                        new() { Quality = "MP3", Priority = 1 }
                    }
                }
            };

            var controller = new LibraryController(
                Mock.Of<IAudiobookRepository>(),
                Mock.Of<IImageCacheService>(),
                Mock.Of<ILogger<LibraryController>>(),
                dbContext,
                Mock.Of<IServiceScopeFactory>(),
                Mock.Of<IFileNamingService>());

            var method = typeof(LibraryController).GetMethod(
                "IsQualityCutoffMetAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var task = (Task<bool>?)method!.Invoke(controller, new object[] { audiobook, Mock.Of<IQualityProfileService>(), dbContext });
            Assert.NotNull(task);

            var result = await task!;

            Assert.True(result);
        }
    }
}
