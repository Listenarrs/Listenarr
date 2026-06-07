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

using Listenarr.Domain.Common;
using Listenarr.Domain.Models;
using Xunit;

namespace Listenarr.Tests.Features.Domain.Common
{
    [Trait("Name", "AudiobookSeriesMembershipHelperTests")]
    [Trait("Category", "Domain")]
    public class AudiobookSeriesMembershipHelperTests
    {
        [Fact]
        [Trait("Method", "Normalize")]
        [Trait("Scenario", "KeepsChosenPrimaryAtNonZeroIndex")]
        public void Normalize_KeepsChosenPrimary_WhenItIsNotTheFirstEntry()
        {
            // Given a membership list where the user's chosen primary is NOT the first entry
            var memberships = new List<AudiobookSeriesMembership>
            {
                new() { SeriesName = "Publication Order", SeriesNumber = "1", IsPrimary = false },
                new() { SeriesName = "Chronological Order", SeriesNumber = "3", IsPrimary = true },
            };

            // When
            var result = AudiobookSeriesMembershipHelper.Normalize(memberships);

            // Then the chosen (second) series stays primary — never reverts to the first
            var primary = AudiobookSeriesMembershipHelper.GetPrimaryMembership(result);
            Assert.Equal("Chronological Order", primary?.SeriesName);
            Assert.Single(result, m => m.IsPrimary);
        }

        [Fact]
        [Trait("Method", "ApplyToAudiobookPreservingPrimary")]
        [Trait("Scenario", "PreservesUserPrimaryWhenProviderStillReturnsIt")]
        public void ApplyToAudiobookPreservingPrimary_KeepsUserChoice_WhenProviderStillReturnsSeries()
        {
            // Given an audiobook whose user-chosen primary is the chronological-order series
            var audiobook = new Audiobook
            {
                Title = "Patriot Games",
                SeriesMemberships = new List<AudiobookSeriesMembership>
                {
                    new() { SeriesName = "Publication Order", SeriesNumber = "1", SeriesAsin = "PUB", IsPrimary = false, SortOrder = 0 },
                    new() { SeriesName = "Chronological Order", SeriesNumber = "3", SeriesAsin = "CHR", IsPrimary = true, SortOrder = 1 },
                },
            };
            AudiobookSeriesMembershipHelper.ApplyPrimarySeriesFields(audiobook);

            // When a metadata rescan returns the provider's data (publication-order marked primary)
            var providerMemberships = new List<AudiobookSeriesMembership>
            {
                new() { SeriesName = "Publication Order", SeriesNumber = "1", SeriesAsin = "PUB", IsPrimary = true },
                new() { SeriesName = "Chronological Order", SeriesNumber = "3", SeriesAsin = "CHR", IsPrimary = false },
            };
            AudiobookSeriesMembershipHelper.ApplyToAudiobookPreservingPrimary(
                audiobook, providerMemberships, "Publication Order", "1");

            // Then the user's chosen primary is retained (incl. the denormalized fields used for naming)
            var primary = AudiobookSeriesMembershipHelper.GetPrimaryMembership(audiobook.SeriesMemberships);
            Assert.Equal("Chronological Order", primary?.SeriesName);
            Assert.Equal("Chronological Order", audiobook.Series);
            Assert.Equal("3", audiobook.SeriesNumber);
            Assert.Single(audiobook.SeriesMemberships!, m => m.IsPrimary);
        }

        [Fact]
        [Trait("Method", "ApplyToAudiobookPreservingPrimary")]
        [Trait("Scenario", "FallsBackToProviderPrimaryWhenChosenSeriesRemoved")]
        public void ApplyToAudiobookPreservingPrimary_UsesProviderPrimary_WhenChosenSeriesNoLongerReturned()
        {
            // Given a user-chosen primary that the provider no longer returns
            var audiobook = new Audiobook
            {
                Title = "Patriot Games",
                SeriesMemberships = new List<AudiobookSeriesMembership>
                {
                    new() { SeriesName = "Chronological Order", SeriesNumber = "3", SeriesAsin = "CHR", IsPrimary = true, SortOrder = 0 },
                },
            };
            AudiobookSeriesMembershipHelper.ApplyPrimarySeriesFields(audiobook);

            // When the rescan only returns a different series
            var providerMemberships = new List<AudiobookSeriesMembership>
            {
                new() { SeriesName = "Publication Order", SeriesNumber = "1", SeriesAsin = "PUB", IsPrimary = true },
            };
            AudiobookSeriesMembershipHelper.ApplyToAudiobookPreservingPrimary(
                audiobook, providerMemberships, "Publication Order", "1");

            // Then it falls back to the provider's primary
            var primary = AudiobookSeriesMembershipHelper.GetPrimaryMembership(audiobook.SeriesMemberships);
            Assert.Equal("Publication Order", primary?.SeriesName);
            Assert.Single(audiobook.SeriesMemberships!, m => m.IsPrimary);
        }
    }
}
