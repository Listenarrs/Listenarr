using Listenarr.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

// The automatic search assesses each book as GetMonitoredAudiobooksForSearchAsync loaded it, and
// before it starts a download IsCurrentAsync rebuilds the target fingerprint from a full read of
// the book. The two have to agree for a book nobody edited, or the sweep never grabs it.
[Trait("Name", "AudiobookRepositoryAutomaticSearchSelectionTests")]
[Trait("Category", "Infrastructure")]
public sealed class AudiobookRepositoryAutomaticSearchSelectionTests : BaseTests
{
    // One series is also the book's own Series field; a second one is only in the memberships.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SelectedBookInSeries_IsStillCurrentForTheAutomaticSearch(int seriesCount)
    {
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        int audiobookId;
        await using (var seed = new ListenArrDbContext(options))
        {
            var book = new Audiobook
            {
                Title = "She And Allan",
                Authors = ["H. Rider Haggard"],
                Series = "Ayesha",
                SeriesNumber = "0",
                Monitored = true,
                QualityProfile = new QualityProfile { Name = "Any" },
                SeriesMemberships = new[] { ("Ayesha", "0"), ("Allan Quatermain", (string?)null) }
                    .Take(seriesCount)
                    .Select((series, index) => new AudiobookSeriesMembership
                    {
                        SeriesName = series.Item1,
                        SeriesNumber = series.Item2,
                        IsPrimary = index == 0,
                        SortOrder = index
                    })
                    .ToList()
            };
            seed.Audiobooks.Add(book);
            await seed.SaveChangesAsync();
            audiobookId = book.Id;
        }

        await using var db = new ListenArrDbContext(options);
        var repository = new AudiobookRepository(db);
        var quality = new Mock<IQualityProfileService>();
        quality
            .Setup(q => q.ScoreSearchResults(It.IsAny<List<SearchResult>>(), It.IsAny<QualityProfile>()))
            .ReturnsAsync(new List<QualityScore>());
        var scoring = new AutomaticSearchScoringService(quality.Object, repository,
            NullLogger<AutomaticSearchScoringService>.Instance, TestAnnotationCatalog.CreateProvider());

        var selected = Assert.Single(await repository.GetMonitoredAudiobooksForSearchAsync(DateTime.UtcNow));
        var assessment = await scoring.ScoreAsync(selected, [], selected.QualityProfile!);

        Assert.Equal(audiobookId, selected.Id);
        Assert.True(await scoring.IsCurrentAsync(assessment, audiobookId));
    }
}
