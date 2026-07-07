/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using System.Globalization;
using System.Text.Json;
using Listenarr.Infrastructure.Ffmpeg.Metadata;

namespace Listenarr.Tests.Features.Infrastructure.Ffmpeg.Metadata
{
    public class FfprobeMetadataMapperTests
    {
        // #737 Bug A regression: ffprobe emits a dot-decimal duration ("108555.624989"). The old
        // culture-less double.TryParse failed on a comma-decimal machine and zeroed EVERY duration
        // library-wide. Force a comma-decimal culture so this test fails without the InvariantCulture fix
        // even on dot-decimal CI machines.
        [Fact]
        public void Map_ParsesDotDecimalDuration_UnderCommaDecimalCulture()
        {
            var original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // comma decimal separator

                using var doc = JsonDocument.Parse(
                    "{\"format\":{\"duration\":\"108555.624989\",\"format_name\":\"mov,mp4,m4a\",\"bit_rate\":\"64000\"}," +
                    "\"streams\":[{\"codec_type\":\"audio\",\"codec_name\":\"aac\",\"sample_rate\":\"44100\",\"channels\":2,\"bit_rate\":\"64000\"}]}");

                var meta = FfprobeMetadataMapper.Map(doc.RootElement, "Light Bringer.m4b");

                Assert.Equal(108555.624989, meta.Duration.TotalSeconds, 3);
                Assert.Equal("aac", meta.Codec);
                Assert.Equal(64000, meta.BitRate);
                Assert.Equal(44100, meta.SampleRate);
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }
    }
}
