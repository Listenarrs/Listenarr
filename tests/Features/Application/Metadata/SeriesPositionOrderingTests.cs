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
using System.Globalization;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Metadata
{
    /// <summary>
    /// A series position arrives from Audible's `sequence`/`sort` field as a string that always
    /// uses '.' as the decimal separator. It is sort data, not display text, so it must parse the
    /// same way on every server.
    ///
    /// A default container runs under the invariant culture and was never affected. The bug shows
    /// up once the process has a real culture, which happens when LANG or LC_ALL is set, and on a
    /// desktop install that inherits the operating system's locale:
    ///
    ///   de-DE: '.' is the GROUP separator, so "1.5" parsed as 15 and sorted after book 10.
    ///   fr-FR: "1.5" did not parse at all, fell to decimal.MaxValue, and sorted last.
    /// </summary>
    [Trait("Name", "SeriesPositionOrderingTests")]
    [Trait("Category", "Unit")]
    public class SeriesPositionOrderingTests : BaseTests
    {
        private static void InCulture(string culture, Action body)
        {
            var original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo(culture);
                body();
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        [Theory]
        [InlineData("")]        // invariant: what a container with no LANG gets
        [InlineData("en-US")]
        [InlineData("de-DE")]   // '.' is the group separator
        [InlineData("fr-FR")]   // ',' is the decimal separator, '.' parses as nothing
        public void DecimalPosition_ParsesTheSameUnderEveryServerCulture(string culture)
        {
            InCulture(culture, () =>
                Assert.Equal(1.5m, AudibleSeriesWorkflow.ParseSeriesPosition("1.5")));
        }

        [Theory]
        [InlineData("")]
        [InlineData("en-US")]
        [InlineData("de-DE")]
        [InlineData("fr-FR")]
        public void SeriesOrder_IsTheSameUnderEveryServerCulture(string culture)
        {
            InCulture(culture, () =>
            {
                string[] positions = ["1", "1.5", "2", "10", "1-4"];

                var ordered = positions
                    .OrderBy(AudibleSeriesWorkflow.ParseSeriesPosition)
                    .ToArray();

                Assert.Equal(["1", "1.5", "2", "10", "1-4"], ordered);
            });
        }

        [Fact]
        public void WholeNumbersAndZero_Parse()
        {
            // "0" is a real position: a prequel sits at position 0 of its series.
            Assert.Equal(0m, AudibleSeriesWorkflow.ParseSeriesPosition("0"));
            Assert.Equal(10m, AudibleSeriesWorkflow.ParseSeriesPosition("10"));
        }

        [Theory]
        [InlineData("1-4")]     // an omnibus covering books 1 to 4
        [InlineData("")]
        [InlineData(null)]
        public void NonDecimalPosition_StillSortsLast(string? position)
        {
            // Deliberate and unchanged: a position that is not a decimal cannot be ordered
            // numerically, so it goes to the end rather than to the front.
            Assert.Equal(decimal.MaxValue, AudibleSeriesWorkflow.ParseSeriesPosition(position));
        }
    }
}
