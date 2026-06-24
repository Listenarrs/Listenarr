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
using Listenarr.Tests.Builders;

namespace Listenarr.Tests.Features.Domain.Utils
{
    public class QualityMatcherTests
    {
        private static QualityProfileBuilder StructuredProfile() =>
            new QualityProfileBuilder().WithName("Structured").WithStructuredDefaults();

        // ---- round-down within a codec ----------------------------------------------------

        [Fact]
        public void Match_RoundsDownToHighestRungFileMeets()
        {
            var profile = StructuredProfile().Build();
            var file = new AudioQualityInput { Codec = "aac", BitrateBitsPerSecond = 224_000 };

            var result = QualityMatcher.Match(file, profile);

            Assert.True(result.IsMatch);
            Assert.Equal("AAC 192kbps", result.Rung!.Quality);
        }

        [Fact]
        public void Match_ReturnsExactRung_WhenBitrateMatchesExactly()
        {
            var profile = StructuredProfile().Build();
            var file = new AudioQualityInput { Codec = "aac", BitrateBitsPerSecond = 256_000 };

            Assert.Equal("AAC 256kbps", QualityMatcher.Match(file, profile).Rung!.Quality);
        }

        [Fact]
        public void Match_ClampsToTopRung_WhenAboveHighestBitrate()
        {
            var profile = StructuredProfile().Build();
            var file = new AudioQualityInput { Codec = "aac", BitrateBitsPerSecond = 400_000 };

            Assert.Equal("AAC 320kbps", QualityMatcher.Match(file, profile).Rung!.Quality);
        }

        [Fact]
        public void Match_FallsToWorstRung_WhenBelowLowestBitrate()
        {
            var profile = StructuredProfile().Build();
            var file = new AudioQualityInput { Codec = "aac", BitrateBitsPerSecond = 32_000 };

            Assert.Equal("AAC 64kbps", QualityMatcher.Match(file, profile).Rung!.Quality);
        }

        // ---- the M4B re-grab loop regression ----------------------------------------------

        [Fact]
        public void Match_M4bContainerAacFile_MatchesAacRung_NotMismatch()
        {
            // The original bug: container "M4B" was emitted as the quality label and never
            // equalled the "AAC 256kbps" rung, so the cutoff was never met -> re-grab forever.
            var profile = StructuredProfile().Build();
            var file = new AudioQualityInput { Codec = "aac", Container = "M4B", BitrateBitsPerSecond = 256_000 };

            var result = QualityMatcher.Match(file, profile);

            Assert.True(result.IsMatch);
            Assert.Equal("AAC 256kbps", result.Rung!.Quality);
        }

        [Fact]
        public void Match_LegacyM4bCodecGroup_Matches()
        {
            var profile = new QualityProfileBuilder()
                .WithName("Legacy")
                .WithQuality("M4B 256kbps", 0, codec: "M4B", bitrate: 256)
                .Build();
            var file = new AudioQualityInput { Codec = "aac", Container = "M4A", BitrateBitsPerSecond = 256_000 };

            Assert.True(QualityMatcher.Match(file, profile).IsMatch);
        }

        // ---- codec absent from profile ----------------------------------------------------

        [Fact]
        public void Match_CodecNotInProfile_ReturnsCodecMismatch()
        {
            var profile = new QualityProfileBuilder()
                .WithName("Mp3AacOnly")
                .WithQuality("MP3 320kbps", 0, codec: "MP3", bitrate: 320)
                .WithQuality("AAC 320kbps", 1, codec: "AAC", bitrate: 320)
                .Build();
            var file = new AudioQualityInput { Codec = "opus", BitrateBitsPerSecond = 192_000 };

            var result = QualityMatcher.Match(file, profile);

            Assert.False(result.IsMatch);
            Assert.Equal(QualityMatchKind.CodecMismatch, result.Kind);
        }

        // ---- lossless ---------------------------------------------------------------------

        [Fact]
        public void Match_FlacFile_MatchesFlacRung()
        {
            var profile = StructuredProfile().Build();
            var file = new AudioQualityInput { Codec = "flac", Container = "FLAC" };

            Assert.Equal("FLAC", QualityMatcher.Match(file, profile).Rung!.Quality);
        }

        [Fact]
        public void Match_FlacFile_WithOnlyPathExtension_MatchesFlacRung()
        {
            // No codec/container/format metadata (metadata processing off, ffprobe missing, or
            // extraction failed) — the path extension is the only quality signal. MapCodec already
            // uses it, so lossless-ness must be derived consistently or "book.flac" maps to the
            // FLAC group yet is treated as lossy and filtered off the FLAC rung.
            var profile = StructuredProfile().Build();
            var file = new AudioQualityInput { Path = "/audiobooks/book.flac" };

            var result = QualityMatcher.Match(file, profile);

            Assert.True(result.IsMatch);
            Assert.Equal("FLAC", result.Rung!.Quality);
        }

