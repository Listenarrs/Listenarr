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
