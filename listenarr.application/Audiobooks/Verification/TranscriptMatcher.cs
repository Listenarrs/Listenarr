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
using System.Text.RegularExpressions;
using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks.Verification
{
    /// <summary>
    /// Pure per-field matching of a spoken-credits transcript against stored
    /// metadata (ADR-0001 Tier 1). Titles match exact-ish on normalized tokens;
    /// author/narrator names additionally match phonetically and by edit
    /// distance, because STT mangles proper nouns far more than common words.
    /// All methods are deterministic and side-effect free for testability.
    /// </summary>
    public static class TranscriptMatcher
    {
        // A transcript with fewer usable tokens than this carries no signal either
        // way (music intro, silence, foreign language) and must land in Uncertain.
        public const int MinUsableTranscriptTokens = 8;

        private static readonly Regex TokenPattern = new(@"[a-z0-9']+", RegexOptions.Compiled);

        // Function words on the HEARD side never constitute a name match: "and"
        // is one edit (and one phonetic class) away from "Andy", so without this
        // gate every transcript containing "and" scores against author "Andy *".
        // An exact heard==target hit still passes (name particles like "van").
        private static readonly HashSet<string> NameStopWords = new(StringComparer.Ordinal)
        {
            "a", "an", "and", "the", "by", "for", "of", "to", "in", "on", "with", "as", "at", "or", "is", "was"
        };

        // STT emits numbers either as digits or words; map the small ones so
        // "Book 2" matches a spoken "book two".
        private static readonly Dictionary<string, string> NumberWords = new(StringComparer.Ordinal)
        {
            ["zero"] = "0", ["one"] = "1", ["two"] = "2", ["three"] = "3", ["four"] = "4",
            ["five"] = "5", ["six"] = "6", ["seven"] = "7", ["eight"] = "8", ["nine"] = "9",
            ["ten"] = "10", ["eleven"] = "11", ["twelve"] = "12", ["thirteen"] = "13",
            ["fourteen"] = "14", ["fifteen"] = "15", ["sixteen"] = "16", ["seventeen"] = "17",
            ["eighteen"] = "18", ["nineteen"] = "19", ["twenty"] = "20"
        };

        /// <summary>Lowercase word tokens of a transcript, numbers canonicalized to digits.</summary>
        public static IReadOnlyList<string> Tokenize(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
            return TokenPattern.Matches(text.ToLowerInvariant())
                .Select(m => m.Value.Trim('\''))
                .Where(t => t.Length > 0)
                .Select(t => NumberWords.GetValueOrDefault(t, t))
                .ToList();
        }

        /// <summary>
        /// Match the stored title against the transcript: normalized via
        /// <see cref="TitleUtils.NormalizeTitle"/>, then best aligned sliding
        /// window over the transcript tokens. Returns null when the book has no
        /// usable title to evaluate.
        /// </summary>
        public static VerificationFieldMatch? MatchTitle(IReadOnlyList<string> transcriptTokens, string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            var titleTokens = Tokenize(TitleUtils.NormalizeTitle(title));
            if (titleTokens.Count == 0) return null;

            return BestWindow(transcriptTokens, titleTokens, nameMode: false);
        }

        /// <summary>
        /// Match a set of person names (authors or narrators) against the
        /// transcript. One clearly-heard name is enough — credits rarely read
        /// every co-author — so the field score is the best name's score.
        /// Returns null when no usable names are stored.
        /// </summary>
        public static VerificationFieldMatch? MatchNames(IReadOnlyList<string> transcriptTokens, IEnumerable<string>? names)
        {
            if (names == null) return null;

            // Single-letter tokens are dropped from BOTH sides: the stored name's
            // middle initial ("Sarah J. Maas" → [sarah, maas]) AND the transcript's
            // spoken initial — otherwise the aligned window compares "maas" against
            // "j" and a perfectly-read credit scores 0.5.
            var heardTokens = transcriptTokens.Where(t => t.Length >= 2).ToList();

            VerificationFieldMatch? best = null;
            foreach (var name in names)
            {
                var nameTokens = Tokenize(name).Where(t => t.Length >= 2).ToList();
                if (nameTokens.Count == 0) continue;

                var match = BestWindow(heardTokens, nameTokens, nameMode: true);
                if (best == null || match.Score > best.Score) best = match;
            }
            return best;
        }

        /// <summary>
        /// Match a free-text phrase (publisher) against the transcript.
        /// Returns null when the phrase is empty.
        /// </summary>
        public static VerificationFieldMatch? MatchPhrase(IReadOnlyList<string> transcriptTokens, string? phrase)
        {
            if (string.IsNullOrWhiteSpace(phrase)) return null;
            var phraseTokens = Tokenize(phrase);
            if (phraseTokens.Count == 0) return null;

            return BestWindow(transcriptTokens, phraseTokens, nameMode: false);
        }

        /// <summary>
        /// Slide an aligned window of the target's length over the transcript and
        /// score each position as the average per-token similarity; the field
        /// score is the best window. Alignment keeps locality — "Andy" scoring
        /// against minute-apart tokens is not a name match.
        /// </summary>
        private static VerificationFieldMatch BestWindow(IReadOnlyList<string> transcriptTokens, IReadOnlyList<string> targetTokens, bool nameMode)
        {
            if (transcriptTokens.Count == 0) return new VerificationFieldMatch(0, null);

            var bestScore = 0.0;
            var bestStart = -1;
            var lastStart = Math.Max(0, transcriptTokens.Count - targetTokens.Count);

            for (var start = 0; start <= lastStart; start++)
            {
                var sum = 0.0;
                for (var i = 0; i < targetTokens.Count; i++)
                {
                    var transcriptIndex = start + i;
                    sum += transcriptIndex < transcriptTokens.Count
                        ? TokenSimilarity(targetTokens[i], transcriptTokens[transcriptIndex], nameMode)
                        : 0;
                }
                var score = sum / targetTokens.Count;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestStart = start;
                }
            }

            // Only surface matched text when there is something usable to audit;
            // a sub-noise-floor "best" window is misleading in the triage UI.
            string? matchedText = null;
            if (bestStart >= 0 && bestScore >= 0.35)
            {
                var length = Math.Min(targetTokens.Count, transcriptTokens.Count - bestStart);
                matchedText = string.Join(' ', transcriptTokens.Skip(bestStart).Take(length));
            }

            return new VerificationFieldMatch(Math.Round(bestScore, 3), matchedText);
        }

        private static double TokenSimilarity(string target, string heard, bool nameMode)
        {
            if (target == heard) return 1.0;
            if (target.Length == 0 || heard.Length == 0) return 0;
            if (nameMode && NameStopWords.Contains(heard)) return 0;

            // Levenshtein similarity carries most of the weight; below 0.6 two
            // short words are essentially unrelated, so clamp to zero noise.
            var distance = StringUtils.LevenshteinDistance(target, heard);
            var levSimilarity = 1.0 - (double)distance / Math.Max(target.Length, heard.Length);
            if (levSimilarity < 0.6) levSimilarity = 0;

            if (!nameMode) return levSimilarity;

            // Names: an exact phonetic hit ("weir"/"ware") is strong evidence even
            // when the spelling drifted past the edit-distance gate.
            var phonetic = Phonetics.SoundAlike(target, heard) ? 0.85 : 0;
            return Math.Max(levSimilarity, phonetic);
        }
    }
}