        [Fact]
        public void Match_LosslessFile_NoLosslessRung_ReturnsCodecMismatch()
        {
            var profile = new QualityProfileBuilder()
                .WithName("LossyOnly")
                .WithQuality("MP3 320kbps", 0, codec: "MP3", bitrate: 320)
                .WithQuality("AAC 320kbps", 1, codec: "AAC", bitrate: 320)
                .Build();
            var file = new AudioQualityInput { Codec = "flac", Container = "FLAC" };

            Assert.Equal(QualityMatchKind.CodecMismatch, QualityMatcher.Match(file, profile).Kind);
        }

        // ---- bitrate unit normalization ---------------------------------------------------

        [Fact]
        public void Match_NormalizesBitsPerSecond()
        {
            var profile = StructuredProfile().Build();
            var file = new AudioQualityInput { Codec = "aac", BitrateBitsPerSecond = 320_000 };

            Assert.Equal("AAC 320kbps", QualityMatcher.Match(file, profile).Rung!.Quality);
        }

        [Fact]
        public void Match_GuardsValuesAlreadyInKbps()
        {
            var profile = StructuredProfile().Build();
            var file = new AudioQualityInput { Codec = "aac", BitrateBitsPerSecond = 320 };

            Assert.Equal("AAC 320kbps", QualityMatcher.Match(file, profile).Rung!.Quality);
        }

        // ---- VBR and unknown bitrate ------------------------------------------------------

        [Fact]
        public void Match_UnknownBitrate_PrefersVbrRung()
        {
            var profile = StructuredProfile().Build();
            var file = new AudioQualityInput { Codec = "mp3", BitrateBitsPerSecond = null };

            Assert.Equal("MP3 VBR", QualityMatcher.Match(file, profile).Rung!.Quality);
        }

        [Fact]
        public void Match_UnknownBitrate_NoVbr_FallsToWorstRung()
        {
            var profile = new QualityProfileBuilder()
                .WithName("AacNoVbr")
                .WithQuality("AAC 320kbps", 0, codec: "AAC", bitrate: 320)
                .WithQuality("AAC 128kbps", 1, codec: "AAC", bitrate: 128)
                .Build();
            var file = new AudioQualityInput { Codec = "aac", BitrateBitsPerSecond = null };

            Assert.Equal("AAC 128kbps", QualityMatcher.Match(file, profile).Rung!.Quality);
        }

        // ---- OGG Vorbis by codec ----------------------------------------------------------

        [Fact]
        public void Match_OggVorbis_MatchesByCodec()
        {
            var profile = new QualityProfileBuilder()
                .WithName("Ogg")
                .WithQuality("OGG Vorbis 256kbps", 0, codec: "OGG Vorbis", bitrate: 256)
                .WithQuality("OGG Vorbis 192kbps", 1, codec: "OGG Vorbis", bitrate: 192)
                .Build();
            var file = new AudioQualityInput { Codec = "vorbis", Container = "OGG", BitrateBitsPerSecond = 192_000 };

            Assert.Equal("OGG Vorbis 192kbps", QualityMatcher.Match(file, profile).Rung!.Quality);
        }

        // ---- seed-style rungs (Codec null, parse from label) ------------------------------

        [Fact]
        public void Match_SeedStyleRung_ParsesLabelForCodecAndBitrate()
        {
            // Seed rungs only set Quality + Priority (no structured Codec/Bitrate).
            var profile = new QualityProfileBuilder()
                .WithName("Seed")
                .WithQuality("AAC 320kbps", 0)
                .WithQuality("AAC 256kbps", 1)
                .WithQuality("AAC 192kbps", 2)
                .Build();
            var file = new AudioQualityInput { Codec = "aac", BitrateBitsPerSecond = 256_000 };

            Assert.Equal("AAC 256kbps", QualityMatcher.Match(file, profile).Rung!.Quality);
        }

        // ---- bare-bitrate wildcard (AudiobookStatusEvaluator parity) ----------------------

        [Fact]
        public void Match_BareBitrateRung_ActsAsCodecWildcard()
        {
            var profile = new QualityProfileBuilder()
                .WithName("Bare")
                .WithQuality("320kbps", 0)
                .WithQuality("256kbps", 1)
                .WithQuality("192kbps", 2)
                .Build();
            var file = new AudioQualityInput { Format = "m4b", BitrateBitsPerSecond = 256_000 };

            Assert.Equal("256kbps", QualityMatcher.Match(file, profile).Rung!.Quality);
        }

