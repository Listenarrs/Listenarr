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
using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
{
    public class AudiobookStatusEvaluatorTests
    {
        [Fact]
        public void ComputeStatus_ReturnsDownloading_WhenIsDownloading()
        {
            var status = AudiobookStatusEvaluator.ComputeStatus(
                isDownloading: true,
                hasAnyFile: false,
                audiobookQuality: null,
                qualityProfile: null,
                files: null);

            Assert.Equal(AudiobookStatusEvaluator.Downloading, status);
        }

        [Fact]
        public void ComputeStatus_ReturnsNoFile_WhenHasNoFiles()
        {
            var status = AudiobookStatusEvaluator.ComputeStatus(
                isDownloading: false,
                hasAnyFile: false,
                audiobookQuality: null,
                qualityProfile: null,
                files: null);

            Assert.Equal(AudiobookStatusEvaluator.NoFile, status);
        }

        [Fact]
        public void ComputeStatus_ReturnsQualityMatch_WhenProfileIsNull()
        {
            var files = new List<AudiobookFileStatusInfo>
            {
                new() { Format = "mp3", Bitrate = 128000 }
            };

            var status = AudiobookStatusEvaluator.ComputeStatus(false, true, null, null, files);

            Assert.Equal(AudiobookStatusEvaluator.QualityMatch, status);
        }

        [Fact]
        public void ComputeStatus_ReturnsQualityMismatch_WhenNoFilesMatchPreferredFormats()
        {
            var profile = CreateDefaultProfile(cutoffQuality: "MP3 256kbps", preferredFormats: new List<string> { "m4b" });
            var files = new List<AudiobookFileStatusInfo>
            {
                new() { Format = "mp3", Bitrate = 320000 }
            };

            var status = AudiobookStatusEvaluator.ComputeStatus(false, true, null, profile, files);

            Assert.Equal(AudiobookStatusEvaluator.QualityMismatch, status);
        }

        [Fact]
        public void ComputeStatus_ReturnsQualityMatch_WhenDerivedQualityMeetsCutoffBoundary()
        {
            var profile = CreateDefaultProfile(cutoffQuality: "MP3 256kbps", preferredFormats: new List<string> { "mp3" });
            var files = new List<AudiobookFileStatusInfo>
            {
                new() { Format = "mp3", Codec = "mp3", Bitrate = 256000 }
            };

            var status = AudiobookStatusEvaluator.ComputeStatus(false, true, null, profile, files);

            Assert.Equal(AudiobookStatusEvaluator.QualityMatch, status);
        }

        [Fact]
        public void ComputeStatus_ReturnsQualityMismatch_WhenDerivedQualityIsBelowCutoff()
        {
            var profile = CreateDefaultProfile(cutoffQuality: "MP3 256kbps", preferredFormats: new List<string> { "mp3" });
            var files = new List<AudiobookFileStatusInfo>
            {
                new() { Format = "mp3", Codec = "mp3", Bitrate = 128000 }
            };

            var status = AudiobookStatusEvaluator.ComputeStatus(false, true, null, profile, files);

            Assert.Equal(AudiobookStatusEvaluator.QualityMismatch, status);
        }

        [Fact]
        public void ComputeStatus_ReturnsQualityMatch_WhenOnlyLegacyFileSummaryExists()
        {
            var profile = CreateDefaultProfile(cutoffQuality: "MP3 256kbps", preferredFormats: new List<string> { "m4b" });

            var status = AudiobookStatusEvaluator.ComputeStatus(false, true, null, profile, files: null);

            Assert.Equal(AudiobookStatusEvaluator.QualityMatch, status);
        }

        // Regression: an M4B file at the cutoff bitrate must satisfy the AAC rung — the
        // bug behind https://github.com/Listenarrs/Listenarr/issues/549 was that M4B
        // files derived to the raw label "m4b" (or "64kbps"), which never matched any
        // QualityProfile.Qualities key like "AAC 64kbps" and triggered an endless
        // ~6h re-grab loop for monitored audiobooks with matching indexer releases.
        [Fact]
        public void ComputeStatus_ReturnsQualityMatch_ForM4BFileMeetingAacCutoff()
        {
            var profile = CreateDefaultProfile(cutoffQuality: "AAC 64kbps", preferredFormats: new List<string> { "m4b" });
            var files = new List<AudiobookFileStatusInfo>
            {
                new() { Format = "m4b", Container = "m4b", Codec = "aac", Bitrate = 64000 }
            };

            var status = AudiobookStatusEvaluator.ComputeStatus(false, true, null, profile, files);

            Assert.Equal(AudiobookStatusEvaluator.QualityMatch, status);
        }

        [Fact]
        public void ComputeStatus_TreatsM4AContainerAsAac()
        {
            var profile = CreateDefaultProfile(cutoffQuality: "AAC 128kbps", preferredFormats: new List<string> { "m4a" });
            var files = new List<AudiobookFileStatusInfo>
            {
                new() { Format = "m4a", Container = "m4a", Codec = "mp4a", Bitrate = 128000 }
            };

            var status = AudiobookStatusEvaluator.ComputeStatus(false, true, null, profile, files);

            Assert.Equal(AudiobookStatusEvaluator.QualityMatch, status);
        }

        [Fact]
        public void ComputeStatus_BucketsBitrateDownToNearestRung()
        {
            // 200kbps lives between the 192 and 256 rungs and must bucket DOWN to 192;
            // otherwise a sub-cutoff file would be treated as if it met the cutoff.
            var profile = CreateDefaultProfile(cutoffQuality: "MP3 256kbps", preferredFormats: new List<string> { "mp3" });
            var files = new List<AudiobookFileStatusInfo>
            {
                new() { Format = "mp3", Codec = "mp3", Bitrate = 200000 }
            };

            var status = AudiobookStatusEvaluator.ComputeStatus(false, true, null, profile, files);

            Assert.Equal(AudiobookStatusEvaluator.QualityMismatch, status);
        }

        [Fact]
        public void ComputeStatus_AcceptsBitrateBelowLowestRungAsLowestRung()
        {
            // 32kbps is below the lowest seeded rung (64). It must still resolve to a
            // known rung so that we don't trigger a perpetual re-grab loop for unusually
            // low-bitrate sources whose cutoff happens to be the lowest rung.
            var profile = CreateDefaultProfile(cutoffQuality: "MP3 64kbps", preferredFormats: new List<string> { "mp3" });
            var files = new List<AudiobookFileStatusInfo>
            {
                new() { Format = "mp3", Codec = "mp3", Bitrate = 32000 }
            };

            var status = AudiobookStatusEvaluator.ComputeStatus(false, true, null, profile, files);

            Assert.Equal(AudiobookStatusEvaluator.QualityMatch, status);
        }

        [Fact]
        public void ComputeStatus_UsesAudiobookQualityHintWhenCodecPrefixed()
        {
            var profile = CreateDefaultProfile(cutoffQuality: "MP3 256kbps", preferredFormats: new List<string> { "mp3" });
            var files = new List<AudiobookFileStatusInfo>
            {
                // File metadata says 64kbps, but the legacy hint says "MP3 320kbps".
                // The codec-prefixed hint must be trusted as-is, overriding the bitrate.
                new() { Format = "mp3", Codec = "mp3", Bitrate = 64000 }
            };

            var status = AudiobookStatusEvaluator.ComputeStatus(false, true, "MP3 320kbps", profile, files);

            Assert.Equal(AudiobookStatusEvaluator.QualityMatch, status);
        }

        [Fact]
        public void ComputeStatus_IgnoresLegacyM4BHintAndRederivesFromFile()
        {
            // The legacy LibraryController hint returns "M4B" for m4b/m4a containers.
            // "M4B" is not a production QP key, so it must NOT short-circuit the
            // file-based derivation — otherwise the cutoff is never met (issue #549).
            var profile = CreateDefaultProfile(cutoffQuality: "AAC 64kbps", preferredFormats: new List<string> { "m4b" });
            var files = new List<AudiobookFileStatusInfo>
            {
                new() { Format = "m4b", Container = "m4b", Codec = "aac", Bitrate = 64000 }
            };

            var status = AudiobookStatusEvaluator.ComputeStatus(false, true, "M4B", profile, files);

            Assert.Equal(AudiobookStatusEvaluator.QualityMatch, status);
        }

        [Fact]
        public void ComputeStatus_TreatsFlacAsLossless()
        {
            var profile = CreateLosslessProfile();
            var files = new List<AudiobookFileStatusInfo>
            {
                new() { Format = "flac", Container = "flac", Codec = "flac" }
            };

            var status = AudiobookStatusEvaluator.ComputeStatus(false, true, null, profile, files);

            Assert.Equal(AudiobookStatusEvaluator.QualityMatch, status);
        }

        [Fact]
        public void ComputeStatus_TreatsAlacAsLossless()
        {
            var profile = CreateLosslessProfile();
            var files = new List<AudiobookFileStatusInfo>
            {
                new() { Format = "m4a", Container = "m4a", Codec = "alac" }
            };

            var status = AudiobookStatusEvaluator.ComputeStatus(false, true, null, profile, files);

            Assert.Equal(AudiobookStatusEvaluator.QualityMatch, status);
        }

        [Fact]
        public void ComputeStatus_TreatsWavPackAsLossless()
        {
            var profile = CreateLosslessProfile();
            var files = new List<AudiobookFileStatusInfo>
            {
                new() { Format = "wv", Container = "wv" }
            };

            var status = AudiobookStatusEvaluator.ComputeStatus(false, true, null, profile, files);

            Assert.Equal(AudiobookStatusEvaluator.QualityMatch, status);
        }

        // Mirrors the production seed in QualityProfileService.EnsureProfileHasRequiredQualitiesAsync.
        // Keeping this in sync is what guarantees DeriveQualityLabel output round-trips
        // with the default QP.
        private static QualityProfile CreateDefaultProfile(string cutoffQuality, List<string> preferredFormats)
        {
            return new QualityProfile
            {
                Name = "Default",
                CutoffQuality = cutoffQuality,
                PreferredFormats = preferredFormats,
                Qualities = new List<QualityDefinition>
                {
                    new() { Quality = "AAC 320kbps", Priority = 0, Allowed = true },
                    new() { Quality = "AAC 256kbps", Priority = 1, Allowed = true },
                    new() { Quality = "AAC 192kbps", Priority = 2, Allowed = true },
                    new() { Quality = "AAC 128kbps", Priority = 3, Allowed = true },
                    new() { Quality = "AAC 64kbps",  Priority = 4, Allowed = true },
                    new() { Quality = "MP3 320kbps", Priority = 5, Allowed = true },
                    new() { Quality = "MP3 256kbps", Priority = 6, Allowed = true },
                    new() { Quality = "MP3 VBR",     Priority = 7, Allowed = true },
                    new() { Quality = "MP3 192kbps", Priority = 8, Allowed = true },
                    new() { Quality = "MP3 128kbps", Priority = 9, Allowed = true },
                    new() { Quality = "MP3 64kbps",  Priority = 10, Allowed = true }
                }
            };
        }

        private static QualityProfile CreateLosslessProfile()
        {
            return new QualityProfile
            {
                Name = "Lossless Profile",
                CutoffQuality = "lossless",
                PreferredFormats = new List<string> { "flac", "m4a", "wv" },
                Qualities = new List<QualityDefinition>
                {
                    new() { Quality = "lossless", Priority = 0, Allowed = true }
                }
            };
        }
    }
}
