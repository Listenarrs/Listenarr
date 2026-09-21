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

namespace Listenarr.Domain.Common
{
    /// <summary>
    /// Removes a contributor role from the end of a credited name.
    /// </summary>
    /// <remarks>
    /// Audible has no role field. A translator or an editor arrives as an ordinary author name
    /// with the role appended to it, so a book comes back credited to
    /// <c>["Fyodor Dostoevsky", "Constance Garnett - translator"]</c>, and the second of those
    /// is displayed, grouped and looked up as though somebody were named that.
    ///
    /// Two rules were considered for this. Readarr's is positional: keep the first credit and
    /// drop the rest. That is unsafe here, because the order is a publisher's editorial choice.
    /// Two editions of the same work credit the same pair in opposite orders: <c>B002V9ZF3K</c>
    /// lists Dostoevsky then Garnett, <c>B00EZAXAF8</c> lists Garnett then Dostoevsky, so
    /// keeping index 0 files the second under its translator.
    ///
    /// The second was to drop any credit that names a role. This class does neither: it removes
    /// the role and keeps the person. Two reasons, both measured.
    ///
    /// Whatever decides a role credit is a heuristic over provider text, and it will sometimes
    /// be wrong. Dropping on a wrong answer deletes a credited person from their own book.
    /// Removing a suffix on a wrong answer shortens a name. Those are not comparable, and the
    /// heuristic fires on the same names either way.
    ///
    /// And a book credited to nobody but contributors, an anthology under its editors or a
    /// classic under its translator, cannot be helped by dropping at all: something has to
    /// survive, so the credits stay exactly as they were. Removing the role fixes those, and
    /// they are not rare.
    ///
    /// What it does NOT do, which matters when reading the Authors page: a translator still
    /// appears as a credited name, now spelled correctly. This makes credited names clean; it
    /// does not decide who wrote the book. <see cref="Primary"/> is where that question is
    /// answered, and it still prefers a credit that names no role.
    /// </remarks>
    public static class AuthorCredits
    {
        // The vocabulary is split because the two notations are not equally trustworthy.
        //
        // An agent noun names a person, so it can only be a credit. A participle or an abstract
        // noun describes an activity, and Amazon and Audible use exactly those words in brackets
        // to describe the WORK rather than the person: "(Illustrated)", "(Annotated)",
        // "(Adapted)" sit on a large share of public-domain classics. Reading one of those as a
        // contributor credit removes the actual author, which is the failure this class exists
        // to prevent, arrived at from the other direction.
        private const string AgentRoleWords =
            "translator|traducteur|traductrice|traduttore|tradutor|tradutora|traductor|" +
            "traductora|ubersetzer|übersetzer|editor|editora|editeur|éditeur|illustrator|" +
            "adapter|adaptateur|annotator|compiler|contributor";

        // "editora" is deliberately in the list above and is the weakest member of it: in
        // Portuguese it usually means a publishing house, and Listenarr has a Publisher field of
        // its own. It is kept because a Brazilian edited volume does credit an "editora" as a
        // person, and dropped credits are cheaper to notice than missing ones. Noted so the next
        // reader knows the ambiguity was seen rather than missed.

        private const string WorkRoleWords =
            "translated|translation|traducao|tradução|traduccion|traducción|edited|adapted|" +
            "adaptado|adaptation|illustrated|annotation|introduction|introductions|introduccion|" +
            "introducción|foreword|afterword|preface|préface|prefacio|postface|avant-propos|" +
            "prologue|prologo|prólogo|essay|notes";

        // Words that may sit inside a role tail without being the role themselves: joiners, and
        // the post-nominals Audible leaves stranded after one ("editor Jr."). On their own they
        // mean nothing, which is why both tails below require an actual role word as well.
        private const string TailFiller = "by|and|or|jr\\.?|sr\\.?|ph\\.?d\\.?|m\\.?d\\.?|series";

        // Agent nouns take a plural: Audible credits "translators" and "editors" on volumes with
        // more than one. The work words are left alone, since "notes" is already the plural form
        // and the rest do not pluralise as credits.
        private const string AgentAny = "(?:" + AgentRoleWords + ")s?";

