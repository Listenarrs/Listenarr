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

namespace Listenarr.Tests.Features.Infrastructure.Metadata.Parsing
{
    public class PathMetadataParserTests
    {
        [Fact]
        public void ParseEmbeddedTagsFromFfprobeJson_ParsesStandardAsinTag()
        {
            var doc = JsonDocument.Parse("""
            {
              "format": {
                "tags": {
                  "album": "Alchemised",
                  "album_artist": "SenLinYu",
                  "ASIN": "B0DQR9D4YG"
                }
              }
            }
            """);

            var result = PathMetadataParser.ParseEmbeddedTagsFromFfprobeJson(doc.RootElement);

            Assert.Equal("Alchemised", result.Title);
            Assert.Equal("SenLinYu", result.Author);
            Assert.Equal("B0DQR9D4YG", result.Asin);
        }

        [Fact]
        public void ParseEmbeddedTagsFromFfprobeJson_ParsesMp3UserTextAsinTag()
        {
            var doc = JsonDocument.Parse("""
            {
              "format": {
                "tags": {
                  "album": "Alchemised",
                  "TXXX:ASIN": "B0DQR9D4YG"
                }
              }
            }
            """);

            var result = PathMetadataParser.ParseEmbeddedTagsFromFfprobeJson(doc.RootElement);

            Assert.Equal("B0DQR9D4YG", result.Asin);
        }

        [Fact]
        public void ParseEmbeddedTagsFromFfprobeJson_ParsesAppleFreeformAsinTag()
        {
            var doc = JsonDocument.Parse("""
            {
              "format": {
                "tags": {
                  "album": "Alchemised",
                  "----:com.apple.iTunes:ASIN": "amazon://asin/B0DQR9D4YG"
                }
              }
            }
            """);

            var result = PathMetadataParser.ParseEmbeddedTagsFromFfprobeJson(doc.RootElement);

            Assert.Equal("B0DQR9D4YG", result.Asin);
        }

        [Fact]
        public void ParseEmbeddedTagsFromFfprobeJson_ParsesColonSuffixedAsinTag()
        {
            var doc = JsonDocument.Parse("""
            {
              "format": {
                "tags": {
                  "ASIN:": "B0DQR9D4YG"
                }
              }
            }
            """);

            var result = PathMetadataParser.ParseEmbeddedTagsFromFfprobeJson(doc.RootElement);

            Assert.Equal("B0DQR9D4YG", result.Asin);
        }

        [Fact]
        public void ParseEmbeddedTagsFromFfprobeJson_ParsesCdekTagContainingAsin()
        {
            var doc = JsonDocument.Parse("""
            {
              "format": {
                "tags": {
                  "CDEK:": "amazon://asin/B0DQR9D4YG"
                }
              }
            }
            """);

            var result = PathMetadataParser.ParseEmbeddedTagsFromFfprobeJson(doc.RootElement);

            Assert.Equal("B0DQR9D4YG", result.Asin);
        }
    }
}
