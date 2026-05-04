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
using System.Reflection;
using Listenarr.Api.Controllers;
using Listenarr.Application.Common;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Domain.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Controllers
{
    public class LibraryController_QualityCutoffTests
    {
        [Fact]
        [Trait("Scenario", "ImportPendingDownloadCountsAsActiveForQualityCutoff")]
        public async Task IsQualityCutoffMetAsync_ImportPendingDownload_ReturnsTrue()
        {
            var testDownload = new Download
            {
                Id = "dl-1",
                AudiobookId = 1,
                Status = DownloadStatus.ImportPending,
                Title = "Dune"
            };

            var downloadRepo = new Mock<IDownloadRepository>();
            downloadRepo.Setup(r => r.GetByAudiobookIdAsync(1, default)).ReturnsAsync(new List<Download> { testDownload });

            var audioFileRepo = new Mock<IAudiobookFileRepository>();
            audioFileRepo.Setup(r => r.GetByAudiobookIdAsync(1, default)).ReturnsAsync(new List<AudiobookFile>());

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
                Mock.Of<IServiceScopeFactory>(),
                Mock.Of<IHistoryRepository>(),
                audioFileRepo.Object,
                Mock.Of<IQualityProfileRepository>(),
                downloadRepo.Object,
                Mock.Of<IRootFolderRepository>(),
                Mock.Of<IFileNamingService>());

            var method = typeof(LibraryController).GetMethod(
                "IsQualityCutoffMetAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var task = (Task<bool>?)method!.Invoke(controller, new object[] { audiobook, Mock.Of<IQualityProfileService>(), downloadRepo.Object, audioFileRepo.Object });
            Assert.NotNull(task);

            var result = await task!;

            Assert.True(result);
        }
    }
}