        private const string AnyRole = AgentAny + "|" + WorkRoleWords;
        private const string TailRest = "(?:\\s+(?:" + AnyRole + "|" + TailFiller + "))*";
        private const string TailLead = "(?:(?:" + TailFiller + ")\\s+)*";

        // After a dash, both halves of the vocabulary are safe. A dash tail is how Audible writes
        // a role suffix, so "Ralph Manheim - translated" has to keep matching.
        private const string DashTailBody =
            TailLead + "(?:" + AnyRole + ")" + TailRest;

        // Inside brackets the vocabulary is the same as after a dash, and that is a reversal
        // worth explaining because it was the other way round one branch ago.
        //
        // Amazon and Audible use a trailing parenthetical for two different things: the
        // person's role ("Smith(Translated by)") and the edition ("Lewis Carroll (Illustrated)").
        // While the rule was to DROP a detected credit, telling those apart was critical, since
        // reading an edition descriptor as a role deleted the author. So participles were
        // required to carry a "by" in brackets.
        //
        // Removing the role rather than the credit changes what a confusion costs. Both readings
        // now produce the same thing, the person's name without the parenthetical, and for an
        // edition descriptor that is the answer you wanted anyway: "Lewis Carroll (Illustrated)"
        // and "Miguel de Cervantes (adapted)" both become the author they were always about,
        // and stop being separate entries on the Authors page. So the restriction is lifted and
        // the two notations agree.
        //
        // What still protects a real name is the vocabulary, not the punctuation. A trailing
        // parenthetical is only removed when everything inside it is role vocabulary, so
        // "Hector Hugh Munro (Saki)", "Plato (Greek)" and "Martin Luther King (Jr.)" are
        // untouched. There are tests for each.
        private const string ParenTailBody = DashTailBody;

        // Every separator inside a tail is mandatory whitespace. That is not tidiness: the
        // previous pattern put optional whitespace on both sides of an alternation inside a
        // repetition, which let a tail be split exponentially many ways and took ninety seconds
        // on a 133 character input. There is now exactly one way to split any tail.
        //
        // NonBacktracking removes the blowup by construction rather than by careful authoring,
        // and the timeout is a backstop for that reasoning being wrong. Compiled is not requested
        // alongside it.
        private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

        private const RegexOptions TailOptions =
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

        // Audible is inconsistent about the spacing ("Gems -introduction by" as well as
        // "Garnett - translator") and uses hyphen, en dash and em dash.
        private static readonly Regex DashTail = new(
            "\\s[-\u2013\u2014]\\s*" + DashTailBody + "$", TailOptions, MatchTimeout);

        // The padding inside the brackets lives here rather than inside the shared tail constant.
        // Putting it back in the constant is what made the pattern exponential, and while
        // NonBacktracking would now absorb that, the call-site version stays correct if anyone
        // ever removes the option.
        private static readonly Regex ParenthesisedTail = new(
            "\\s*\\(\\s*" + ParenTailBody + "\\s*\\)\\s*$", TailOptions, MatchTimeout);

        /// <summary>
        /// True when a credited name ends in a contributor role rather than naming an author.
        /// </summary>
        /// <remarks>
        /// Only a trailing tail counts, and only when it contains at least one role word and
        /// nothing but role vocabulary and joiners. Four things it deliberately does not match:
        ///
        /// A dashed tail that is not a role. Audible credits transliterated names as
        /// <c>"Yang Jing - Yang Jing"</c>, and a rule keying on the dash alone would discard a
        /// real author.
        ///
        /// A combined credit such as <c>"Jonathan Maberry - editor/author"</c>. The person is
        /// also an author of the book, so the credit stays. Note how that actually works: a
        /// slash is not a separator the tail knows about, so it simply ends the tail and the
        /// match fails. The side effect is that <c>"someone - editor/translator"</c> is kept
        /// too, which is a credit that should have gone. That is the safe direction to be
        /// wrong in and it is not worth a second separator to fix.
        ///
        /// A role word anywhere but the tail. An author surnamed Editor, or one whose name
        /// contains "Foreword", is a name and not a credit.
        ///
        /// A tail of post-nominals with no role in it. <c>"Martin Luther King (Jr.)"</c> and
        /// <c>"Gabor Maté (M.D.)"</c> are names. Those suffixes are only tolerated when they
        /// trail an actual role, as Audible leaves them in <c>"editor Jr."</c>.
        /// </remarks>
        public static bool IsRoleCredit(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            var trimmed = name.Trim();
            try
            {
                return ParenthesisedTail.IsMatch(trimmed) || DashTail.IsMatch(trimmed);
            }
            catch (RegexMatchTimeoutException)
            {
                // Not unreachable, just far away: a non-backtracking match is linear in the
                // input, and linear on a long enough string still passes 100 ms. Measured, it
                // takes somewhere between one and fifteen million characters, which no author
                // name will ever be, but a mangled provider payload might. If it does fire the
                // answer has to be "not a contributor". This runs per credited name during
                // ingestion, so throwing would fail the whole import over a punctuation
                // pattern, and guessing the other way would delete somebody from their own
                // book. Keeping the credit is the recoverable mistake.
                return false;
            }
        }

