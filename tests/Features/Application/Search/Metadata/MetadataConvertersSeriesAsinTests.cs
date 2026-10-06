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
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Search.Metadata
{
    /// <summary>
    /// Audnexus returns an ASIN for each series a book belongs to, and
    /// AudiobookSeriesMembership has a SeriesAsin column to hold it. The converter maps the
    /// series name and position but drops the ASIN, so SeriesAsin is never populated.
    ///
    /// The series ASIN is the stable identifier: a book ASIN is per-marketplace and
    /// per-narrator, and a series name is free text that varies between editions.
    ///
    /// Data below is from the live Audnexus record for H. Rider Haggard's "She and Allan"
    /// (B00CQ5WAXW), which belongs to two series at once.
    /// </summary>
    [Trait("Name", "MetadataConvertersSeriesAsinTests")]
    [Trait("Category", "Application")]
    public class MetadataConvertersSeriesAsinTests : BaseTests
    {
        private static MetadataConverters Converter() =>
            new(imageCacheService: null,
                NullLogger<MetadataConverters>.Instance,
                requestContextAccessor: null);

        private static AudnexusBookResponse SheAndAllan() => new()
        {
            Asin = "B00CQ5WAXW",
            Title = "She and Allan",
            SeriesPrimary = new AudnexusSeries
            {
                Asin = "B01E633FQM",
                Name = "Ayesha",
                Position = "0",
            },
            SeriesSecondary = new AudnexusSeries
            {
                Asin = "B01F5TL5K4",
                Name = "Allan Quatermain",
                Position = "7",
            },
        };

        [Fact]
        public void PrimarySeriesAsin_IsMapped()
        {
            var metadata = Converter().ConvertAudnexusToMetadata(SheAndAllan(), "B00CQ5WAXW");

            var primary = metadata.SeriesMemberships?.Single(m => m.IsPrimary);

            Assert.NotNull(primary);
            Assert.Equal("Ayesha", primary!.SeriesName);
            Assert.Equal("0", primary.SeriesNumber);
            Assert.Equal("B01E633FQM", primary.SeriesAsin);
        }

        [Fact]
        public void SecondarySeriesAsin_IsMapped()
        {
            var metadata = Converter().ConvertAudnexusToMetadata(SheAndAllan(), "B00CQ5WAXW");

            var secondary = metadata.SeriesMemberships?.Single(m => !m.IsPrimary);

            Assert.NotNull(secondary);
            Assert.Equal("Allan Quatermain", secondary!.SeriesName);
            Assert.Equal("7", secondary.SeriesNumber);
            Assert.Equal("B01F5TL5K4", secondary.SeriesAsin);
        }

        [Fact]
        public void ABookInTwoSeries_KeepsBothMemberships()
        {
            var metadata = Converter().ConvertAudnexusToMetadata(SheAndAllan(), "B00CQ5WAXW");

            Assert.Equal(2, metadata.SeriesMemberships?.Count);
        }

        /// <summary>
        /// AudnexusSeriesMapper copies the ASIN across verbatim, so a record that answers with an
        /// empty or whitespace ASIN reaches AudiobookSeriesMembershipHelper.Normalize with it. The
        /// membership must survive on the strength of its name, with a null identifier rather than
        /// a blank one, so nothing downstream has to tell "" and null apart. PR 961 pins the same
        /// rule on the monitoring path; this pins it on the converter path.
        /// </summary>
        [Fact]
        public void SeriesWithBlankAsin_KeepsTheMembershipAndStoresNoIdentifier()
        {
            var book = SheAndAllan();
            book.SeriesPrimary!.Asin = "   ";
            book.SeriesSecondary = null;

            var metadata = Converter().ConvertAudnexusToMetadata(book, "B00CQ5WAXW");

            var membership = Assert.Single(metadata.SeriesMemberships!);
            Assert.Equal("Ayesha", membership.SeriesName);
            Assert.Equal("0", membership.SeriesNumber);
            Assert.Null(membership.SeriesAsin);
            Assert.True(membership.IsPrimary);
        }

        [Fact]
        public void SeriesWithEmptyAsin_StoresNoIdentifier()
        {
            var book = SheAndAllan();
            book.SeriesPrimary!.Asin = string.Empty;
            book.SeriesSecondary = null;

            var metadata = Converter().ConvertAudnexusToMetadata(book, "B00CQ5WAXW");

            var membership = Assert.Single(metadata.SeriesMemberships!);
            Assert.Null(membership.SeriesAsin);
        }
    }
}
