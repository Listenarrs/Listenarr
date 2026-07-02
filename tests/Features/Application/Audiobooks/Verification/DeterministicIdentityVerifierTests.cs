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
using Listenarr.Application.Audiobooks.Verification.Contracts;

using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Listenarr.Tests.Features.Application.Audiobooks.Verification
{
    public class DeterministicIdentityVerifierTests
    {
        private static DeterministicIdentityVerifier CreateVerifier()
        {
            var whisper = new Mock<IWhisperService>();
            whisper.SetupGet(w => w.ModelName).Returns("base.en");

            return new DeterministicIdentityVerifier(
                Mock.Of<IAudioSampleExtractor>(),
                whisper.Object,
                Mock.Of<IConfigurationService>(),
                NullLogger<DeterministicIdentityVerifier>.Instance);
        }

        private static Audiobook HailMary() => new()
        {
            Id = 1,
            Title = "Project Hail Mary",
            Authors = new List<string> { "Andy Weir" },
            Narrators = new List<string> { "Ray Porter" },
            Publisher = "Audible Studios"
        };

        [Fact]
        public void Evaluate_CleanCredits_IsMatch()
        {
            var verdict = CreateVerifier().Evaluate(
                HailMary(),
                "Audible presents Project Hail Mary by Andy Weir narrated by Ray Porter",
                closingText: null);

            Assert.Equal(VerificationOutcome.Match, verdict.Outcome);
            Assert.True(verdict.Confidence >= 0.7, $"confidence {verdict.Confidence}");
            Assert.NotNull(verdict.TitleMatch);
            Assert.NotNull(verdict.AuthorMatch);
            Assert.Equal("deterministic:whisper-base.en", verdict.Method);
        }

        [Fact]
        public void Evaluate_WrongContent_IsHighConfidenceMismatch()
        {
            // Music-scene release imported as an audiobook: plenty of speech,
            // zero trace of title or author — the gross mismatch this feature
            // exists to catch, and the HIGH-confidence signal per the ADR.
            var verdict = CreateVerifier().Evaluate(
                HailMary(),
                "thank you for listening to the midnight sessions volume four " +
                "all tracks composed and performed by the velvet underground revival band",
                closingText: null);

            Assert.Equal(VerificationOutcome.Mismatch, verdict.Outcome);
            Assert.True(verdict.Confidence >= 0.7, $"gross mismatch should be confident, got {verdict.Confidence}");
        }

        [Fact]
        public void Evaluate_PureNarrationWithoutCredits_IsNoSpokenCredits_NotMismatch()
        {
            // The top live false-flag: a book that opens with pure narration.
            // Plenty of clear speech, zero announcement-shaped phrases — the
            // audio simply never says what it is. That is NOT evidence of wrong
            // content and must not land in the flagged bucket.
            var verdict = CreateVerifier().Evaluate(
                HailMary(),
                "the rain had not stopped for three days and the river kept rising " +
                "past the old stone markers so she finally decided to leave the " +
                "cabin and walk down the mountain toward the village because " +
                "her sister still kept a lamp burning in the front room every night",
                closingText: null);

            Assert.Equal(VerificationOutcome.NoSpokenCredits, verdict.Outcome);
            Assert.True(verdict.Confidence <= 0.4, $"neutral outcome should not be confident, got {verdict.Confidence}");
        }

        [Fact]
        public void Evaluate_ContradictingSpokenCredits_IsStillMismatch()
        {
            // The audio ANNOUNCES a different book — structured credits that
            // contradict the stored metadata. This must remain the confident
            // mismatch the feature exists to catch.
            var verdict = CreateVerifier().Evaluate(
                HailMary(),
                "blackstone audio presents the quiet gardener by margaret holloway " +
                "narrated by susan fields this recording is copyrighted 2011 by blackstone audio",
                closingText: null);

            Assert.Equal(VerificationOutcome.Mismatch, verdict.Outcome);
            Assert.True(verdict.Confidence >= 0.7, $"contradicting credits should stay confident, got {verdict.Confidence}");
        }

        [Fact]
        public void StatusFor_NoSpokenCredits_MapsToAgentUnverifiable()
        {
            Assert.Equal(
                VerificationStatus.AgentUnverifiable,
                Listenarr.Infrastructure.HostedServices.Verification.LibraryVerificationBackgroundService.StatusFor(VerificationOutcome.NoSpokenCredits));
            Assert.True(VerificationStatus.AgentUnverifiable.IsAgentWritable());
        }

        [Fact]
        public void Evaluate_TooLittleSpeech_IsUncertainNeverMismatch()
        {
            // Music intro / silence yields a near-empty transcript: no signal
            // either way. Must land in Uncertain (low confidence), never Mismatch.
            var verdict = CreateVerifier().Evaluate(HailMary(), "la la music sounds", closingText: null);

            Assert.Equal(VerificationOutcome.Uncertain, verdict.Outcome);
            Assert.True(verdict.Confidence <= 0.3);
        }

        [Fact]
        public void Evaluate_MangledTitleButClearAuthor_IsMatch()
        {
            // STT garbles an unusual title word but nails the author name —
            // common on invented words; author corroboration carries it.
            var verdict = CreateVerifier().Evaluate(
                HailMary(),
                "project hale mary written by andy weir and narrated by ray porter for your enjoyment",
                closingText: null);

            Assert.Equal(VerificationOutcome.Match, verdict.Outcome);
        }

        [Fact]
        public void Evaluate_PartialSignal_IsUncertain()
        {
            // Title half-present, author absent: ambiguous, goes to human review.
            var verdict = CreateVerifier().Evaluate(
                HailMary(),
                "welcome to this recording of the mary chronicles read aloud for you by your favorite narrator today",
                closingText: null);

            Assert.Equal(VerificationOutcome.Uncertain, verdict.Outcome);
        }

        [Fact]
        public void Evaluate_MangledAuthorButExactTitleAndNarrator_IsMatch()
        {
            // Observed live on A Court of Thorns and Roses: title, narrator, and
            // publisher all exact but the author rendering imperfect — the
            // narrator corroboration path must accept this, not flag it.
            var book = new Audiobook
            {
                Id = 3,
                Title = "A Court of Thorns and Roses",
                Authors = new List<string> { "Sarah Q. Maazzz" }, // unmatchable rendering
                Narrators = new List<string> { "Jennifer Ikeda" }
            };

            var verdict = CreateVerifier().Evaluate(
                book,
                "a court of thorns and roses written by somebody unclear narrated by jennifer ikeda",
                closingText: null);

            Assert.Equal(VerificationOutcome.Match, verdict.Outcome);
        }

        [Fact]
        public void Evaluate_ClosingWindowCredits_CountTowardMatch()
        {
            var verdict = CreateVerifier().Evaluate(
                HailMary(),
                openingText: "previously on the unrelated publisher preview of some other novel entirely",
                closingText: "this has been Project Hail Mary by Andy Weir narrated by Ray Porter");

            Assert.Equal(VerificationOutcome.Match, verdict.Outcome);
            Assert.Contains("[closing]", verdict.Transcript);
        }

        [Fact]
        public void Evaluate_BookWithoutNarratorOrPublisher_LeavesFieldsNull()
        {
            var book = new Audiobook { Id = 2, Title = "The Martian", Authors = new List<string> { "Andy Weir" } };

            var verdict = CreateVerifier().Evaluate(
                book, "the martian by andy weir read by someone great today", closingText: null);

            Assert.Null(verdict.NarratorMatch);
            Assert.Null(verdict.PublisherMatch);
            Assert.Equal(VerificationOutcome.Match, verdict.Outcome);
        }
    }
}
