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
            "adaptado|adaptation|illustrated|annotated|annotation|introduction|introductions|introduccion|" +
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

        // Inside brackets the vocabulary depends on who is asking, and that is the whole
        // subtlety of this class.
        //
        // Amazon and Audible use a trailing parenthetical for two different things: the person's
        // role ("Smith(Translated by)") and the edition ("Lewis Carroll (Illustrated)"). Nothing
        // in the text tells them apart.
        //
        // For REMOVING the tail that ambiguity is cheap, and resolving it either way gives the
        // same answer: the person's name without the bracket. "Lewis Carroll (Illustrated)"
        // becoming "Lewis Carroll" is what you wanted from a reading that was arguably wrong,
        // and it stops one author being two entries. So StripRole accepts the loose form.
        //
        // For DECIDING WHO WROTE THE BOOK it is not cheap at all. Reading an edition descriptor
        // as a role there demotes the author in favour of whoever is credited next, which is
        // the same severity as deleting them. So IsRoleCredit, which is what Primary consults,
        // takes the strict form: an agent noun names a person and can only be a credit, and a
        // participle has to carry a "by" before it counts.
        //
        // An earlier revision used the loose form for both and a review caught it:
        // Primary(["Lewis Carroll (Illustrated)", "John Tenniel"]) returned John Tenniel.
        private const string ParenStrictBody =
            TailLead + "(?:" + AgentAny + "|(?:" + WorkRoleWords + ")\\s+by)" + TailRest;

        private const string ParenLooseBody = DashTailBody;

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
        // What Primary consults. Strict: an edition descriptor must not decide authorship.
        private static readonly Regex ParenthesisedRole = new(
            "\\s*\\(\\s*" + ParenStrictBody + "\\s*\\)\\s*$", TailOptions, MatchTimeout);

        // What StripRole removes. Loose: an edition descriptor is worth taking off a name.
        private static readonly Regex ParenthesisedTail = new(
            "\\s*\\(\\s*" + ParenLooseBody + "\\s*\\)\\s*$", TailOptions, MatchTimeout);

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
                return ParenthesisedRole.IsMatch(trimmed) || DashTail.IsMatch(trimmed);
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
                // Repeated because one pass is not a fixed point. Each regex is anchored at the
                // end and replaces once, so removing an outer tail can expose an inner one:
                // "Constance Garnett (editor) - translator" loses the dash tail and is left
                // holding a bracket tail that nothing has looked at. The cap is there so a
                // pathological input cannot spin; four is well past anything observed.
                stripped = trimmed;
                for (var pass = 0; pass < 4; pass++)
                {
                    var before = stripped;
                    stripped = ParenthesisedTail.Replace(stripped, string.Empty).TrimEnd();
                    stripped = DashTail.Replace(stripped, string.Empty).TrimEnd();
                    if (stripped == before)
                    {
                        break;
                    }
                }
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
        /// Two things happen here beyond the obvious, and both are forced.
        ///
        /// Credits that name a role are moved after the ones that do not, keeping the relative
        /// order within each group. That is not tidiness. Once the role is removed nothing
        /// downstream can tell a translator from an author, and four separate code paths pick an
        /// author by taking <c>Authors[0]</c> off the stored row: the library path planner, the
        /// rename service, the manual import planner and the search result classifier. If a
        /// reversed byline were stored in the order it arrived, those four would all name the
        /// folder after the translator while the add path, which still sees the provider's
        /// original strings, named it after the author. They would disagree about the same book
        /// and rename would keep proposing to move it. Putting the author first makes
        /// <c>Authors[0]</c> and <see cref="Primary"/> agree by construction.
        ///
        /// Removing a role can also make two credits identical, which is the one problem this
        /// rule creates that dropping the credit did not: somebody credited as both author and
        /// translator arrives as two names and would otherwise leave as the same name twice.
        /// Duplicates are collapsed, first occurrence winning.
        /// </remarks>
        public static IReadOnlyList<string> WithoutRoleSuffixes(IReadOnlyList<string>? credits)
        {
            if (credits == null || credits.Count == 0)
            {
                return credits ?? Array.Empty<string>();
            }

            var authors = new List<string>(credits.Count);
            var contributors = new List<string>(credits.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var credit in credits)
            {
                var stripped = StripRole(credit);
                if (stripped.Length == 0 || !seen.Add(stripped))
                {
                    continue;
                }

                if (IsRoleCredit(credit))
                {
                    contributors.Add(stripped);
                }
                else
                {
                    authors.Add(stripped);
                }
            }

            authors.AddRange(contributors);
            return authors.Count == 0 ? credits : authors;
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
