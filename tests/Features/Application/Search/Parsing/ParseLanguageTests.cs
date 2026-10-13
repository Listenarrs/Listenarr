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
        [InlineData("Some.Title.Dansk.M4B", "Danish")]
        [InlineData("Ein Deutsch Hoerbuch", "German")]
        [InlineData("Title [SWE]", "Swedish")]
        [InlineData("Title.Svenska.M4B", "Swedish")]
        [InlineData("Title - Italiano", "Italian")]
        // Ambiguous short codes only count inside brackets/parens...
        [InlineData("Some Title [IT]", "Italian")]
        [InlineData("Book (NO)", "Norwegian")]
        // ...or in the release-tag position (delimited code directly before the year), which is
        // how audiobook scene releases encode language (parallel to the Danish WEB-DK-2026).
        [InlineData("Colleen Hoover - Verity-AUDiOBOOK-WEB-SE-2023-CRAViNGS iNT-xpost", "Swedish")]
        [InlineData("Some.Book-WEB-IT-2021-GROUP", "Italian")]
        [InlineData("Another-WEB-NO-2019-GRP", "Norwegian")]
        // ...but a bare ambiguous code with no year/bracket context must NOT be a language.
        [InlineData("Great book, it is", null)]
        [InlineData("Say no more", null)]
        [InlineData("Verity-AUDiOBOOK-WEB-SE-CRAViNGS", null)]
        // A name-like alias (DAN/NOR/EL) with no bracket/scene-tag context is NOT a language;
        // otherwise an author's first name ("Dan") would win over the real release tag.
        [InlineData("Some Title - DAN", null)]
        // Regression (PR #1031 review): the author's first name must not beat the release's
        // own language tag. "Dan Brown ...-WEB-DE-2017" is German (the DE scene tag), and
        // "Dan Brown ... Eng" is English (the ENG tag) — neither is Danish.
        [InlineData("Dan Brown - Origin-AUDiOBOOK-WEB-DE-2017-CRAViNGS", "German")]
        [InlineData("Dan Brown, The Lost Symbol Audiobook Eng MP3", "English")]
        // Regression: an ordinary title word immediately before a year is not a scene tag.
        [InlineData("Stephen King - It 1986", null)]
        [InlineData("Book No 2022", null)]
        // Regression: a qualified scene tag beats an ordinary word in the book title.
        [InlineData("French.Lessons-AUDiOBOOK-WEB-SE-2023-GRP", "Swedish")]
        public void ParseLanguageFromText_RecognizesCodes(string input, string? expected)
        {
            var result = SearchResultAttributeParser.ParseLanguageFromText(input);
            Assert.Equal(expected, result);
        }
    }
}