        // ---- MeetsCutoff direction (lower priority = higher quality) ----------------------

        [Fact]
        public void MeetsCutoff_BelowCutoff_IsFalse()
        {
            var profile = StructuredProfile().WithCutoff("AAC 256kbps").Build();
            var file = new AudioQualityInput { Codec = "aac", BitrateBitsPerSecond = 192_000 };

            Assert.False(QualityMatcher.MeetsCutoff(file, profile));
        }

        [Fact]
        public void MeetsCutoff_AtCutoffBoundary_IsTrue()
        {
            var profile = StructuredProfile().WithCutoff("AAC 256kbps").Build();
            var file = new AudioQualityInput { Codec = "aac", BitrateBitsPerSecond = 256_000 };

            Assert.True(QualityMatcher.MeetsCutoff(file, profile));
        }

        [Fact]
        public void MeetsCutoff_ExceedsCutoff_IsTrue()
        {
            var profile = StructuredProfile().WithCutoff("AAC 256kbps").Build();
            var file = new AudioQualityInput { Codec = "aac", BitrateBitsPerSecond = 320_000 };

            Assert.True(QualityMatcher.MeetsCutoff(file, profile));
        }

        [Fact]
        public void MeetsCutoff_CodecMismatch_IsFalse()
        {
            var profile = new QualityProfileBuilder()
                .WithName("Mp3Only")
                .WithCutoff("MP3 256kbps")
                .WithQuality("MP3 320kbps", 0, codec: "MP3", bitrate: 320)
                .WithQuality("MP3 256kbps", 1, codec: "MP3", bitrate: 256)
                .Build();
            var file = new AudioQualityInput { Codec = "opus", BitrateBitsPerSecond = 320_000 };

            Assert.False(QualityMatcher.MeetsCutoff(file, profile));
        }

        [Fact]
        public void MeetsCutoff_NoCutoffConfigured_IsTrue()
        {
            var profile = StructuredProfile().Build(); // no cutoff set
            var file = new AudioQualityInput { Codec = "aac", BitrateBitsPerSecond = 64_000 };

            Assert.True(QualityMatcher.MeetsCutoff(file, profile));
        }

        [Fact]
        public void MeetsCutoff_CutoffRungMissing_IsFalse()
        {
            var profile = StructuredProfile().WithCutoff("Nonexistent 999kbps").Build();
            var file = new AudioQualityInput { Codec = "aac", BitrateBitsPerSecond = 320_000 };

            Assert.False(QualityMatcher.MeetsCutoff(file, profile));
        }

        // ---- LabelMeetsCutoff & IsLabelBetter & ProfileContainsHint -----------------------

        [Fact]
        public void LabelMeetsCutoff_HonoursPriorityDirection()
        {
            var profile = StructuredProfile().WithCutoff("AAC 256kbps").Build();

            Assert.True(QualityMatcher.LabelMeetsCutoff("AAC 320kbps", profile));
            Assert.True(QualityMatcher.LabelMeetsCutoff("AAC 256kbps", profile));
            Assert.False(QualityMatcher.LabelMeetsCutoff("AAC 192kbps", profile));
        }

        [Fact]
        public void IsLabelBetter_LowerPriorityNumberWins()
        {
            var profile = StructuredProfile().Build();

            Assert.True(QualityMatcher.IsLabelBetter("AAC 320kbps", "AAC 256kbps", profile));
            Assert.False(QualityMatcher.IsLabelBetter("AAC 192kbps", "AAC 256kbps", profile));
            Assert.False(QualityMatcher.IsLabelBetter("AAC 256kbps", "AAC 256kbps", profile));
        }

        [Fact]
        public void IsLabelBetter_HandlesUnknownInputs()
        {
            var profile = StructuredProfile().Build();

            Assert.False(QualityMatcher.IsLabelBetter(null, "AAC 256kbps", profile));
            Assert.True(QualityMatcher.IsLabelBetter("AAC 256kbps", null, profile));
            Assert.False(QualityMatcher.IsLabelBetter("Unknown", "AAC 256kbps", profile));
            Assert.True(QualityMatcher.IsLabelBetter("AAC 256kbps", "Unknown", profile));
        }

        [Fact]
        public void ProfileContainsHint_IsCaseInsensitive()
        {
            var profile = new QualityProfileBuilder()
                .WithName("Custom")
                .WithQuality("Audible AAX", 0)
                .Build();

            Assert.True(QualityMatcher.ProfileContainsHint(profile, "audible aax"));
            Assert.False(QualityMatcher.ProfileContainsHint(profile, "FLAC"));
        }
    }
}
