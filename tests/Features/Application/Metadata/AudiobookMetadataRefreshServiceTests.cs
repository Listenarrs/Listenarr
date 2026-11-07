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
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Metadata
{
    [Trait("Name", "AudiobookMetadataRefreshServiceTests")]
    [Trait("Category", "AudiobookMetadataRefresh")]
    public class AudiobookMetadataRefreshServiceTests : BaseTests
    {
        [Fact]
        public void FillMissingFields_FillsEmptyFields_WithoutOverwritingExisting()
        {
            var audiobook = new Audiobook
            {
                Title = "My Existing Title",   // already set — must be preserved
                Narrators = new List<string>() // empty — should be filled
            };
            var metadata = new AudibleBookMetadata
            {
                Title = "Provider Title",
                Narrators = new List<string> { "Erin Bennett" },
                Publisher = "Little, Brown & Company",
                PublishYear = "2018",
                Description = "A novel.",
                Runtime = 728
            };

            var changed = AudiobookMetadataRefreshService.FillMissingFields(audiobook, metadata);

            Assert.True(changed);
            Assert.Equal("My Existing Title", audiobook.Title);          // not overwritten
            Assert.Equal(new[] { "Erin Bennett" }, audiobook.Narrators); // filled
            Assert.Equal("Little, Brown & Company", audiobook.Publisher);
            Assert.Equal("2018", audiobook.PublishYear);
            Assert.Equal("A novel.", audiobook.Description);
            Assert.Equal(728, audiobook.Runtime);
        }

        [Fact]
        public void FillMissingFields_ReturnsFalse_WhenNothingToFill()
        {
            var audiobook = new Audiobook
            {
                Title = "Title",
                Publisher = "Publisher",
                Narrators = new List<string> { "Someone" }
            };
            var metadata = new AudibleBookMetadata
            {
                Title = "Other Title",
                Publisher = "Other Publisher",
                Narrators = new List<string> { "Someone Else" }
            };

            var changed = AudiobookMetadataRefreshService.FillMissingFields(audiobook, metadata);

            Assert.False(changed);
            Assert.Equal("Title", audiobook.Title);
            Assert.Equal("Publisher", audiobook.Publisher);
        }
    }
}
