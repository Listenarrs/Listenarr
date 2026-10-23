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
using Listenarr.Infrastructure.Ffmpeg.Metadata;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Ffmpeg.Metadata
{
    [Trait("Name", "FfprobeTagMetadataMapperTests")]
    [Trait("Category", "FfprobeTagMetadataMapper")]
    public class FfprobeTagMetadataMapperTests : BaseTests
    {
        private static JsonElement TagsFrom(string json)
        {
            // JsonDocument is disposable, but the returned element is only read synchronously
            // within each test, so cloning keeps it valid without keeping the document alive.
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }

        [Fact]
        public void Apply_ReadsAsinAndIsbnFromTags()
        {
            var metadata = new AudioMetadata();
            var tags = TagsFrom("{\"title\":\"A Book\",\"ASIN\":\"B0078PA1OA\",\"ISBN\":\"9781250120207\"}");

            FfprobeTagMetadataMapper.Apply(metadata, tags);

            Assert.Equal("B0078PA1OA", metadata.Asin);
            Assert.Equal("9781250120207", metadata.Isbn);
            Assert.Equal("A Book", metadata.Title);
        }

        [Fact]
        public void Apply_MatchesAsinTag_CaseInsensitively()
        {
            var metadata = new AudioMetadata();
            var tags = TagsFrom("{\"asin\":\"B0078PA1OA\"}");

            FfprobeTagMetadataMapper.Apply(metadata, tags);

            Assert.Equal("B0078PA1OA", metadata.Asin);
        }

        [Fact]
        public void Apply_ReadsAudibleAsinTagVariant()
        {
            var metadata = new AudioMetadata();
            var tags = TagsFrom("{\"AUDIBLE_ASIN\":\"B0078PA1OA\"}");

            FfprobeTagMetadataMapper.Apply(metadata, tags);

            Assert.Equal("B0078PA1OA", metadata.Asin);
        }

        [Fact]
        public void Apply_LeavesAsinNull_WhenNoAsinTagPresent()
        {
            var metadata = new AudioMetadata();
            var tags = TagsFrom("{\"title\":\"A Book\"}");

            FfprobeTagMetadataMapper.Apply(metadata, tags);

            Assert.Null(metadata.Asin);
            Assert.Null(metadata.Isbn);
        }

        [Fact]
        public void Apply_DoesNotOverwriteExistingAsin()
        {
            var metadata = new AudioMetadata { Asin = "EXISTING123" };
            var tags = TagsFrom("{\"ASIN\":\"B0078PA1OA\"}");

            FfprobeTagMetadataMapper.Apply(metadata, tags);

            Assert.Equal("EXISTING123", metadata.Asin);
        }

        [Fact]
        public void Apply_SkipsWhitespaceOnlyAsinTag_AndFallsThroughToNextSpelling()
        {
            // A present-but-empty ASIN tag must not win: GetTag skips whitespace-only values and
            // falls through to the next spelling.
            var metadata = new AudioMetadata();
            var tags = TagsFrom("{\"ASIN\":\"   \",\"AUDIBLE_ASIN\":\"B0078PA1OA\"}");

            FfprobeTagMetadataMapper.Apply(metadata, tags);

            Assert.Equal("B0078PA1OA", metadata.Asin);
        }

        [Fact]
        public void Apply_IntraFileAsinDisagreement_TakesFirstSpellingInPrecedenceOrder()
        {
            // One file carrying two different ASIN spellings resolves to the first name in the
            // lookup order ("ASIN" before "AUDIBLE_ASIN"). The cross-file unanimity guard compares
            // across files, not within one, so intra-file precedence is what decides here.
            var metadata = new AudioMetadata();
            var tags = TagsFrom("{\"ASIN\":\"B000AAAAAA\",\"AUDIBLE_ASIN\":\"B111BBBBBB\"}");

            FfprobeTagMetadataMapper.Apply(metadata, tags);

            Assert.Equal("B000AAAAAA", metadata.Asin);
        }
    }
}
