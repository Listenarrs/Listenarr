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
 */
using System.Text.Json;
using Listenarr.Infrastructure.Ffmpeg.Metadata;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Ffmpeg
{
    [Trait("Area", "Metadata")]
    [Trait("Name", nameof(FfprobeTagMetadataMapperTests))]
    [Trait("Category", "Infrastructure")]
    public class FfprobeTagMetadataMapperTests : BaseTests
    {
        [Fact]
        public void Apply_ComposerTag_IsMappedToNarrator()
        {
            // Audiobook tagging convention repurposes MP4/ID3 "composer" for the narrator,
            // since neither format has a dedicated narrator field. Verified live against a
            // real downloaded file ("Great Expectations.m4b": composer=Stephen Fry).
            var metadata = new AudioMetadata();
            var tags = JsonDocument.Parse("""{"composer": "Stephen Fry"}""").RootElement;

            FfprobeTagMetadataMapper.Apply(metadata, tags);

            Assert.Equal("Stephen Fry", metadata.Narrator);
        }

        [Fact]
        public void Apply_LiteralNarratorTag_TakesPrecedenceOverComposer()
        {
            var metadata = new AudioMetadata();
            var tags = JsonDocument.Parse(
                """{"narrator": "R.C. Bray", "composer": "Someone Else"}""").RootElement;

            FfprobeTagMetadataMapper.Apply(metadata, tags);

            Assert.Equal("R.C. Bray", metadata.Narrator);
        }

        [Fact]
        public void Apply_NoNarratorOrComposerTag_LeavesNarratorUnset()
        {
            var metadata = new AudioMetadata();
            var tags = JsonDocument.Parse("""{"title": "Some Book"}""").RootElement;

            FfprobeTagMetadataMapper.Apply(metadata, tags);

            Assert.True(string.IsNullOrEmpty(metadata.Narrator));
        }

        [Fact]
        public void Apply_ExistingNarratorValue_IsNotOverwritten()
        {
            var metadata = new AudioMetadata { Narrator = "Already Set" };
            var tags = JsonDocument.Parse("""{"composer": "Different Narrator"}""").RootElement;

            FfprobeTagMetadataMapper.Apply(metadata, tags);

            Assert.Equal("Already Set", metadata.Narrator);
        }
    }
}
