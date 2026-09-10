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

namespace Listenarr.Tests.Features.Domain.Audiobooks
{
    [Trait("Name", "AudibleBookMetadataTests")]
    [Trait("Category", "Domain")]
    public class AudibleBookMetadataTests : BaseTests
    {
        [Fact]
        public void ToAudiobook_CarriesTheLegacySeriesAsinIntoTheFallbackMembership()
        {
            var metadata = new AudibleBookMetadata
            {
                Title = "The Final Empire",
                Series = "Mistborn",
                SeriesNumber = "1",
                SeriesAsin = "SERIESONE"
            };

            var audiobook = metadata.ToAudiobook();

            var membership = Assert.Single(audiobook.SeriesMemberships!);
            Assert.Equal("Mistborn", membership.SeriesName);
            Assert.Equal("1", membership.SeriesNumber);
            Assert.Equal("SERIESONE", membership.SeriesAsin);
            Assert.True(membership.IsPrimary);
        }
    }
}
