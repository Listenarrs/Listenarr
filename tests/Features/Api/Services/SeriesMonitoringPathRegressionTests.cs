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
using Listenarr.Application.Interfaces;
using Listenarr.Application.Metadata;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
{
    public class SeriesMonitoringPathRegressionTests : BaseTests
    {
        private readonly Mock<ISeriesCatalogService> _seriesCatalogService = new();
        private string _rootPath = null!;

        public override async Task InitializeAsync()
        {
            var audibleService = new Mock<AudibleService>(
                new HttpClient(),
                Mock.Of<ILogger<AudibleService>>());
            audibleService
                .Setup(service => service.LookupAuthorAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync((AuthorLookupItem?)null);

            Init(services => services
                .WithSingleton(_seriesCatalogService.Object)
                .WithSingleton(audibleService.Object)
                .WithSingleton(Mock.Of<IImageCacheService>()));

            await base.InitializeAsync();

            _rootPath = FileService.GetTempDirectory("series-monitoring-library");

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithFolderNamingPattern("{Author}/{Series}/{Title}")
                .Build());

            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithIsDefault()
                .WithPath(_rootPath)
                .Build());
        }

        [Fact]
        public async Task MonitorSeriesAsync_PersistsTitleFolderInBasePath()
        {
            _seriesCatalogService
                .Setup(service => service.GetCatalogAsync(
                    "Dungeon Crawler Carl",
                    "us",
                    500,
                    null,
                    true,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SeriesCatalogFetchResult
                {
                    Series = new SeriesLookupItem
                    {
                        Asin = "SERIES123",
                        Name = "Dungeon Crawler Carl"
                    },
                    Books = new List<AudibleSearchResult>
                    {
                        new()
                        {
                            Asin = "BOOK123",
                            Title = "This Inevitable Ruin",
                            Authors = new List<AudibleAuthor> { new() { Name = "Matt Dinniman" } },
                            Language = "english",
                            Series = new List<AudibleSeries>
                            {
                                new()
                                {
                                    Asin = "SERIES123",
                                    Name = "Dungeon Crawler Carl",
                                    Position = "7"
                                }
                            }
                        }
                    }
                });

            var service = _provider.GetRequiredService<ISeriesMonitoringService>();

            var result = await service.MonitorSeriesAsync(new MonitorSeriesRequest
            {
                Name = "Dungeon Crawler Carl",
                Region = "us",
                Language = "english"
            });

            Assert.True(result.SyncResult.Succeeded);
            Assert.Equal(1, result.SyncResult.AddedCount);

            var storedAudiobook = Assert.Single(await _audiobookRepository.GetAllAsync());
            Assert.Equal(
                Path.Join(_rootPath, "Matt Dinniman", "Dungeon Crawler Carl", "This Inevitable Ruin"),
                storedAudiobook.BasePath);
        }
    }
}
