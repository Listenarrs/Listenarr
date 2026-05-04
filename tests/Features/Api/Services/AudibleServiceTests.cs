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
using System.Reflection;
using Listenarr.Application.Metadata;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
{
    [Trait("Name", "AudibleServiceTests")]
    [Trait("Category", "AudibleService")]
    [Trait("Third-Party", "Audible")]
    public class AudibleServiceTests
    {
        private static bool InvokeSearchResultIndicatesPodcast(AudibleSearchResult r)
        {
            var method = typeof(AudibleService).GetMethod("SearchResultIndicatesPodcast", BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null) throw new InvalidOperationException("Could not find SearchResultIndicatesPodcast method");
            return (bool)method.Invoke(null, new object[] { r });
        }

        [Fact]
        [Trait("Method", "SearchResultIndicatesPodcast")]
        public void ContentDeliveryBook_PreventsPodcastDetection()
        {
            var r = new AudibleSearchResult
            {
                ContentType = "podcast",
                ContentDeliveryType = "SinglePartBook"
            };

            var isPodcast = InvokeSearchResultIndicatesPodcast(r);
            Assert.False(isPodcast);
        }

        [Fact]
        [Trait("Method", "SearchResultIndicatesPodcast")]
        public void ContentTypePodcast_DetectedWhenNoBookDelivery()
        {
            var r = new AudibleSearchResult
            {
                ContentType = "podcast",
                ContentDeliveryType = null
            };

            var isPodcast = InvokeSearchResultIndicatesPodcast(r);
            Assert.True(isPodcast);
        }

        [Theory]
        [Trait("Method", "RemoveDiacritics")]
        [InlineData("Åsa Larsson", "Asa Larsson")]
        [InlineData("Ärzte Öberg", "Arzte Oberg")]
        [InlineData("café naïve", "cafe naive")]
        [InlineData("Björk Guðmundsdóttir", "Bjork Guðmundsdottir")]
        [InlineData("Harry Potter", "Harry Potter")]  // ASCII unchanged
        [InlineData("", "")]                           // empty unchanged
        public void RemoveDiacritics_StripsAccents(string input, string expected)
        {
            var result = AudibleService.RemoveDiacritics(input);
            Assert.Equal(expected, result);
        }

        [Fact]
        [Trait("Method", "RemoveDiacritics")]
        public void RemoveDiacritics_Null_ReturnsNull()
        {
            var result = AudibleService.RemoveDiacritics(null!);
            Assert.Null(result);
        }
    }
}
