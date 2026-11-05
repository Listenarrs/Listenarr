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
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Search
{
    // Every caller that reaches DownloadService.SearchAndDownloadAsync sets isAutomaticSearch,
    // so the origin is what tells the indexer budget whether a person asked (#639). These pin
    // the origin each caller sends and that it reaches the search service unchanged.
    [Trait("Name", "SearchOriginPlumbingTests")]
    [Trait("Category", "IndexerSearchWorkflow")]
    public sealed class SearchOriginPlumbingTests : BaseTests
    {
        [Theory]
        [InlineData(SearchOrigin.Interactive)]
        [InlineData(SearchOrigin.Retry)]
        public async Task SearchAndDownloadAsync_PassesItsOriginToTheSearch(SearchOrigin origin)
        {
            var search = new Mock<ISearchService>();
            search.Setup(s => s.SearchAsync(
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<List<string>?>(),
                    It.IsAny<SearchSortBy>(),
                    It.IsAny<SearchSortDirection>(),
                    It.IsAny<bool>(),
                    It.IsAny<SearchOrigin>()))
                .ReturnsAsync(new List<SearchResult>());
            Init(builder => builder.WithSingleton(search.Object));
            var book = await SeedMonitoredBookAsync();

            await _provider.GetRequiredService<DownloadService>().SearchAndDownloadAsync(book.Id, origin);

            search.Verify(s => s.SearchAsync(
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<List<string>?>(),
                    It.IsAny<SearchSortBy>(),
                    It.IsAny<SearchSortDirection>(),
                    true,
                    origin),
                Times.Once);
        }

        [Theory]
        [InlineData(false, SearchOrigin.Interactive)]
        [InlineData(true, SearchOrigin.Bulk)]
        public async Task SearchAndDownloadEndpoint_IsInteractiveUnlessFlaggedBulk(bool bulk, SearchOrigin expected)
        {
            var downloads = new Mock<IDownloadService>();
            downloads.Setup(d => d.SearchAndDownloadAsync(It.IsAny<int>(), It.IsAny<SearchOrigin>()))
                .ReturnsAsync(new SearchAndDownloadResult { Success = false, Message = "No search results found" });
            var controller = new DownloadController(
                downloads.Object,
                Mock.Of<IDownloadQueueService>(),
                Mock.Of<IDownloadProcessingJobService>(),
                NullLogger<DownloadController>.Instance);

            await controller.SearchAndDownload(new SearchAndDownloadRequest { AudiobookId = 42, Bulk = bulk });

            downloads.Verify(d => d.SearchAndDownloadAsync(42, expected), Times.Once);
        }

        [Fact]
        public async Task FailedDownloadRetry_SearchesWithTheRetryOrigin()
        {
            var downloads = new Mock<IDownloadService>();
            downloads.Setup(d => d.SearchAndDownloadAsync(It.IsAny<int>(), It.IsAny<SearchOrigin>()))
                .ReturnsAsync(new SearchAndDownloadResult { Success = false, Message = "No search results found" });
            Init(builder => builder.WithSingleton(downloads.Object));
            var settings = await _applicationSettingsRepository.GetAsync() ?? new ApplicationSettings();
            settings.FailedDownloadHandlingEnabled = true;
            settings.FailedDownloadAutoSearch = true;
            await _applicationSettingsRepository.SaveAsync(settings);
            var book = await SeedMonitoredBookAsync();
            var client = new DownloadClientConfigurationBuilder().WithType("mock").WithName("Mock").Build();
            var download = new DownloadBuilder().WithAudiobookId(book.Id).WithStatus(DownloadStatus.Failed).Build();

            var processor = _provider.GetRequiredService<DownloadMonitorProcessor>();
            var onDownloadFailed = typeof(DownloadMonitorProcessor).GetMethod("OnDownloadFailed", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(onDownloadFailed);
            await (Task)onDownloadFailed.Invoke(processor, new object[] { download, client, "failed", CancellationToken.None })!;

            downloads.Verify(d => d.SearchAndDownloadAsync(book.Id, SearchOrigin.Retry), Times.Once);
        }

        private async Task<Audiobook> SeedMonitoredBookAsync()
        {
            var profile = await _qualityProfileRepository.AddAsync(new QualityProfileBuilder().WithName("Any").Build());
            var book = new AudiobookBuilder()
                .WithTitle("The Hound of the Baskervilles")
                .WithAuthor("Arthur Conan Doyle")
                .WithMonitored()
                .Build();
            book.QualityProfileId = profile.Id;
            return await _audiobookRepository.AddAsync(book);
        }
    }
}