        /// <summary>
        /// One credited name with any trailing contributor role removed.
        /// </summary>
        /// <remarks>
        /// The name is returned unchanged when it carries no role, and also when removing the
        /// role would leave nothing behind. A credit of <c>" - translator"</c> with no name in
        /// front of it is not improved by becoming an empty string: whatever the provider sent
        /// is at least something a person can recognise and correct.
        /// </remarks>
        public static string StripRole(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return name ?? string.Empty;
            }

            var trimmed = name.Trim();
            string stripped;
            try
            {
                stripped = ParenthesisedTail.Replace(trimmed, string.Empty);
                stripped = DashTail.Replace(stripped, string.Empty);
            }
            catch (RegexMatchTimeoutException)
            {
                // Same reasoning as IsRoleCredit: leave the name alone rather than throwing
                // partway through an import.
                return trimmed;
            }

            stripped = stripped.Trim().TrimEnd(',', '-', '\u2013', '\u2014', '(', ' ').Trim();
            return stripped.Length == 0 ? trimmed : stripped;
        }

        /// <summary>
        /// The credited names with their roles removed, in order, without duplicates.
        /// </summary>
        /// <remarks>
        /// Removing a role can make two credits identical, which is the one way this rule
        /// creates a problem the alternative did not: a book crediting somebody as both author
        /// and translator arrives as two names and would otherwise leave as the same name twice.
        /// Duplicates are collapsed, keeping the first occurrence so byline order survives.
        /// </remarks>
        public static IReadOnlyList<string> WithoutRoleSuffixes(IReadOnlyList<string>? credits)
        {
            if (credits == null || credits.Count == 0)
            {
                return credits ?? Array.Empty<string>();
            }

            var cleaned = new List<string>(credits.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var credit in credits)
            {
                var stripped = StripRole(credit);
                if (stripped.Length > 0 && seen.Add(stripped))
                {
                    cleaned.Add(stripped);
                }
            }

            return cleaned.Count == 0 ? credits : cleaned;
        }

        /// <summary>
        /// The one name to use where a single author is required, such as a folder name.
        /// </summary>
        /// <remarks>
        /// Removing roles does not answer this on its own. Stripping
        /// <c>["Constance Garnett - translator", "Fyodor Dostoevsky"]</c> gives two clean names
        /// and the first of them is still the translator, so the choice has to be made against
        /// the original credits and the role has to be read before it is removed.
        ///
        /// So: the first credit that names no role, cleaned. If every credit names one, the
        /// first, cleaned, because an editor's name beats "Unknown Author". Null when there is
        /// nothing to choose from, which leaves the caller's own fallback in charge.
        /// </remarks>
        public static string? Primary(IReadOnlyList<string>? credits)
        {
            if (credits == null || credits.Count == 0)
            {
                return null;
            }

            foreach (var credit in credits)
            {
                if (!string.IsNullOrWhiteSpace(credit) && !IsRoleCredit(credit))
                {
                    return StripRole(credit);
                }
            }

            var first = credits.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
            return first == null ? null : StripRole(first);
        }
    }
}
