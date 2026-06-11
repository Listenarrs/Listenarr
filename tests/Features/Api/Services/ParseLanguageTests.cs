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
using Listenarr.Application.Search;

namespace Listenarr.Tests.Features.Api.Services
{
    public class ParseLanguageTests
    {
        [Theory]
        [InlineData("[ENG / M4B] Some Title", "English")]
        [InlineData("Some Title (EN)", "English")]
        [InlineData("Some Title EN", "English")]
        [InlineData("[DUT] Title", "Dutch")]
        [InlineData("Title - NL", "Dutch")]
        [InlineData("Title (DE)", "German")]
        [InlineData("[GER / MP3] Foo", "German")]
        [InlineData("Book Title FR", "French")]
        [InlineData("[FRE] Bar", "French")]
        [InlineData("No language here", null)]
        public void ParseLanguageFromText_RecognizesCodes(string input, string? expected)
        {
            // Create an uninitialized SearchService instance so we don't have to satisfy constructor dependencies
            var svcObj = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(SearchService));

            var method = typeof(SearchService).GetMethod("ParseLanguageFromText", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(method);

            var result = method.Invoke(svcObj, new object[] { input }) as string;
            Assert.Equal(expected, result);
        }
    }
}
