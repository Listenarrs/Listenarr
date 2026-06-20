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


namespace Listenarr.Tests.Features.Application.Search.Core
{
    public class SearchWorkflowHelperTests
    {
        [Fact]
        public void Parse_Prefers_Asin_And_Removes_Prefixed_Ranges_From_Fallback_Query()
        {
            var parsed = SearchQueryParser.Parse("space opera AUTHOR: Martha Wells TITLE: Network Effect ASIN: B088C4Z8T5");

            Assert.Equal("ASIN", parsed.SearchType);
            Assert.Equal("B088C4Z8T5", parsed.Asin);
            Assert.Equal("Martha Wells", parsed.Author);
            Assert.Equal("Network Effect", parsed.Title);
            Assert.Equal("space opera", parsed.ActualQuery);
        }

        [Theory]
        [InlineData("ISBN: 9781234567890", "ISBN")]
        [InlineData("AUTHOR: Becky Chambers TITLE: A Psalm for the Wild-Built", "AUTHOR_TITLE")]
        [InlineData("AUTHOR: Becky Chambers", "AUTHOR")]
        [InlineData("TITLE: A Psalm for the Wild-Built", "TITLE")]
        public void Parse_Determines_Targeted_Search_Type(string query, string expectedSearchType)
        {
            var parsed = SearchQueryParser.Parse(query);

            Assert.Equal(expectedSearchType, parsed.SearchType);
        }

        [Fact]
        public void ComputeContainmentScore_Preserves_Hyphenated_Tokens()
        {
            var result = new SearchResult
            {
                Title = "Stargate SG-1",
                Artist = "Ashley McConnell"
            };

            var score = SearchResultMatchEvaluator.ComputeContainmentScore(result, "SG-1");

            Assert.Equal(1.0, score);
        }

        [Fact]
        public void ComputeFuzzySimilarity_Normalizes_Punctuation()
        {
            var similarity = SearchResultMatchEvaluator.ComputeFuzzySimilarity("The Long Way to a Small, Angry Planet", "The Long Way to a Small Angry Planet");

            Assert.Equal(1.0, similarity);
        }
    }
}
