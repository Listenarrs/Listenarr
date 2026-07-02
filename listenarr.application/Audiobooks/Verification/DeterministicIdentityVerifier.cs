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
using Listenarr.Application.Audiobooks.Verification.Contracts;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Verification
{
    /// <summary>
    /// ADR-0001 Tier 1: deterministic, fully-offline identity verification.
    /// Clips the book's opening/closing windows, transcribes them with local
    /// whisper.cpp, and fuzzy-matches the transcript against stored metadata.
    ///
    /// The HIGH-confidence signal is gross-mismatch detection — a transcript
    /// whose spoken credits name a DIFFERENT book is obvious junk. A transcript
    /// with plenty of speech but no credit-shaped claims at all lands in
    /// NoSpokenCredits instead: absence of credits is not evidence of wrong
    /// content (many books open with pure narration). Match-confirmation is the
    /// lower-confidence path, and everything ambiguous (music intro, foreign
    /// language) lands in Uncertain for human review rather than being guessed at.
    /// </summary>
    public class DeterministicIdentityVerifier : IIdentityVerifier
    {
        // Field weights, by how reliably STT renders each field. Renormalized
        // over the fields the book actually has (a missing narrator must not
        // drag the aggregate down). Initial values — tuned later against the
        // labeled human-vs-agent data Phase 1 produces (ADR open question).
        private const double TitleWeight = 0.35;
        private const double AuthorWeight = 0.40;
        private const double NarratorWeight = 0.20;
        private const double PublisherWeight = 0.05;

        private const double TitleMatchThreshold = 0.75;
        private const double AuthorMatchThreshold = 0.65;
        // Kept strictly below the noise a partial echo produces: one of three
        // title tokens present scores 0.333, which must stay Uncertain, not
        // become a confident Mismatch.
        private const double GrossMismatchCeiling = 0.30;

        private readonly IAudioSampleExtractor _extractor;
        private readonly IWhisperService _whisper;
        private readonly IConfigurationService _configurationService;
        private readonly ILogger<DeterministicIdentityVerifier> _logger;

        public DeterministicIdentityVerifier(
            IAudioSampleExtractor extractor,
            IWhisperService whisper,
            IConfigurationService configurationService,
            ILogger<DeterministicIdentityVerifier> logger)
        {
            _extractor = extractor;
            _whisper = whisper;
            _configurationService = configurationService;
            _logger = logger;
        }

        public async Task<VerificationVerdict> VerifyAsync(Audiobook audiobook, string audioFilePath, CancellationToken cancellationToken = default)
        {
            var settings = await _configurationService.GetApplicationSettingsAsync();
            var strategy = new AudioSampleStrategy(
                OpeningSeconds: Math.Clamp(settings?.VerificationOpeningSeconds ?? 90, 0, 600),
                ClosingSeconds: Math.Clamp(settings?.VerificationClosingSeconds ?? 30, 0, 600));

            var (_, lastFile) = VerificationFileSelection.SelectFirstAndLast(audiobook);

            await using var samples = await _extractor.ExtractAsync(audioFilePath, lastFile, strategy, cancellationToken);
            if (samples.IsEmpty)
            {
                return Inconclusive("clip extraction produced no audio windows");
            }

            // The opening window almost always carries the credits; only spend
            // CPU on the closing window when the opening alone doesn't already
            // produce a confident match.
            string? openingText = null, closingText = null;
            if (samples.OpeningClipPath != null)
            {
                openingText = await _whisper.TranscribeAsync(samples.OpeningClipPath, cancellationToken);
            }

            var verdict = Evaluate(audiobook, openingText, closingText: null);
            if (verdict.Outcome != VerificationOutcome.Match && samples.ClosingClipPath != null)
            {
                closingText = await _whisper.TranscribeAsync(samples.ClosingClipPath, cancellationToken);
                if (!string.IsNullOrWhiteSpace(closingText))
                {
                    verdict = Evaluate(audiobook, openingText, closingText);
                }
            }

            if (openingText == null && closingText == null)
            {
                return Inconclusive("transcription failed for every sampled window");
            }

            // Independently of the match verdict, extract what the credits CLAIM
            // the book is — on a flagged book this seeds the "find the correct
            // match" relabel flow with the actual title/author the audio names.
            verdict = verdict with { HeardCredits = SpokenCreditsExtractor.Extract(openingText ?? closingText) };

            // Completeness: a partial set (e.g. parts 10+20 of 20) samples a
            // mid-book cold open and would read as a confident wrong-content
            // mismatch; the runtime comparison reframes it as "right book,
            // most of it missing" — still flagged, but honestly diagnosed.
            verdict = ApplyCompleteness(verdict, AudioCompletenessEstimator.Estimate(audiobook));

            _logger.LogInformation(
                "Verified audiobook {Id}: {Outcome} (confidence {Confidence:0.00}, title {Title:0.00}, author {Author:0.00})",
                audiobook.Id, verdict.Outcome, verdict.Confidence, verdict.TitleMatch?.Score ?? -1, verdict.AuthorMatch?.Score ?? -1);

            return verdict;
        }

        // Below this fraction of the catalog runtime the content set is too
        // partial to support a confident identity verdict in either direction.
        public const double IncompleteCoverageThreshold = 0.7;

        // Above this fraction the content is MORE than the book — the signature
        // of a collection/mega-pack import (live case: ~300h of audio on a "12h"
        // record) or a longer different book entirely.
        public const double OverCoverageThreshold = 1.5;

        /// <summary>
        /// Attaches the completeness estimate and, when coverage falls outside
        /// the plausible band, downgrades a confident outcome to Uncertain: a
        /// mid-book cold open from a partial set can't prove wrong content,
        /// matching credits on one surviving part can't vouch for a mostly
        /// absent book — and matching credits on the first file of a collection
        /// can't vouch for the other 200 hours. Public + pure so the rule is
        /// directly unit-testable.
        /// </summary>
        public static VerificationVerdict ApplyCompleteness(VerificationVerdict verdict, VerificationCompleteness? completeness)
        {
            if (completeness == null) return verdict;

            var adjusted = verdict with { Completeness = completeness };
            var implausible = completeness.Coverage < IncompleteCoverageThreshold
                || completeness.Coverage > OverCoverageThreshold;
            // NoSpokenCredits downgrades too: silent credits + an implausible
            // total runtime is exactly the case a human should look at.
            if (implausible && adjusted.Outcome != VerificationOutcome.Uncertain)
            {
                adjusted = adjusted with { Outcome = VerificationOutcome.Uncertain };
            }
            return adjusted;
        }

        private string Method => $"deterministic:whisper-{_whisper.ModelName}";

        private VerificationVerdict Inconclusive(string reason) => new()
        {
            Outcome = VerificationOutcome.Uncertain,
            Confidence = 0.1,
            Method = Method,
            Transcript = $"[inconclusive: {reason}]"
        };

        /// <summary>
        /// Pure aggregation of per-field matches into a verdict — internal so the
        /// threshold rules are unit-testable without audio or STT.
        /// </summary>
        internal VerificationVerdict Evaluate(Audiobook audiobook, string? openingText, string? closingText)
        {
            var transcript = BuildTranscript(openingText, closingText);
            var tokens = TranscriptMatcher.Tokenize(transcript);

            var title = TranscriptMatcher.MatchTitle(tokens, audiobook.Title);
            var author = TranscriptMatcher.MatchNames(tokens, audiobook.Authors);
            var narrator = TranscriptMatcher.MatchNames(tokens, audiobook.Narrators);
            var publisher = TranscriptMatcher.MatchPhrase(tokens, audiobook.Publisher);

            VerificationOutcome outcome;
            double confidence;

            var weighted = WeightedScore(title, author, narrator, publisher);

            if (tokens.Count < TranscriptMatcher.MinUsableTranscriptTokens)
            {
                // Too little speech to carry signal either way (music intro,
                // silence). Never call this a mismatch.
                outcome = VerificationOutcome.Uncertain;
                confidence = 0.15;
            }
            else if (title != null && author != null &&
                     title.Score < GrossMismatchCeiling && author.Score < GrossMismatchCeiling)
            {
                // Plenty of speech, no trace of title OR author. Before calling
                // that a confident mismatch, ask whether the audio ANNOUNCES
                // anything at all: absence of credits is not evidence of wrong
                // content — many books open with pure narration, and this was
                // the top false-flag in live use. A confident Mismatch requires
                // credit-shaped claims that CONTRADICT the metadata.
                var heard = SpokenCreditsExtractor.Extract(transcript);
                var announcesSomething = heard?.Title != null || heard?.Author != null
                    || SpokenCreditsExtractor.ContainsCreditMarkers(transcript);
                if (!announcesSomething)
                {
                    outcome = VerificationOutcome.NoSpokenCredits;
                    confidence = 0.3;
                }
                else
                {
                    outcome = VerificationOutcome.Mismatch;
                    var worst = Math.Max(title.Score, author.Score);
                    confidence = Math.Round(Math.Min(0.95, 0.70 + (GrossMismatchCeiling - worst)), 3);
                }
            }
            else if ((title?.Score ?? 0) >= TitleMatchThreshold && (author == null || author.Score >= AuthorMatchThreshold))
            {
                // Title clearly present and the author corroborates (or the book
                // has no stored author to check). Deliberately capped below the
                // mismatch path: match-confirmation is the weaker inference.
                outcome = VerificationOutcome.Match;
                confidence = Math.Round(Math.Min(0.90, weighted), 3);
            }
            else if ((author?.Score ?? 0) >= 0.85 && (title?.Score ?? 0) >= 0.55)
            {
                // Author heard loud and clear with a passable title rendering —
                // common when STT garbles an unusual title word but nails the name.
                outcome = VerificationOutcome.Match;
                confidence = Math.Round(Math.Min(0.85, weighted), 3);
            }
            else if ((title?.Score ?? 0) >= TitleMatchThreshold && (narrator?.Score ?? 0) >= 0.85)
            {
                // Clear title + clearly-heard narrator: the narrator is read from
                // the same spoken credits, so it corroborates identity as well as
                // the author does when STT mangles the author's name.
                outcome = VerificationOutcome.Match;
                confidence = Math.Round(Math.Min(0.85, weighted), 3);
            }
            else
            {
                outcome = VerificationOutcome.Uncertain;
                confidence = Math.Round(Math.Clamp(weighted, 0.15, 0.60), 3);
            }

            return new VerificationVerdict
            {
                Outcome = outcome,
                Confidence = confidence,
                Method = Method,
                TitleMatch = title,
                AuthorMatch = author,
                NarratorMatch = narrator,
                PublisherMatch = publisher,
                Transcript = transcript
            };
        }

        private static double WeightedScore(VerificationFieldMatch? title, VerificationFieldMatch? author, VerificationFieldMatch? narrator, VerificationFieldMatch? publisher)
        {
            double sum = 0, weights = 0;
            void Add(VerificationFieldMatch? match, double weight)
            {
                if (match == null) return;
                sum += match.Score * weight;
                weights += weight;
            }

            Add(title, TitleWeight);
            Add(author, AuthorWeight);
            Add(narrator, NarratorWeight);
            Add(publisher, PublisherWeight);

            return weights > 0 ? sum / weights : 0;
        }

        private static string? BuildTranscript(string? openingText, string? closingText)
        {
            var parts = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(openingText)) parts.Add($"[opening] {openingText.Trim()}");
            if (!string.IsNullOrWhiteSpace(closingText)) parts.Add($"[closing] {closingText.Trim()}");
            return parts.Count > 0 ? string.Join('\n', parts) : null;
        }
    }
}
