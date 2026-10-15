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
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Audiobooks.Catalog
{
    [Trait("Area", "Library")]
    [Trait("Name", "LibraryIssueServiceTests")]
    [Trait("Category", "LibraryIssueService")]
    public class LibraryIssueServiceTests : BaseTests
    {
        private static AudiobookExternalIdentifier Asin(string value, string? region) => new()
        {
            Type = AudiobookExternalIdentifierType.Asin,
            ValueRaw = value,
            ValueNormalized = value,
            Region = region,
            IsPrimary = true,
            Source = AudiobookExternalIdentifierSource.Imported
        };

        private async Task<Audiobook> AddBook(string? title, string? asin, AudiobookExternalIdentifier? identifier)
        {
            var builder = new AudiobookBuilder().WithAuthor("Zogarth");
            if (title != null) builder.WithTitle(title);
            if (asin != null) builder.WithAsin(asin);
            var book = builder.Build();
            if (title == null) book.Title = null;
            book.ExternalIdentifiers = identifier == null
                ? new List<AudiobookExternalIdentifier>()
                : new List<AudiobookExternalIdentifier> { identifier };
            return await _audiobookRepository.AddAsync(book);
        }

        [Fact]
        [Trait("Method", "GetIssuesAsync")]
        [Trait("Scenario", "ListsBooksWithAnIncompleteAudibleIdentity")]
        public async Task GetIssuesAsync_ListsBooksWithAnIncompleteAudibleIdentity()
        {
            // Given
            var healthy = await AddBook("Primal Hunter", "B0FXXVV42J", Asin("B0FXXVV42J", "fr"));
            var noRegion = await AddBook("Primal Hunter 4", "B0BK9W82PQ", Asin("B0BK9W82PQ", null));
            var noAsin = await AddBook("Ready Player One", null, null);
            var badAsin = await AddBook("dfh", "erg", null);
            var noTitle = await AddBook(null, "B0HM45F8MW", Asin("B0HM45F8MW", "fr"));
            var service = _provider.GetRequiredService<ILibraryIssueService>();

            // When
            var issues = await service.GetIssuesAsync(includeIgnored: false);

            // Then
            Assert.DoesNotContain(issues, i => i.Id == healthy.Id);
            Assert.Equal(new[] { LibraryIssueCodes.MissingRegion }, issues.Single(i => i.Id == noRegion.Id).Issues);
            Assert.Equal("B0BK9W82PQ", issues.Single(i => i.Id == noRegion.Id).Asin);
            Assert.Equal(new[] { LibraryIssueCodes.MissingAsin }, issues.Single(i => i.Id == noAsin.Id).Issues);
            Assert.Equal(new[] { LibraryIssueCodes.InvalidAsin }, issues.Single(i => i.Id == badAsin.Id).Issues);
            var untitled = issues.Single(i => i.Id == noTitle.Id);
            Assert.Equal(new[] { LibraryIssueCodes.MissingTitle }, untitled.Issues);
            Assert.Equal("fr", untitled.Region);
        }

        [Fact]
        [Trait("Method", "SetIgnoredAsync")]
        [Trait("Scenario", "IgnoredBooksLeaveTheListUntilShownOrBroughtBack")]
        public async Task SetIgnoredAsync_IgnoredBooksLeaveTheListUntilShownOrBroughtBack()
        {
            // Given a book with an issue and a healthy book, both ignored
            var noAsin = await AddBook("Ready Player One", null, null);
            var healthy = await AddBook("Primal Hunter", "B0FXXVV42J", Asin("B0FXXVV42J", "fr"));
            var service = _provider.GetRequiredService<ILibraryIssueService>();
            Assert.True(await service.SetIgnoredAsync(noAsin.Id, true));
            Assert.True(await service.SetIgnoredAsync(healthy.Id, true));

            // Then they are out of the default list
            Assert.DoesNotContain(await service.GetIssuesAsync(), i => i.Id == noAsin.Id || i.Id == healthy.Id);

            // And listed with includeIgnored, even the one without issues, so they can be brought back
            var withIgnored = await service.GetIssuesAsync(includeIgnored: true);
            var ignoredIssue = withIgnored.Single(i => i.Id == noAsin.Id);
            Assert.True(ignoredIssue.Ignored);
            Assert.Equal(new[] { LibraryIssueCodes.MissingAsin }, ignoredIssue.Issues);
            Assert.Empty(withIgnored.Single(i => i.Id == healthy.Id).Issues);

            // When one is brought back, it is listed again; a missing book is reported
            Assert.True(await service.SetIgnoredAsync(noAsin.Id, false));
            Assert.False((await service.GetIssuesAsync()).Single(i => i.Id == noAsin.Id).Ignored);
            Assert.False(await service.SetIgnoredAsync(999999, true));
        }

        [Fact]
        [Trait("Method", "GetIssueAsync")]
        [Trait("Scenario", "ReturnsOneBooksIssuesWithTheSameRules")]
        public async Task GetIssueAsync_ReturnsOneBooksIssuesWithTheSameRules()
        {
            var noRegion = await AddBook("Primal Hunter 4", "B0BK9W82PQ", Asin("B0BK9W82PQ", null));
            var healthy = await AddBook("Primal Hunter", "B0FXXVV42J", Asin("B0FXXVV42J", "fr"));
            var service = _provider.GetRequiredService<ILibraryIssueService>();

            var issue = await service.GetIssueAsync(noRegion.Id);
            Assert.NotNull(issue);
            Assert.Equal(new[] { LibraryIssueCodes.MissingRegion }, issue!.Issues);
            Assert.Empty((await service.GetIssueAsync(healthy.Id))!.Issues);
            Assert.Null(await service.GetIssueAsync(999999));
        }
    }
}
