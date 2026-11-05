using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Audiobooks.Catalog
{
    [Trait("Name", "LibraryListServiceTests")]
    [Trait("Category", "Application")]
    public sealed class LibraryListServiceTests : BaseTests
    {
        [Fact]
        public async Task GetAllAsync_GivesEachBookItsBrokenReasons_FromItsMainAsin()
        {
            string[] authors = ["Robin Hobb"];
            var service = CreateService(
                [
                    new Audiobook { Id = 1, Title = "Fine", Authors = [.. authors], Asin = "B075ZTX494" },
                    new Audiobook { Id = 2, Title = "Region missing", Authors = [.. authors], Asin = "B0HM45F8MW" },
                    // Its ASIN field is malformed, but its main ASIN identifier is fine
                    new Audiobook { Id = 3, Title = "Old field", Authors = [.. authors], Asin = "typed by hand" },
                    // No identifier: only the ASIN field, so no region
                    new Audiobook { Id = 4, Title = "No identifier", Authors = [.. authors], Asin = "B0BK9W82PQ" },
                    new Audiobook { Id = 5, Title = "No author", Authors = [], Asin = "B0OTHER002" },
                ],
                new Dictionary<int, List<AudiobookExternalIdentifier>>
                {
                    // The repository returns the primary ASIN first
                    [1] = [Asin(1, "B075ZTX494", "fr", isPrimary: true), Asin(1, "B0OTHER001", null, isPrimary: false)],
                    [2] = [Asin(2, "B0HM45F8MW", null, isPrimary: true)],
                    [3] = [Asin(3, "B0BK9W82PQ", "us", isPrimary: true)],
                    [5] = [Asin(5, "B0OTHER002", "us", isPrimary: true)],
                });

            var items = await service.GetAllAsync();

            var reasonsById = items.ToDictionary(i => i.Id, i => i.BrokenReasons);
            Assert.Empty(reasonsById[1]);
            Assert.Equal([BrokenBookReasons.MissingRegion], reasonsById[2]);
            Assert.Empty(reasonsById[3]);
            Assert.Equal([BrokenBookReasons.MissingRegion], reasonsById[4]);
            Assert.Equal([BrokenBookReasons.MissingAuthor], reasonsById[5]);
        }

        private static AudiobookExternalIdentifier Asin(
            int audiobookId,
            string value,
            string? region,
            bool isPrimary) =>
            new()
            {
                AudiobookId = audiobookId,
                Type = AudiobookExternalIdentifierType.Asin,
                ValueRaw = value,
                ValueNormalized = value,
                Region = region,
                IsPrimary = isPrimary,
            };

        private static LibraryListService CreateService(
            List<Audiobook> audiobooks,
            Dictionary<int, List<AudiobookExternalIdentifier>> asinsByAudiobookId)
        {
            var audiobookRepository = new Mock<IAudiobookRepository>();
            audiobookRepository.Setup(r => r.GetAllAsync()).ReturnsAsync(audiobooks);
            audiobookRepository
                .Setup(r => r.GetAllSeriesMembershipsGroupedByAudiobookIdAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<int, List<AudiobookSeriesMembership>>());
            audiobookRepository
                .Setup(r => r.GetAsinIdentifiersGroupedByAudiobookIdAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(asinsByAudiobookId);
            var fileRepository = new Mock<IAudiobookFileRepository>();
            fileRepository
                .Setup(r => r.GetFormatSummariesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<AudiobookFormatSummary>());
            fileRepository
                .Setup(r => r.GetCountsByAudiobookIdAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<int, int>());
            var qualityProfileRepository = new Mock<IQualityProfileRepository>();
            qualityProfileRepository.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<QualityProfile>());
            var downloadRepository = new Mock<IDownloadRepository>();
            downloadRepository
                .Setup(r => r.GetActiveAudiobookIdsAsync(It.IsAny<IEnumerable<DownloadStatus>>()))
                .ReturnsAsync(new List<int>());

            return new LibraryListService(
                audiobookRepository.Object,
                fileRepository.Object,
                qualityProfileRepository.Object,
                downloadRepository.Object);
        }
    }
}
