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

namespace Listenarr.Application.Audiobooks.Verification
{
    /// <summary>
    /// Extracts what the spoken credits CLAIM the book is from an opening
    /// transcript (ADR-0001 "relabel" remediation). Spoken credits are heavily
    /// templated — "&lt;Publisher&gt; presents &lt;Title&gt;, by &lt;Author&gt;.
    /// Narrated by &lt;Narrator&gt;" — so a rule-based pass covers the common
    /// shapes. Extraction is deliberately conservative: fields that don't match
    /// a known pattern come back null, because the output's job is to SEED a
    /// human-confirmed catalog search, where a missing suggestion costs nothing
    /// but a wrong one misleads. The ADR's Phase-2 local-LLM pass can later
    /// replace this behind the same shape for the long tail of phrasings.
    /// </summary>
    public static class SpokenCreditsExtractor
    {
        // Credits live at the very start; ignore everything past this so chapter
        // text containing "by" can't fabricate claims.
        private const int WindowChars = 700;
        private const int MaxFieldLength = 120;

        // "narrated by X", "read by X", "performed by X" — narrator span ends at
        // sentence punctuation or a follow-on production credit ("and directed by").
        // A period preceded by a lone capital is a middle initial ("Sarah J. Mass"),
        // not a sentence end — the (?<!\s[A-Z]) lookbehind keeps consuming through it.
        private static readonly Regex NarratorRegex = new(
            @"\b(?:narrated|read|performed)\s+by\s+(?<name>[^;:!?]+?)(?=\s+and\s+(?:directed|produced|engineered|adapted|edited)\b|\s*[;:!?]|(?<!\s[A-Z])\s*\.(?:\s|$)|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Author candidate: a "by X" clause. Which "by" is the author's is decided
        // in ExtractAuthor (must not be a narrator/production/copyright "by").
        // Same middle-initial handling as the narrator span.
        private static readonly Regex ByClauseRegex = new(
            @"\b(?:written\s+by|by)\s+(?<name>[^,;:!?]+?)(?=\s*[,;:!?]|\s+(?:narrated|read|performed)\b|(?<!\s[A-Z])\s*\.(?:\s|$)|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Words that, appearing just before a "by", mark it as NOT the author's.
        private static readonly Regex NonAuthorContextRegex = new(
            @"(?:narrated|read|performed|directed|produced|engineered|adapted|edited|copyright(?:ed)?|recording|music|\d{4})\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // "<Publisher> presents" — capitalized tokens (no '.' in the token class so
        // the span can't bridge a sentence boundary like "Audible. Recorded Books")
        // so transcript noise like "and one-click digital present" doesn't get
        // promoted to a publisher. The literal allows "Presents" — STT capitalizes it.
        private static readonly Regex PublisherPresentsRegex = new(
            @"(?<name>[A-Z][A-Za-z0-9&'\-]*(?:\s+[A-Z][A-Za-z0-9&'\-]*){0,4})\s+[Pp]resents?\b",
            RegexOptions.Compiled);

        // Fallback: "This recording is copyrighted 2009 by Recorded Books."
        private static readonly Regex PublisherCopyrightRegex = new(
            @"recording\s+is\s+copyright(?:ed)?\s+[^.;]*?\bby\s+(?<name>[^.;:!?]+?)(?=\s*[.;:!?]|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Lead-in fillers preceding the title inside a "presents" clause, e.g.
        // "presents a sci-fi audio production, <Title>" / "and now, <Title>".
        private static readonly Regex TitleLeadInRegex = new(
            @"^(?:(?:a|an|the)\b[^,]*\b(?:production|presentation|recording|audio|edition)\b[^,]*|and\s+now)\s*,\s*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Announcement-shaped phrases: the audio is talking ABOUT a recording
        // (credits, idents, production notes), not just narrating prose. Used to
        // distinguish "announces a different book" (a real mismatch) from "never
        // announces anything" (NoSpokenCredits — not evidence of wrong content).
        private static readonly Regex CreditMarkerRegex = new(
            @"\b(?:narrated\s+by|read\s+by|performed\s+by|written\s+by|presents?\b|audiobook|unabridged|abridged|production\s+of|thank\s+you\s+for\s+listening|this\s+is\s+audible|recorded\s+books|copyright(?:ed)?\s+\d{4})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// True when the transcript contains announcement-shaped phrases — the
        /// audio credits/idents SOMETHING, even if the extractor couldn't pull
        /// structured claims out of it. Pure for unit testing.
        /// </summary>
        public static bool ContainsCreditMarkers(string? transcript) =>
            !string.IsNullOrWhiteSpace(transcript) && CreditMarkerRegex.IsMatch(transcript);

        /// <summary>
        /// Extract claimed credits from the opening transcript. Returns null when
        /// nothing recognizable was found.
        /// </summary>
        public static SpokenCredits? Extract(string? openingTranscript)
        {
            if (string.IsNullOrWhiteSpace(openingTranscript)) return null;

            // Drop whisper's parenthesized sound cues ("(upbeat music)"), collapse
            // whitespace, and bound the window.
            var text = Regex.Replace(openingTranscript, @"\([^)]{0,60}\)", " ");
            text = Regex.Replace(text, @"\s+", " ").Trim();
            if (text.Length > WindowChars) text = text[..WindowChars];

            var narrator = ExtractNarrator(text);
            var (author, authorByIndex) = ExtractAuthor(text);
            var title = author != null ? ExtractTitle(text, authorByIndex) : null;
            var publisher = ExtractPublisher(text);

            var credits = new SpokenCredits
            {
                Title = title,
                Author = author,
                Narrator = narrator,
                Publisher = publisher
            };
            return credits.IsEmpty ? null : credits;
        }

        private static string? ExtractNarrator(string text)
        {
            var match = NarratorRegex.Match(text);
            return match.Success ? CleanSpan(match.Groups["name"].Value) : null;
        }

        private static (string? Author, int ByIndex) ExtractAuthor(string text)
        {
            foreach (Match match in ByClauseRegex.Matches(text))
            {
                var preceding = text[..match.Index];
                if (NonAuthorContextRegex.IsMatch(preceding)) continue;

                var name = CleanSpan(match.Groups["name"].Value);
                if (name == null) continue;

                // A plausible person name starts with an uppercase letter or digit
                // (STT capitalizes proper nouns); reject prose like "by the way".
                if (!char.IsUpper(name[0]) && !char.IsDigit(name[0])) continue;

                return (name, match.Index);
            }
            return (null, -1);
        }

        private static string? ExtractTitle(string text, int authorByIndex)
        {
            if (authorByIndex <= 0) return null;

            // The title is the span immediately before the author's "by", within
            // the same sentence.
            var before = text[..authorByIndex];
            var sentenceStart = before.LastIndexOfAny(new[] { '.', '!', '?', ';' });
            var fragment = before[(sentenceStart + 1)..].Trim();

            // "The Housekeepers. Written by ..." — the by-clause starts its own
            // sentence, so the title is the whole PREVIOUS sentence.
            if (string.IsNullOrWhiteSpace(fragment) && sentenceStart >= 0)
            {
                var prior = before[..sentenceStart];
                var priorStart = prior.LastIndexOfAny(new[] { '.', '!', '?', ';' });
                fragment = prior[(priorStart + 1)..].Trim();
            }

            // Inside a "presents" clause only the part after "presents" is title
            // territory ("Blackstone Audio Presents 1984").
            var presents = Regex.Match(fragment, @"\bpresents?\b[,:]?\s*", RegexOptions.IgnoreCase | RegexOptions.RightToLeft);
            if (presents.Success)
            {
                fragment = fragment[(presents.Index + presents.Length)..];
            }

            // Strip production lead-ins ("a sci-fi audio production,", "and now,")
            // repeatedly — some idents chain them.
            string previous;
            do
            {
                previous = fragment;
                fragment = TitleLeadInRegex.Replace(fragment.Trim(), string.Empty);
            } while (fragment != previous);

            return CleanSpan(fragment);
        }

        private static string? ExtractPublisher(string text)
        {
            var presents = PublisherPresentsRegex.Match(text);
            if (presents.Success)
            {
                var name = CleanSpan(presents.Groups["name"].Value);
                // "This is Audible" idents make "Audible presents" common but
                // useless as a publisher; prefer the copyright line in that case.
                if (name != null && !name.Equals("audible", StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }

            var copyright = PublisherCopyrightRegex.Match(text);
            return copyright.Success ? CleanSpan(copyright.Groups["name"].Value) : null;
        }

        /// <summary>Trim punctuation/quote shells and reject implausible spans.</summary>
        private static string? CleanSpan(string raw)
        {
            var value = raw.Trim().Trim('"', '\'', '“', '”', '‘', '’', ',', ':', '-', '–', ' ');
            if (value.Length < 2 || value.Length > MaxFieldLength) return null;
            if (!value.Any(char.IsLetterOrDigit)) return null;
            return value;
        }
    }
}
