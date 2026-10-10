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
        // Non-English codes and full/native names are now recognized.
        [InlineData("Colleen.Hoover.-.Verity.Moerkt.Bedrag-AUDiOBOOK-WEB-DK-2026-CRAViNGS.iNT", "Danish")]
        [InlineData("Some Title - DAN", "Danish")]
        [InlineData("Some.Title.Dansk.M4B", "Danish")]
        [InlineData("Ein Deutsch Hoerbuch", "German")]
        [InlineData("Title [SWE]", "Swedish")]
        [InlineData("Title.Svenska.M4B", "Swedish")]
        [InlineData("Title - Italiano", "Italian")]
        // Ambiguous short codes only count inside brackets/parens...
        [InlineData("Some Title [IT]", "Italian")]
        [InlineData("Book (NO)", "Norwegian")]
        // ...never as a bare scene tag, so these must NOT be mistaken for a language.
        [InlineData("Verity-AUDiOBOOK-WEB-SE-2023-CRAViNGS", null)]
        [InlineData("Great book, it is", null)]
        [InlineData("Say no more", null)]
        public void ParseLanguageFromText_RecognizesCodes(string input, string? expected)
        {
            var result = SearchResultAttributeParser.ParseLanguageFromText(input);
            Assert.Equal(expected, result);
        }
    }
}
