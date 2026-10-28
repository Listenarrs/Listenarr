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

namespace Listenarr.Tests.Features.Application.Audiobooks.Matching
{
    public class AudiobookStatusEvaluatorTests
    {
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
        public void ComputeStatus_ReturnsQualityMismatch_WhenNoFilesMatchPreferredFormats()
        {
            var profile = CreateProfile(cutoffQuality: "256kbps", preferredFormats: new List<string> { "m4b" });
            var files = new List<AudiobookFormatSummary>
            {
                new() { Format = "mp3", Bitrate = 320000 }
            };

            var status = AudiobookStatusEvaluator.ComputeStatus(false, true, null, profile, files);

            Assert.Equal(AudiobookStatusEvaluator.QualityMismatch, status);
        }

        [Fact]
        public void ComputeStatus_ReturnsQualityMatch_WhenDerivedQualityMeetsCutoffBoundary()
        {
            var profile = CreateProfile(cutoffQuality: "256kbps", preferredFormats: new List<string> { "m4b" });
            var files = new List<AudiobookFormatSummary>
            {
                new() { Format = "m4b", Bitrate = 256000 }
            };

            var status = AudiobookStatusEvaluator.ComputeStatus(false, true, null, profile, files);

            Assert.Equal(AudiobookStatusEvaluator.QualityMatch, status);
        }

        [Fact]
        public void ComputeStatus_ReturnsQualityMismatch_WhenDerivedQualityIsBelowCutoff()
        {
            var profile = CreateProfile(cutoffQuality: "256kbps", preferredFormats: new List<string> { "m4b" });
            var files = new List<AudiobookFormatSummary>
            {
                new() { Format = "m4b", Bitrate = 192000 }
            };

            var status = AudiobookStatusEvaluator.ComputeStatus(false, true, null, profile, files);

            Assert.Equal(AudiobookStatusEvaluator.QualityMismatch, status);
        }

        [Fact]
        public void ComputeStatus_ReturnsQualityMatch_WhenOnlyLegacyFileSummaryExists()
        {
            var profile = CreateProfile(cutoffQuality: "256kbps", preferredFormats: new List<string> { "m4b" });

            var status = AudiobookStatusEvaluator.ComputeStatus(false, true, null, profile, files: null);

            Assert.Equal(AudiobookStatusEvaluator.QualityMatch, status);
        }

        [Fact]
        public void ComputeStatus_TreatsWavPackAsLossless()
        {
            var profile = new QualityProfile
            {
                Name = "Lossless Profile",
                CutoffQuality = "lossless",
                PreferredFormats = new List<string> { "wv" },
                Qualities = new List<QualityDefinition>
                {
                    new() { Quality = "lossless", Priority = 0 }
                }
            };
            var files = new List<AudiobookFormatSummary>
            {
                new() { Format = "wv", Container = "wv" }
            };

            var status = AudiobookStatusEvaluator.ComputeStatus(false, true, null, profile, files);

            Assert.Equal(AudiobookStatusEvaluator.QualityMatch, status);
        }

        [Fact]
        public void ComputeStatus_ReturnsQualityMatch_ForPathOnlyFile_WhenProbeMetadataMissing()
        {
            // Regression: when metadata processing is disabled / ffprobe is unavailable, the file
            // summary carries only a Path. The status evaluator must forward that Path to the
            // QualityMatcher (as AudiobookQualityCutoffEvaluator does) so a "book.flac" maps to the
            // FLAC lossless rung. Previously Path was dropped, so this resolved as quality-mismatch
            // and disagreed with the automatic-search cutoff.
            // PreferredFormats = ["flac"] exercises the candidate filter too: a metadata-less file
            // must match the preferred format via its path extension, not be dropped before the matcher.
            var profile = new QualityProfile
            {
                Name = "Lossless Profile",
                CutoffQuality = "lossless",
                PreferredFormats = new List<string> { "flac" },
                Qualities = new List<QualityDefinition>
                {
                    new() { Quality = "lossless", Priority = 0 }
                }
            };
            var files = new List<AudiobookFormatSummary>
            {
                new() { Path = "/audiobooks/Author/Title/book.flac" }
            };

            var status = AudiobookStatusEvaluator.ComputeStatus(false, true, null, profile, files);

            Assert.Equal(AudiobookStatusEvaluator.QualityMatch, status);
        }

        [Fact]
        public void ComputeStatus_ReturnsQualityMatch_ForPathOnlyLossyFile_WhenPreferredFormatMatchesExtension()
        {
            // Generality beyond FLAC: the path-extension fallback is format-agnostic. A metadata-less
            // book.m4b with PreferredFormats = ["m4b"] must pass the candidate filter via its extension
            // (AAC group) and resolve through the matcher, not be dropped as quality-mismatch.
            var profile = new QualityProfile
            {
                Name = "AAC Profile",
                CutoffQuality = "AAC 256kbps",
                PreferredFormats = new List<string> { "m4b" },
                Qualities = new List<QualityDefinition>
                {
                    new() { Quality = "AAC 256kbps", Codec = "AAC", Bitrate = 256, Priority = 0 }
                }
            };
            var files = new List<AudiobookFormatSummary>
            {
                new() { Path = "/audiobooks/Author/Title/book.m4b" }
            };

            var status = AudiobookStatusEvaluator.ComputeStatus(false, true, null, profile, files);

            Assert.Equal(AudiobookStatusEvaluator.QualityMatch, status);
        }

        private static QualityProfile CreateProfile(string cutoffQuality, List<string> preferredFormats)
        {
            return new QualityProfile
            {
                Name = "Test Profile",
                CutoffQuality = cutoffQuality,
                PreferredFormats = preferredFormats,
                Qualities = new List<QualityDefinition>
                {
                    new() { Quality = "320kbps", Priority = 0 },
                    new() { Quality = "256kbps", Priority = 1 },
                    new() { Quality = "192kbps", Priority = 2 }
                }
            };
        }
    }
}
