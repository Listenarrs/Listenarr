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
using Listenarr.Application.Audiobooks;
using Listenarr.Application.Metadata;
using Listenarr.Domain.Models;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
{
    // Covers ScanBackgroundService.MatchEmbeddedTags — the embedded-tag confirmation
    // fallback used when path/filename heuristics cannot attribute a file to a book.
    public class ScanBackgroundServiceTagMatchTests
    {
        private static Audiobook Book(string? title, string? asin, params string[] authors) => new()
        {
            Title = title,
            Asin = asin,
            Authors = new List<string>(authors),
        };

        private static PathParsedMetadata Tags(string? title = null, string? author = null, string? asin = null) =>
            new() { Title = title, Author = author, Asin = asin };

        [Fact]
        public void MatchingAsin_IsDefinitive()
        {
            var book = Book("Das Tierarztpraktikum", "B004VQF7K2", "Markus Dittrich");
            var tags = Tags(title: "something totally different", author: "Nobody", asin: "B004VQF7K2");

            Assert.Equal(ScanBackgroundService.TagMatchReason.Asin, ScanBackgroundService.MatchEmbeddedTags(book, tags));
        }

        [Fact]
        public void Asin_IsCaseAndWhitespaceInsensitive()
        {
            var book = Book("X", "B004VQF7K2");
            var tags = Tags(asin: "  b004vqf7k2 ");

            Assert.Equal(ScanBackgroundService.TagMatchReason.Asin, ScanBackgroundService.MatchEmbeddedTags(book, tags));
        }

        [Fact]
        public void TitleAndAuthor_MatchWhenAsinDiffers()
        {
            // ab3-style: record ASIN differs from the file's, but title + author agree.
            var book = Book("Das Tierarztpraktikum", "B00U6W36DU", "Markus Dittrich");
            var tags = Tags(title: "Das Tierarztpraktikum", author: "Markus Dittrich", asin: "B004VQF7K2");

            Assert.Equal(ScanBackgroundService.TagMatchReason.TitleAndAuthor, ScanBackgroundService.MatchEmbeddedTags(book, tags));
        }

        [Fact]
        public void TitleMatch_ToleratesPunctuationAndSubtitle()
        {
            // Combined "Title: Subtitle" tag still matches the bare record title (normalized, substring).
            var book = Book("A Dance with Dragons", null, "George R.R. Martin");
            var tags = Tags(title: "A Dance with Dragons: A Song of Ice and Fire, Book 5", author: "George R. R. Martin");

            Assert.Equal(ScanBackgroundService.TagMatchReason.TitleAndAuthor, ScanBackgroundService.MatchEmbeddedTags(book, tags));
        }

        [Fact]
        public void TitleMatchButAuthorMismatch_IsNotAMatch()
        {
            var book = Book("Das Tierarztpraktikum", "B00U6W36DU", "Markus Dittrich");
            var tags = Tags(title: "Das Tierarztpraktikum", author: "Someone Else", asin: "B004VQF7K2");

            Assert.Equal(ScanBackgroundService.TagMatchReason.None, ScanBackgroundService.MatchEmbeddedTags(book, tags));
        }

        [Fact]
        public void AuthorMatchButTitleMismatch_IsNotAMatch()
        {
            var book = Book("Das Tierarztpraktikum", "B00U6W36DU", "Markus Dittrich");
            var tags = Tags(title: "An Unrelated Book", author: "Markus Dittrich", asin: "B004VQF7K2");

            Assert.Equal(ScanBackgroundService.TagMatchReason.None, ScanBackgroundService.MatchEmbeddedTags(book, tags));
        }

        [Fact]
        public void NoUsableSignals_IsNotAMatch()
        {
            var book = Book("Das Tierarztpraktikum", "B00U6W36DU", "Markus Dittrich");
            var tags = Tags(title: null, author: null, asin: null);

            Assert.Equal(ScanBackgroundService.TagMatchReason.None, ScanBackgroundService.MatchEmbeddedTags(book, tags));
        }

        [Fact]
        public void NullTags_IsNotAMatch()
        {
            var book = Book("Das Tierarztpraktikum", "B00U6W36DU", "Markus Dittrich");

            Assert.Equal(ScanBackgroundService.TagMatchReason.None, ScanBackgroundService.MatchEmbeddedTags(book, null));
        }

        [Fact]
        public void EmptyRecordAsin_DoesNotMatchEmptyTagAsin()
        {
            // Two missing ASINs must not be treated as "equal" — fall through to title/author.
            var book = Book("Das Tierarztpraktikum", asin: null, "Markus Dittrich");
            var tags = Tags(title: "Unrelated", author: "Markus Dittrich", asin: null);

            Assert.Equal(ScanBackgroundService.TagMatchReason.None, ScanBackgroundService.MatchEmbeddedTags(book, tags));
        }
    }
}
