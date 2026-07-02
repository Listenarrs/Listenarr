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
using Listenarr.Application.Audiobooks.Verification;

namespace Listenarr.Tests.Features.Application.Audiobooks.Verification
{
    public class AudioCompletenessEstimatorTests
    {
        [Fact]
        public void Estimate_PartialSet_ReportsLowCoverage()
        {
            // The live Ascendant shape: parts 10 and 20 of a ~20-part set, 42min
            // of a 17.4h book. One file's duration probe failed (0.0) — its
            // length is extrapolated from byte size at the sibling's bitrate.
            var book = new Audiobook
            {
                Runtime = 1043, // minutes
                Files = new List<AudiobookFile>
                {
                    new() { Path = "/a/Ascendant-10.mp3", DurationSeconds = 0.0, Size = 29_609_243 },
                    new() { Path = "/a/Ascendant-20.mp3", DurationSeconds = 2528.3, Size = 20_226_457 },
                }
            };

            var completeness = AudioCompletenessEstimator.Estimate(book);

            Assert.NotNull(completeness);
            Assert.Equal(1043, completeness!.ExpectedMinutes);
            // 2528s known + ~3701s extrapolated ≈ 104 minutes ≈ 10% coverage.
            Assert.InRange(completeness.ActualMinutes, 95, 115);
            Assert.True(completeness.Coverage < DeterministicIdentityVerifier.IncompleteCoverageThreshold);
        }

        [Fact]
        public void Estimate_CompleteBook_ReportsFullCoverage()
        {
            var book = new Audiobook
            {
                Runtime = 60,
                Files = new List<AudiobookFile>
                {
                    new() { Path = "/a/part1.mp3", DurationSeconds = 1800, Size = 30_000_000 },
                    new() { Path = "/a/part2.mp3", DurationSeconds = 1790, Size = 29_000_000 },
                }
            };

            var completeness = AudioCompletenessEstimator.Estimate(book);

            Assert.NotNull(completeness);
            Assert.InRange(completeness!.Coverage, 0.95, 1.05);
        }

        [Fact]
        public void Estimate_NullSizeOnKnownDurationFile_DoesNotPoisonTheBitrateAnchor()
        {
            // Live regression ("363h of 12h expected, 3027%"): a file with a
            // known duration but NULL size added seconds without bytes to the
            // bitrate anchor, shrinking bytes-per-second and inflating every
            // size-extrapolated file. Anchor must be computed pairwise.
            var book = new Audiobook
            {
                Runtime = 120, // 2h
                Files = new List<AudiobookFile>
                {
                    // 1h, size unknown — must contribute to the total but NOT the anchor.
                    new() { Path = "/a/p1.mp3", DurationSeconds = 3600, Size = null },
                    // 30min at ~10 KB/s — the only valid anchor pair.
                    new() { Path = "/a/p2.mp3", DurationSeconds = 1800, Size = 18_000_000 },
                    // Unknown duration, same size as p2 → should extrapolate to ~30min.
                    new() { Path = "/a/p3.mp3", DurationSeconds = null, Size = 18_000_000 },
                }
            };

            var completeness = AudioCompletenessEstimator.Estimate(book);

            Assert.NotNull(completeness);
            // 60 + 30 + ~30 ≈ 120 minutes ≈ full coverage — NOT a wild inflation.
            Assert.InRange(completeness!.ActualMinutes, 110, 130);
            Assert.InRange(completeness.Coverage, 0.9, 1.1);
        }

        [Fact]
        public void Estimate_NoCatalogRuntime_ReturnsNull()
        {
            var book = new Audiobook
            {
                Runtime = null,
                Files = new List<AudiobookFile> { new() { Path = "/a/x.mp3", DurationSeconds = 100, Size = 1000 } }
            };

            Assert.Null(AudioCompletenessEstimator.Estimate(book));
        }

        [Fact]
        public void Estimate_NoUsableDurations_ReturnsNull()
        {
            // Every duration probe failed: no bitrate anchor to extrapolate from,
            // so no claim is made rather than a fabricated one.
            var book = new Audiobook
            {
                Runtime = 600,
                Files = new List<AudiobookFile>
                {
                    new() { Path = "/a/x.mp3", DurationSeconds = 0.0, Size = 1_000_000 },
                    new() { Path = "/a/y.mp3", DurationSeconds = null, Size = 2_000_000 },
                }
            };

            Assert.Null(AudioCompletenessEstimator.Estimate(book));
        }

        [Theory]
        [InlineData(VerificationOutcome.Mismatch)]
        [InlineData(VerificationOutcome.Match)]
        public void ApplyCompleteness_LowCoverage_DowngradesToUncertain(VerificationOutcome original)
        {
            var verdict = new VerificationVerdict
            {
                Outcome = original,
                Confidence = 0.95,
                Method = "deterministic:test"
            };
            var completeness = new VerificationCompleteness(1043, 104, 0.1);

            var adjusted = DeterministicIdentityVerifier.ApplyCompleteness(verdict, completeness);

            Assert.Equal(VerificationOutcome.Uncertain, adjusted.Outcome);
            Assert.Same(completeness, adjusted.Completeness);
        }

        [Theory]
        [InlineData(VerificationOutcome.Mismatch)]
        [InlineData(VerificationOutcome.Match)]
        public void ApplyCompleteness_OverCoverage_DowngradesToUncertain(VerificationOutcome original)
        {
            // The collection fingerprint: ~300h of audio on a "12h" record.
            // Matching credits on the first file can't vouch for the other 288h.
            var verdict = new VerificationVerdict
            {
                Outcome = original,
                Confidence = 0.9,
                Method = "deterministic:test"
            };
            var completeness = new VerificationCompleteness(720, 21793, 30.27);

            var adjusted = DeterministicIdentityVerifier.ApplyCompleteness(verdict, completeness);

            Assert.Equal(VerificationOutcome.Uncertain, adjusted.Outcome);
        }

        [Fact]
        public void ApplyCompleteness_FullCoverage_LeavesOutcomeAlone()
        {
            var verdict = new VerificationVerdict
            {
                Outcome = VerificationOutcome.Match,
                Confidence = 0.9,
                Method = "deterministic:test"
            };
            var completeness = new VerificationCompleteness(600, 590, 0.983);

            var adjusted = DeterministicIdentityVerifier.ApplyCompleteness(verdict, completeness);

            Assert.Equal(VerificationOutcome.Match, adjusted.Outcome);
            Assert.NotNull(adjusted.Completeness);
        }

        [Fact]
        public void ApplyCompleteness_NullEstimate_IsANoOp()
        {
            var verdict = new VerificationVerdict
            {
                Outcome = VerificationOutcome.Mismatch,
                Confidence = 0.95,
                Method = "deterministic:test"
            };

            var adjusted = DeterministicIdentityVerifier.ApplyCompleteness(verdict, null);

            Assert.Equal(VerificationOutcome.Mismatch, adjusted.Outcome);
            Assert.Null(adjusted.Completeness);
        }
    }
}
