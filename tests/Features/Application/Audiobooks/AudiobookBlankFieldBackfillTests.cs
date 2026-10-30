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
using Listenarr.Application.Audiobooks.Metadata;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Audiobooks
{
    [Trait("Name", "AudiobookBlankFieldBackfillTests")]
    [Trait("Category", "Application")]
    public sealed class AudiobookBlankFieldBackfillTests : BaseTests
    {
        private static AudibleBookMetadata FullProviderRecord() => new()
        {
            Asin = "B00B5HZGUG",
            Title = "The Martian (Provider Title)",
            Subtitle = "A Novel",
            Description = "Six days ago, astronaut Mark Watney became one of the first people to walk on Mars.",
            Publisher = "Podium Audio",
            Language = "english",
            PublishYear = "2013",
            PublishedDate = "2013-03-22",
            Runtime = 653,
            Authors = new List<string> { "Andy Weir" },
            Narrators = new List<string> { "R. C. Bray" },
            Genres = new List<string> { "Science Fiction", "Hard Science Fiction" },
            Series = "Provider Series",
            SeriesNumber = "1",
            SeriesMemberships = new List<AudiobookSeriesMembership>
            {
                new() { SeriesName = "Provider Series", SeriesNumber = "1", IsPrimary = true }
            }
        };

        [Fact]
        public void Apply_FillsOnlyBlankFields_AndReportsThem()
        {
            var audiobook = new Audiobook
            {
                Title = "The Martian",
                Authors = new List<string> { "Andy Weir" },
                Narrators = new List<string>(),
                Language = "  ",
            };

            var filled = AudiobookBlankFieldBackfill.Apply(audiobook, FullProviderRecord());

            // Populated fields survive untouched.
            Assert.Equal("The Martian", audiobook.Title);
            Assert.Equal(new[] { "Andy Weir" }, audiobook.Authors);

            // Blank (null, empty list, whitespace) fields are filled.
            Assert.Equal("A Novel", audiobook.Subtitle);
            Assert.StartsWith("Six days ago", audiobook.Description);
            Assert.Equal("Podium Audio", audiobook.Publisher);
            Assert.Equal("english", audiobook.Language);
            Assert.Equal("2013", audiobook.PublishYear);
            Assert.Equal("2013-03-22", audiobook.PublishedDate);
            Assert.Equal(653, audiobook.Runtime);
            Assert.Equal(new[] { "R. C. Bray" }, audiobook.Narrators);
            Assert.Equal(new[] { "Science Fiction", "Hard Science Fiction" }, audiobook.Genres);
            Assert.Equal("Provider Series", audiobook.Series);
            Assert.Equal("1", audiobook.SeriesNumber);

            Assert.DoesNotContain(nameof(Audiobook.Title), filled);
            Assert.DoesNotContain(nameof(Audiobook.Authors), filled);
            Assert.Contains(nameof(Audiobook.Description), filled);
            Assert.Contains(nameof(Audiobook.Narrators), filled);
            Assert.Contains(nameof(Audiobook.Language), filled);
            Assert.Contains(nameof(Audiobook.Series), filled);
        }

        [Fact]
        public void Apply_CompleteRecord_ChangesNothing()
        {
            var audiobook = new Audiobook
            {
                Title = "The Martian",
                Subtitle = "My Subtitle",
                Description = "My description.",
                Publisher = "My Publisher",
                Language = "German",
                PublishYear = "2014",
                PublishedDate = "2014-01-01",
                Runtime = 600,
                Authors = new List<string> { "Someone Else" },
                Narrators = new List<string> { "My Narrator" },
                Genres = new List<string> { "My Genre" },
                Series = "My Series",
                SeriesNumber = "3",
                SeriesMemberships = new List<AudiobookSeriesMembership>
                {
                    new() { SeriesName = "My Series", SeriesNumber = "3", IsPrimary = true }
                }
            };

            var filled = AudiobookBlankFieldBackfill.Apply(audiobook, FullProviderRecord());

            Assert.Empty(filled);
            Assert.Equal("My description.", audiobook.Description);
            Assert.Equal("My Narrator", Assert.Single(audiobook.Narrators!));
            Assert.Equal("My Series", audiobook.Series);
            Assert.Equal("3", audiobook.SeriesNumber);
            Assert.Equal("My Series", Assert.Single(audiobook.SeriesMemberships!).SeriesName);
        }

        [Fact]
        public void Apply_ProviderBlanks_NeverBlankTheRecord()
        {
            var audiobook = new Audiobook { Title = "Kept", Description = null };
            var sparse = new AudibleBookMetadata { Title = "   ", Description = "", Narrators = new List<string> { " " } };

            var filled = AudiobookBlankFieldBackfill.Apply(audiobook, sparse);

            Assert.Empty(filled);
            Assert.Equal("Kept", audiobook.Title);
            Assert.Null(audiobook.Description);
            Assert.Null(audiobook.Narrators);
        }

        [Fact]
        public void Apply_LegacySingleAuthorNarrator_UsedWhenListsEmpty()
        {
            var audiobook = new Audiobook();
            var legacy = new AudibleBookMetadata { Author = "Solo Author", Narrator = "Solo Narrator" };

            AudiobookBlankFieldBackfill.Apply(audiobook, legacy);

            Assert.Equal("Solo Author", Assert.Single(audiobook.Authors!));
            Assert.Equal("Solo Narrator", Assert.Single(audiobook.Narrators!));
        }

        [Fact]
        public void Apply_ExistingSeriesPlacement_IsNotReplaced()
        {
            // A user-chosen series (legacy field only, no memberships) must not be
            // overridden by the provider's series.
            var audiobook = new Audiobook { Series = "Hand-Picked Series", SeriesNumber = "7" };

            var filled = AudiobookBlankFieldBackfill.Apply(audiobook, FullProviderRecord());

            Assert.DoesNotContain(nameof(Audiobook.Series), filled);
            Assert.Equal("Hand-Picked Series", audiobook.Series);
            Assert.Equal("7", audiobook.SeriesNumber);
        }
    }
}
