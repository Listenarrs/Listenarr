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

namespace Listenarr.Domain.Audiobooks
{
    /// <summary>
    /// Score and evidence for a single metadata field matched against the
    /// spoken-credits transcript (ADR-0001).
    /// </summary>
    /// <param name="Score">Match strength in [0, 1].</param>
    /// <param name="MatchedText">The transcript fragment that produced the score, for audit/triage UI; null when nothing usable was found.</param>
    public record VerificationFieldMatch(double Score, string? MatchedText);

    /// <summary>
    /// What the spoken credits CLAIM the book is — extracted from the opening
    /// transcript independently of whether it matches the stored metadata
    /// (ADR-0001). Drives the "relabel to the right book" remediation flow on
    /// flagged books. Null fields were not confidently extracted; extraction is
    /// deliberately conservative (a missing suggestion is better than a wrong one).
    /// </summary>
    public record SpokenCredits
    {
        public string? Title { get; init; }
        public string? Author { get; init; }
        public string? Narrator { get; init; }
        public string? Publisher { get; init; }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsEmpty => Title == null && Author == null && Narrator == null && Publisher == null;
    }

    /// <summary>
    /// On-disk audio length versus the catalog runtime (ADR-0001 completeness
    /// check). Distinguishes "partial content" (right book, most parts missing —
    /// a mid-book cold open that would otherwise read as a wrong-content
    /// mismatch) from genuinely wrong audio.
    /// </summary>
    /// <param name="ExpectedMinutes">Catalog runtime.</param>
    /// <param name="ActualMinutes">Sum of tracked files' durations (size-extrapolated for files whose duration probe failed).</param>
    /// <param name="Coverage">ActualMinutes / ExpectedMinutes.</param>
    public record VerificationCompleteness(double ExpectedMinutes, double ActualMinutes, double Coverage);

    /// <summary>
    /// Result of an identity-verification pass over one audiobook (ADR-0001).
    /// Per-field results are kept separate (not folded into one confidence blob)
    /// so the triage UI can show which field diverged and thresholds can be
    /// tuned per field. A null field means it was not evaluated (e.g. the book
    /// has no stored narrator).
    /// </summary>
    public record VerificationVerdict
    {
        public required VerificationOutcome Outcome { get; init; }

        /// <summary>Aggregate confidence in the outcome, in [0, 1].</summary>
        public required double Confidence { get; init; }

        /// <summary>How the verdict was produced, e.g. "deterministic" or "llm:ollama:llama3".</summary>
        public required string Method { get; init; }

        public VerificationFieldMatch? TitleMatch { get; init; }
        public VerificationFieldMatch? AuthorMatch { get; init; }
        public VerificationFieldMatch? NarratorMatch { get; init; }
        public VerificationFieldMatch? PublisherMatch { get; init; }

        /// <summary>What STT heard (sampled windows), persisted for audit.</summary>
        public string? Transcript { get; init; }

        /// <summary>
        /// What the spoken credits claim the book is, when extractable. Null when
        /// the transcript carried no recognizable credit announcement.
        /// </summary>
        public SpokenCredits? HeardCredits { get; init; }

        /// <summary>
        /// On-disk audio length versus the catalog runtime. Null when the record
        /// has no catalog runtime or no usable per-file duration data.
        /// </summary>
        public VerificationCompleteness? Completeness { get; init; }
    }
}
