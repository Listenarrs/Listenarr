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

namespace Listenarr.Tests.Features.Application.Search.Parsing
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
            var result = SearchResultAttributeParser.ParseLanguageFromText(input);
            Assert.Equal(expected, result);
        }
    }
}
