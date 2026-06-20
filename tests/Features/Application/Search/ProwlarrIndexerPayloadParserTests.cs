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

using System.Text.Json;

namespace Listenarr.Tests.Features.Application.Search
{
    public class ProwlarrIndexerPayloadParserTests
    {
        [Fact]
        public void GetTagValues_ResolvesNumericTagsThroughTagMap()
        {
            using var document = JsonDocument.Parse("""
                {
                  "tags": [1, { "id": 2 }, { "label": "direct" }],
                  "fields": [
                    { "name": "tagNames", "value": "field-tag" }
                  ]
                }
                """);
            var tagMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["1"] = "audiobook",
                ["2"] = "vip"
            };

            var tags = ProwlarrIndexerPayloadParser.GetTagValues(document.RootElement, tagMap);

            Assert.Contains("1", tags);
            Assert.Contains("audiobook", tags);
            Assert.Contains("2", tags);
            Assert.Contains("vip", tags);
            Assert.Contains("direct", tags);
            Assert.Contains("field-tag", tags);
        }

        [Fact]
        public void PayloadRequiresTagMap_ReturnsTrue_WhenTagsAreOnlyNumeric()
        {
            using var document = JsonDocument.Parse("""[{ "tags": [1, 2] }]""");

            Assert.True(ProwlarrIndexerPayloadParser.PayloadRequiresTagMap(document.RootElement));
        }

        [Fact]
        public void GetCategoryIds_ReadsCapabilitiesDirectCategoriesAndFieldCategories()
        {
            using var document = JsonDocument.Parse("""
                {
                  "capabilities": {
                    "categories": [
                      { "id": 3000, "subCategories": [{ "id": 3030 }] }
                    ]
                  },
                  "categories": ["8010"],
                  "fields": [
                    { "name": "categories", "value": [{ "id": 8020 }] }
                  ]
                }
                """);

            var categories = ProwlarrIndexerPayloadParser.GetCategoryIds(document.RootElement);

            Assert.Contains(3000, categories);
            Assert.Contains(3030, categories);
            Assert.Contains(8010, categories);
            Assert.Contains(8020, categories);
        }
    }
}
