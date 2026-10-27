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

using Microsoft.Extensions.Logging.Abstractions;

using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Search.Matching;

[Trait("Name", "AutomaticSearchScoringTests")]
[Trait("Category", "Application")]
public sealed class AutomaticSearchScoringTests : BaseTests
{
    [Fact]
    public async Task EqualQualitySelectionIsStableAcrossProviderOrdering()
    {
        var first = Result("Dune", "Frank Herbert", 1);
        var second = Result("Dune", "Frank Herbert", 2);
        var quality = new Mock<IQualityProfileService>();
        quality.Setup(q => q.ScoreSearchResults(It.IsAny<List<SearchResult>>(), It.IsAny<QualityProfile>()))
            .ReturnsAsync((List<SearchResult> candidates, QualityProfile _) => candidates.Select(r =>
                new QualityScore { SearchResult = r, TotalScore = 100 }).ToList());
        var scorer = New(quality);
        var target = new Audiobook { Title = "Dune", Authors = ["Frank Herbert"] };
        Assert.Same(first, scorer.SelectBest(await scorer.ScoreAsync(target, [first, second], new()))!.SearchResult);
        Assert.Same(first, scorer.SelectBest(await scorer.ScoreAsync(target, [second, first], new()))!.SearchResult);
    }

    private static SearchResult Result(string title, string author, int indexer = 1) => new()
    {
        Id = "duplicate-source-id",
        Title = title,
        Artist = author,
        IndexerId = indexer,
        IdentityEvidence = new() { ReleaseName = title, Authors = [new(author, IdentityEvidenceOrigin.ProviderSupplied)] }
    };

