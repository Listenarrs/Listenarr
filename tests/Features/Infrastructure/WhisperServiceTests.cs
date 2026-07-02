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
using Listenarr.Infrastructure.Whisper;

namespace Listenarr.Tests.Features.Infrastructure
{
    public class WhisperServiceTests
    {
        [Fact]
        public void NormalizeTranscriptOutput_StripsSegmentTimestamps_AndJoinsSegments()
        {
            // Real whisper-cli timestamped output shape (the production decode runs
            // WITH timestamps because suppressing them makes base.en skip
            // music-overlaid segments such as the spoken credits).
            var stdout =
                "[00:00:00.000 --> 00:00:09.260]   \"This is Audible,\" A Court of Thorns and Roses,\n" +
                "[00:00:09.260 --> 00:00:17.160]   by Sarah J. Mass, narrated by Jennifer Eketa.\n" +
                "[00:00:17.160 --> 00:00:23.520]   Chapter 1 The forest had become a labyrinth of snow\n";

            var transcript = WhisperService.NormalizeTranscriptOutput(stdout);

            Assert.Equal(
                "\"This is Audible,\" A Court of Thorns and Roses, by Sarah J. Mass, narrated by Jennifer Eketa. Chapter 1 The forest had become a labyrinth of snow",
                transcript);
        }

        [Fact]
        public void NormalizeTranscriptOutput_PlainTextWithoutTimestamps_PassesThrough()
        {
            var transcript = WhisperService.NormalizeTranscriptOutput("  Plain text output.  \n");
            Assert.Equal("Plain text output.", transcript);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   \n  \n")]
        public void NormalizeTranscriptOutput_EmptyOutput_ReturnsNull(string? stdout)
        {
            Assert.Null(WhisperService.NormalizeTranscriptOutput(stdout));
        }

        [Fact]
        public void ParseSegments_ReadsTimestampsAndText()
        {
            var stdout =
                "[00:00:00.000 --> 00:00:02.000]   This is Audible.\n" +
                "[00:00:30.000 --> 00:00:35.500]   Daniel glanced over his shoulder.\n";

            var segments = WhisperService.ParseSegments(stdout);

            Assert.Equal(2, segments.Count);
            Assert.Equal(0.0, segments[0].Start);
            Assert.Equal(2.0, segments[0].End);
            Assert.Equal("This is Audible.", segments[0].Text);
            Assert.Equal(30.0, segments[1].Start);
            Assert.Equal(35.5, segments[1].End);
        }

        [Fact]
        public void FindSilentGaps_DetectsTheCreditsHole()
        {
            // The live Artifact Enigma shape: ident at 0–2s, then nothing until
            // the story resumes at the 30s chunk boundary — the 2–30s hole is
            // exactly where the music-overlaid credits live.
            var segments = new List<WhisperService.TranscriptSegment>
            {
                new(0.0, 2.0, "This is Audible."),
                new(30.0, 35.0, "Daniel glanced over his shoulder."),
                new(35.0, 41.0, "A tall leggy blonde gave him a lingering glance."),
            };

            var gaps = WhisperService.FindSilentGaps(segments, clipDurationSeconds: 90.0);

            Assert.Equal(2, gaps.Count);
            Assert.Equal((2.0, 30.0), gaps[0]);
            // Trailing hole (41–90s) also qualifies — closing-window credits sit
            // at the very end of a clip.
            Assert.Equal((41.0, 90.0), gaps[1]);
        }

        [Fact]
        public void FindSilentGaps_IgnoresOrdinaryNarrationPauses()
        {
            var segments = new List<WhisperService.TranscriptSegment>
            {
                new(0.0, 10.0, "a"),
                new(14.0, 25.0, "b"), // 4s pause: scene break, not a credits hole
                new(27.0, 60.0, "c"),
            };

            var gaps = WhisperService.FindSilentGaps(segments, clipDurationSeconds: 62.0);

            Assert.Empty(gaps);
        }
    }
}
