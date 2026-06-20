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
using Microsoft.AspNetCore.Mvc;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    [Trait("Area", "LibraryApi")]
    [Trait("Name", "LibraryController_UpdateAudiobookTests")]
    [Trait("Category", "LibraryController")]
    public class LibraryController_UpdateAudiobookTests : BaseTests
    {
        [Fact]
        [Trait("Method", "UpdateAudiobook")]
        [Trait("Scenario", "PersistsExpandedMetadataFields")]
        public async Task UpdateAudiobook_PersistsExpandedMetadataFields()
        {
            // Given
            var existingAudiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Original Title",
                Subtitle = "Original Subtitle",
                Authors = new List<string> { "Original Author" },
                Narrators = new List<string> { "Original Narrator" },
                Description = "Original description",
                Publisher = "Original Publisher",
                Language = "english",
                PublishedDate = "2024-01-01",
                PublishYear = "2024",
                Runtime = 600,
                Edition = "Original Edition",
                Version = "Original Version",
                Series = "Original Series",
                SeriesNumber = "1",
                SeriesMemberships = new List<AudiobookSeriesMembership>
                {
                    new()
                    {
                        SeriesName = "Original Series",
                        SeriesNumber = "1",
                        IsPrimary = true,
                        SortOrder = 0
                    }
                },
                Genres = new List<string> { "Fantasy" },
                ImageUrl = "https://example.com/original.jpg",
                Tags = new List<string> { "tag-one" },
                Monitored = true,
                Explicit = false,
                Abridged = false,
            });

            var controller = _provider.GetRequiredService<LibraryController>();

            var updatedAudiobook = new Audiobook
            {
                Title = "Edited Title",
                Subtitle = "Edited Subtitle",
                Authors = new List<string> { "Edited Author" },
                Narrators = new List<string> { "Edited Narrator" },
                Description = "Edited description",
                Publisher = "Edited Publisher",
                Language = "swedish",
                PublishedDate = "2025-02-01",
                PublishYear = "2025",
                Runtime = 720,
                Edition = "Collector Edition",
                Version = "Edited Version",
                Series = "Edited Universe",
                SeriesNumber = "4",
                SeriesMemberships = new List<AudiobookSeriesMembership>
                {
                    new()
                    {
                        SeriesName = "Edited Universe",
                        SeriesNumber = "4",
                        IsPrimary = true,
                        SortOrder = 0
                    },
                    new()
                    {
                        SeriesName = "Anthology Line",
                        SeriesNumber = "12",
                        IsPrimary = false,
                        SortOrder = 1
                    }
                },
                Genres = new List<string> { "Sci-Fi", "Adventure" },
                ImageUrl = "https://example.com/edited.jpg",
                Tags = new List<string> { "tag-two" },
                Monitored = false,
                Explicit = true,
                Abridged = true,
            };

            // When
            var actionResult = await controller.UpdateAudiobook(existingAudiobook.Id, updatedAudiobook);

            // Then
            Assert.IsType<OkObjectResult>(actionResult);
            var storedAudiobook = await _audiobookRepository.GetByIdAsync(existingAudiobook.Id);
            Assert.NotNull(storedAudiobook);
            Assert.Equal("Edited Title", storedAudiobook.Title);
            Assert.Equal("Edited Subtitle", storedAudiobook.Subtitle);
            Assert.Equal(new List<string> { "Edited Author" }, storedAudiobook.Authors);
            Assert.Equal(new List<string> { "Edited Narrator" }, storedAudiobook.Narrators);
            Assert.Equal("Edited description", storedAudiobook.Description);
            Assert.Equal("Edited Publisher", storedAudiobook.Publisher);
            Assert.Equal("swedish", storedAudiobook.Language);
            Assert.Equal("2025-02-01", storedAudiobook.PublishedDate);
            Assert.Equal("2025", storedAudiobook.PublishYear);
            Assert.Equal(720, storedAudiobook.Runtime);
            Assert.Equal("Collector Edition", storedAudiobook.Edition);
            Assert.Equal("Edited Version", storedAudiobook.Version);
            Assert.Equal("Edited Universe", storedAudiobook.Series);
            Assert.Equal("4", storedAudiobook.SeriesNumber);
            Assert.NotNull(storedAudiobook.SeriesMemberships);
            Assert.Collection(
                storedAudiobook.SeriesMemberships!,
                membership =>
                {
                    Assert.Equal("Edited Universe", membership.SeriesName);
                    Assert.Equal("4", membership.SeriesNumber);
                    Assert.True(membership.IsPrimary);
                },
                membership =>
                {
                    Assert.Equal("Anthology Line", membership.SeriesName);
                    Assert.Equal("12", membership.SeriesNumber);
                    Assert.False(membership.IsPrimary);
                });
            Assert.Equal(new List<string> { "Sci-Fi", "Adventure" }, storedAudiobook.Genres);
            Assert.Equal("https://example.com/edited.jpg", storedAudiobook.ImageUrl);
            Assert.Equal(new List<string> { "tag-two" }, storedAudiobook.Tags);
            Assert.False(storedAudiobook.Monitored);
            Assert.True(storedAudiobook.Explicit);
            Assert.True(storedAudiobook.Abridged);
        }
    }
}