    [Fact]
    public async Task EveryCandidateIsScored_AndQualityCannotOverrideIdentity()
    {
        var book = new Audiobook { Id = 1, Title = "Dune", Authors = ["Frank Herbert"] };
        var correct = Result("Dune", "Frank Herbert");
        var decoy = Result("The Shining", "Stephen King", 2);
        var results = new List<SearchResult> { correct, decoy };
        var quality = new Mock<IQualityProfileService>(MockBehavior.Strict);
        quality.Setup(q => q.ScoreSearchResults(results, It.IsAny<QualityProfile>())).ReturnsAsync(
            [new QualityScore { SearchResult = decoy, TotalScore = 10000 },
             new QualityScore { SearchResult = correct, TotalScore = 1 }]);
        var scorer = New(quality);
        var batch = await scorer.ScoreAsync(book, results, new());
        Assert.Equal(2, batch.Candidates.Count);
        Assert.Same(correct, scorer.SelectBest(batch)!.SearchResult);
        Assert.Contains(batch.Candidates, s => ReferenceEquals(s.SearchResult, decoy) && s.IsRejected);
        quality.VerifyAll();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task PositiveQualityIsRequiredEvenWithConfirmedIdentity(int score)
    {
        var candidate = Result("Dune", "Frank Herbert");
        var quality = new Mock<IQualityProfileService>();
        quality.Setup(q => q.ScoreSearchResults(It.IsAny<List<SearchResult>>(), It.IsAny<QualityProfile>()))
            .ReturnsAsync([new QualityScore { SearchResult = candidate, TotalScore = score }]);
        var scorer = New(quality);
        var batch = await scorer.ScoreAsync(new() { Title = "Dune", Authors = ["Frank Herbert"] }, [candidate], new());
        Assert.Equal(score > 0, scorer.SelectBest(batch) != null);
    }

    [Fact]
    public async Task CancellationAfterQualityNeverProducesASelection()
    {
        using var cts = new CancellationTokenSource();
        var quality = new Mock<IQualityProfileService>();
        quality.Setup(q => q.ScoreSearchResults(It.IsAny<List<SearchResult>>(), It.IsAny<QualityProfile>()))
            .Callback(() => cts.Cancel()).ReturnsAsync([]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => New(quality).ScoreAsync(
            new() { Title = "Dune" }, [], new(), cts.Token));
    }

    [Fact]
    public async Task TargetEditsInvalidateTheAssessment_UsingFreshRepositorySnapshot()
    {
        var book = new Audiobook { Id = 9, Title = "Dune", Authors = ["Frank Herbert"], QualityProfile = new() };
        var candidate = Result("Dune", "Frank Herbert");
        var quality = new Mock<IQualityProfileService>();
        quality.Setup(q => q.ScoreSearchResults(It.IsAny<List<SearchResult>>(), It.IsAny<QualityProfile>()))
            .ReturnsAsync([new QualityScore { SearchResult = candidate, TotalScore = 100 }]);
        var books = new Mock<IAudiobookRepository>();
        books.Setup(b => b.GetByIdSnapshotAsync(9, It.IsAny<CancellationToken>())).ReturnsAsync(book);
        var scorer = new AutomaticSearchScoringService(quality.Object, books.Object,
            NullLogger<AutomaticSearchScoringService>.Instance);
        var batch = await scorer.ScoreAsync(book, [candidate], book.QualityProfile);
        Assert.True(await scorer.IsCurrentAsync(batch, 9));
        book.Title = "Another Work";
        Assert.False(await scorer.IsCurrentAsync(batch, 9));
    }

    [Fact]
    public async Task QualityMutationCannotSwapTheApprovedSource()
    {
        var candidate = Result("Dune", "Frank Herbert");
        candidate.TorrentUrl = "http://fixture.invalid/original.torrent";
        var quality = new Mock<IQualityProfileService>();
        quality.Setup(q => q.ScoreSearchResults(It.IsAny<List<SearchResult>>(), It.IsAny<QualityProfile>()))
            .Callback(() => candidate.TorrentUrl = "http://fixture.invalid/other.torrent")
            .ReturnsAsync([new QualityScore { SearchResult = candidate, TotalScore = 9999 }]);
        var scorer = New(quality);
        var batch = await scorer.ScoreAsync(new() { Title = "Dune", Authors = ["Frank Herbert"] }, [candidate], new());
        Assert.True(Assert.Single(batch.Candidates).Identity.IsConfirmed);
        Assert.Null(scorer.SelectBest(batch));
        Assert.Contains("candidate_changed", batch.Candidates[0].RejectionReasons);
    }

    [Fact]
    public async Task MissingOrCopiedQualityAssociationsRemainVisibleButCannotBeSelected()
    {
        var candidate = Result("Dune", "Frank Herbert");
        var copy = Result("Dune", "Frank Herbert");
        var quality = new Mock<IQualityProfileService>();
        quality.Setup(q => q.ScoreSearchResults(It.IsAny<List<SearchResult>>(), It.IsAny<QualityProfile>()))
            .ReturnsAsync([new QualityScore { SearchResult = copy, TotalScore = 100 }]);
        var scorer = New(quality);
        var batch = await scorer.ScoreAsync(new() { Title = "Dune", Authors = ["Frank Herbert"] }, [candidate], new());
        Assert.Same(candidate, Assert.Single(batch.Candidates).SearchResult);
        Assert.Equal(BookIdentityStatus.AssessmentUnavailable, batch.Candidates[0].Identity.Status);
        Assert.Null(scorer.SelectBest(batch));
    }

    [Fact]
    public async Task QualityRejectionRemainsAQualityRejection()
    {
        var candidate = Result("Dune", "Frank Herbert");
        var quality = new Mock<IQualityProfileService>();
        quality.Setup(q => q.ScoreSearchResults(It.IsAny<List<SearchResult>>(), It.IsAny<QualityProfile>()))
            .ReturnsAsync([new QualityScore { SearchResult = candidate, TotalScore = 100, RejectionReasons = ["forbidden"] }]);
        var scorer = New(quality);
        var batch = await scorer.ScoreAsync(new() { Title = "Dune", Authors = ["Frank Herbert"] }, [candidate], new());
        Assert.True(batch.Candidates[0].Identity.IsConfirmed);
        Assert.Null(scorer.SelectBest(batch));
        Assert.Contains("forbidden", batch.Candidates[0].RejectionReasons);
        Assert.DoesNotContain("complete_title_and_credit", batch.Candidates[0].RejectionReasons);
    }

    private static AutomaticSearchScoringService New(Mock<IQualityProfileService> quality) =>
        new(quality.Object, Mock.Of<IAudiobookRepository>(), NullLogger<AutomaticSearchScoringService>.Instance);
}
