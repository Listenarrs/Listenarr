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

using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Domain.Utils
{
    /// <summary>
    /// The rule is only as good as the thing that decides what a role credit is, so most of
    /// these are about the detector rather than the filtering.
    /// </summary>
    /// <remarks>
    /// A detector that matched every name and a detector that matched none would both pass a
    /// test suite that only ever checks translators get dropped, so every case here is paired
    /// with one that has to come out the other way. Credited names are taken from a catalogue
    /// sample wherever the case allows it, because a made-up string can be made to match
    /// anything. A handful are invented, to reach a shape the sample does not contain, and they
    /// are recognisable as placeholders: "Jane Doe", "Someone", "Alguem".
    /// </remarks>
    [Trait("Name", nameof(AuthorCreditsTests))]
    [Trait("Category", "AuthorCredits")]
    public class AuthorCreditsTests : BaseTests
    {
        [Theory]
        [InlineData("Constance Garnett - translator")]
        [InlineData("George Makepeace Towle - translator")]
        [InlineData("Samuel Butler - translator")]
        [InlineData("Ralph Manheim - translated")]
        [InlineData("Marlaine Delargy - Translated by")]
        [InlineData("James L. Snyder - editor")]
        [InlineData("Geoffrey R. Stone - edited by")]
        [InlineData("Marty Ross - adapted by")]
        [InlineData("Anthony A. Barrett - introduction")]
        [InlineData("Yogi Berra - foreword")]
        [InlineData("Dr. Akiko Iwasaki - afterword")]
        [InlineData("Guy Newland - editor and translator")]
        [InlineData("Theodore C. Van Alst - editor Jr.")]
        [InlineData("Gerald R. Gems -introduction by")]
        [InlineData("A. M. Sheridan Smith(Translated by)")]
        [InlineData("Alfred Lin (Foreword By) - introduction")]
        [InlineData("Ned Asta(Illustrated by)")]
        [InlineData("Ned Asta (Illustrator)")]
        [InlineData("A. M. Sheridan Smith( Translated by )")]
        [InlineData("Someone - translators")]
        [InlineData("Someone - editors")]
        public void IsRoleCredit_RecognisesTheNotationsAudibleActuallyUses(string credit)
        {
            Assert.True(AuthorCredits.IsRoleCredit(credit));
        }

        [Theory]
        [InlineData("Fyodor Dostoevsky")]
        [InlineData("Jules Verne")]
        [InlineData("O. Henry")]
        [InlineData("Hector Hugh Munro (Saki)")]
        [InlineData("Louis-Ferdinand Celine")]
        [InlineData("Jean-Paul Sartre")]
        [InlineData("Yang Jing - Yang Jing")]
        [InlineData("Jonathan Maberry - editor/author")]
        [InlineData("Alison Tyler - author/editor")]
        [InlineData("Martin Luther King (Jr.)")]
        [InlineData("Sammy Davis (Jr.)")]
        [InlineData("Gabor Maté (M.D.)")]
        [InlineData("Jordan B. Peterson (Ph.D.)")]
        [InlineData("Daniel G. Amen - M.D.")]
        [InlineData("Deepak Chopra - MD")]
        [InlineData("Jane Doe - Series")]
        [InlineData("Ali Smith - and")]
        [InlineData("Gedeon - Andor")]
        [InlineData("John Smith, Ph.D.")]
        [InlineData("Robert Downey Jr.")]
        [InlineData("")]
        [InlineData(null)]
        public void IsRoleCredit_LeavesEverythingElseAlone(string? credit)
        {
            Assert.False(AuthorCredits.IsRoleCredit(credit));
        }

        [Fact]
        public void IsRoleCredit_DoesNotFireOnADashThatIsNotACredit()
        {
            // The control for a detector keying on punctuation instead of vocabulary. Audible
            // credits transliterated names this way, and a hyphenated surname is ordinary.
            Assert.False(AuthorCredits.IsRoleCredit("Zhao Fanyu - Zhao Fanyu"));
            Assert.False(AuthorCredits.IsRoleCredit("Eleanor Marx-Aveling"));

            // And the same name with a role on the end must still be caught, or the test above
            // would pass just as well against a detector that never fires at all.
            Assert.True(AuthorCredits.IsRoleCredit("Eleanor Marx-Aveling - translator"));
        }

        [Fact]
        public void IsRoleCredit_NeedsAnActualRoleWordAndNotJustAPostNominal()
        {
            // A tail made only of the words tolerated *around* a role is not a role. Before this
            // was required, post-nominals alone matched, so every "Name (Ph.D.)" byline read as
            // a contributor credit. Health and self-help titles carry those constantly and are
            // often co-credited, so the never-empty guard would not have saved them.
            Assert.False(AuthorCredits.IsRoleCredit("Gabor Maté (M.D.)"));
            Assert.False(AuthorCredits.IsRoleCredit("Martin Luther King (Jr.)"));

            // The same suffixes stay tolerated trailing a real role, which is how Audible emits
            // them. Without this the fix above would have traded one defect for another.
            Assert.True(AuthorCredits.IsRoleCredit("Theodore C. Van Alst - editor Jr."));
        }

        [Fact]
        public void StripRole_NormalisesAnEditionDescriptorOffTheAuthorName()
        {
            // Amazon and Audible put "(Illustrated)" and the like on a large share of
            // public-domain classics, describing the work rather than the person. Those land in
            // the author field often enough to matter, and they split one author into two
            // entries on the Authors page.
            //
            // While the rule was to drop a detected credit this was the worst case in the whole
            // change, because it deleted the author. Removing the role instead gives the answer
            // you wanted from a reading that was arguably wrong, so it is a correction now
            // rather than a hazard. That reversal is deliberate and is explained in the source.
            Assert.Equal("Lewis Carroll", AuthorCredits.StripRole("Lewis Carroll (Illustrated)"));
            Assert.Equal("Jules Verne", AuthorCredits.StripRole("Jules Verne (Illustrated)"));
            Assert.Equal("Virginia Woolf", AuthorCredits.StripRole("Virginia Woolf (Essay)"));

            // And the whole list keeps everybody, which is the difference from the rejected rule.
            Assert.Equal(
                new[] { "Lewis Carroll", "Martin Gardner" },
                AuthorCredits.WithoutRoleSuffixes(new List<string> { "Lewis Carroll (Illustrated)", "Martin Gardner" }));
        }

        [Fact]
        public void StripRole_LeavesAParentheticalThatIsNotAnyKindOfRole()
        {
            // The control that makes the test above safe, and the reason the vocabulary rather
            // than the punctuation is what decides. A pen name, a transliteration, an initialism
            // and a post-nominal all sit in brackets after a name and none of them is a credit.
            Assert.Equal("Hector Hugh Munro (Saki)", AuthorCredits.StripRole("Hector Hugh Munro (Saki)"));
            Assert.Equal("Plato (Platon)", AuthorCredits.StripRole("Plato (Platon)"));
            Assert.Equal("Robert F. Kennedy Jr. (RFK Jr.)", AuthorCredits.StripRole("Robert F. Kennedy Jr. (RFK Jr.)"));
            Assert.Equal("Martin Luther King (Jr.)", AuthorCredits.StripRole("Martin Luther King (Jr.)"));
            Assert.Equal("Gabor Mate (M.D.)", AuthorCredits.StripRole("Gabor Mate (M.D.)"));
            Assert.Equal("Enoch (Traditional Attribution)", AuthorCredits.StripRole("Enoch (Traditional Attribution)"));
        }

        [Fact]
        public void IsRoleCredit_StillNeedsRoleVocabularyInsideTheBrackets()
        {
            // Amazon and Audible put "(Illustrated)" and "(Annotated)" on a large share of
            // public-domain classics, describing the WORK rather than the person. An earlier
            // version of this detector read them as contributor credits, which under the rule
            // this branch rejected deleted the author outright. It is milder now, since nothing
            // is deleted, but a name shortened to "Lewis Carroll" from an edition descriptor
            // would still be a guess the provider did not make.
            Assert.False(AuthorCredits.IsRoleCredit("Hector Hugh Munro (Saki)"));
            Assert.False(AuthorCredits.IsRoleCredit("Enoch (Traditional Attribution)"));

            // The controls that stop this being solved by never matching a parenthetical: an
            // agent noun in brackets names a person, and a participle in brackets does too once
            // "by" follows it. Both are forms Audible really returns.
            Assert.True(AuthorCredits.IsRoleCredit("Ned Asta (Illustrator)"));
            Assert.True(AuthorCredits.IsRoleCredit("A. M. Sheridan Smith(Translated by)"));

            // And after a dash the bare participle still counts, because that is how Audible
            // writes a role suffix.
            Assert.True(AuthorCredits.IsRoleCredit("Ralph Manheim - translated"));
        }

        [Fact]
        public void StripRole_CorrectsAnAdaptationCreditWithoutCallingItARole()
        {
            // B0GTS5WKFF credits nobody but "Miguel de Cervantes (adapted)". Under the rejected
            // rule this string was a liability: it matched, and dropping the credit took
            // Cervantes off his own book, with the never-empty guard saving him only by leaving
            // the string exactly as it was. Removing the tail gives the right answer.
            Assert.Equal("Miguel de Cervantes", AuthorCredits.StripRole("Miguel de Cervantes (adapted)"));

            // But it is not read as a role when deciding authorship, because that would hand
            // the book to whoever is credited after him. The two questions get different
            // answers on purpose and this is the case that shows why.
            Assert.False(AuthorCredits.IsRoleCredit("Miguel de Cervantes (adapted)"));
            Assert.Equal(
                "Miguel de Cervantes",
                AuthorCredits.Primary(new List<string> { "Miguel de Cervantes (adapted)", "Peter Motteux" }));
        }

        [Fact]
        public void IsRoleCredit_DoesNotSplitAWordIntoTwoJoiners()
        {
            // "Andor" is not "and" followed by "or". Separators inside a tail are mandatory
            // whitespace, so one word cannot be read as two.
            Assert.False(AuthorCredits.IsRoleCredit("Gedeon - Andor"));
            Assert.True(AuthorCredits.IsRoleCredit("Guy Newland - editor and translator"));
        }

        [Fact]
        public void IsRoleCredit_ReturnsPromptlyOnAPathologicalInput()
        {
            // A regression test for a measured denial of service. The previous pattern put
            // optional whitespace on both sides of an alternation inside a repetition, so a tail
            // of repeated role words could be split exponentially many ways: 133 characters took
            // about ninety seconds. Ingestion feeds provider data straight in, so that is an
            // outage rather than a slow test.
            var pathological = "A -" + string.Concat(Enumerable.Repeat("  editor", 32)) + " Z";

            var started = System.Diagnostics.Stopwatch.StartNew();
            var matched = AuthorCredits.IsRoleCredit(pathological);
            started.Stop();

            Assert.False(matched);
            Assert.True(
                started.ElapsedMilliseconds < 1000,
                $"took {started.ElapsedMilliseconds} ms, which means the tail is ambiguous again");
        }

        [Fact]
        public void IsRoleCredit_KeepsTheCreditRatherThanThrowingOnAHugeInput()
        {
            // Ingestion calls this once per credited name, so an exception here fails the whole
            // import. Nothing should reach the match timeout now, and if anything ever does the
            // answer has to be "keep the credit": that is the mistake somebody can see and undo,
            // where the other direction removes a person from their own book silently.
            var enormous = new string('a', 200_000) + " - "
                + string.Concat(Enumerable.Repeat("editor ", 5_000));

            Assert.Null(Record.Exception(() => AuthorCredits.IsRoleCredit(enormous)));
        }

        [Fact]
        public void IsRoleCredit_MatchesTheNonEnglishVocabularyWhateverTheCase()
        {
            Assert.True(AuthorCredits.IsRoleCredit("Someone - ÜBERSETZER"));
            Assert.True(AuthorCredits.IsRoleCredit("Alguem - TRADUÇÃO"));
            Assert.True(AuthorCredits.IsRoleCredit("Quelqu'un - PRÉFACE"));
        }

        [Fact]
        public void IsRoleCredit_DoesNotFireOnARoleWordThatIsPartOfTheName()
        {
            // A role word only counts as a credit in the trailing tail. Somebody surnamed
            // Editor is an author, and so is somebody whose book has Translator in the byline
            // position for a reason of their own.
            Assert.False(AuthorCredits.IsRoleCredit("Editor Jones"));
            Assert.False(AuthorCredits.IsRoleCredit("Translator Smith"));
            Assert.True(AuthorCredits.IsRoleCredit("Jones - editor"));
        }

        [Fact]
        public void StripRole_ShortensTheCreditInsteadOfRemovingIt()
        {
            // The whole difference from the rejected rule, in one assertion. The person stays.
            Assert.Equal("Constance Garnett", AuthorCredits.StripRole("Constance Garnett - translator"));
            Assert.Equal("Samuel Butler", AuthorCredits.StripRole("Samuel Butler - translator"));
            Assert.Equal("A. M. Sheridan Smith", AuthorCredits.StripRole("A. M. Sheridan Smith(Translated by)"));
        }

        [Fact]
        public void StripRole_LeavesANameWithNoRoleExactlyAsItIs()
        {
            // The control. A rule that returned a shortened string for everything would pass the
            // test above and fail this one.
            Assert.Equal("Fyodor Dostoevsky", AuthorCredits.StripRole("Fyodor Dostoevsky"));
            Assert.Equal("Hector Hugh Munro (Saki)", AuthorCredits.StripRole("Hector Hugh Munro (Saki)"));
            Assert.Equal("Yang Jing - Yang Jing", AuthorCredits.StripRole("Yang Jing - Yang Jing"));
            Assert.Equal("Eleanor Marx-Aveling", AuthorCredits.StripRole("Eleanor Marx-Aveling"));
        }

        [Fact]
        public void StripRole_CorrectsAnEditionDescriptorThatNamesAnAdaptation()
        {
            // Under the rejected rule this string was a liability: it matched, and dropping it
            // would have removed Cervantes from his own book. Removing the role instead folds
            // the credit onto the author it was always meant to be, which is the case that
            // rule could not have got right at all.
            Assert.Equal("Miguel de Cervantes", AuthorCredits.StripRole("Miguel de Cervantes (adapted)"));
        }

        [Fact]
        public void StripRole_KeepsWhateverArrivedWhenThereWouldBeNothingLeft()
        {
            // The guard, whose meaning changed with the rule. Nothing is ever removed now, so
            // the failure to defend against is a credit that is nothing but a role. An empty
            // author is worse than a strange one, and a strange one is at least correctable by
            // somebody who can see it.
            Assert.Equal("- translator", AuthorCredits.StripRole("- translator"));
            Assert.Equal("(Translated by)", AuthorCredits.StripRole("(Translated by)"));
            Assert.Equal(string.Empty, AuthorCredits.StripRole(""));
            Assert.Equal(string.Empty, AuthorCredits.StripRole(null));
        }

        [Fact]
        public void WithoutRoleSuffixes_CleansTheContributorAndKeepsEverybody()
        {
            // B002V9ZF3K. Both credits survive, and both are spelled as people rather than as
            // people plus a job title.
            Assert.Equal(
                new[] { "Fyodor Dostoevsky", "Constance Garnett" },
                AuthorCredits.WithoutRoleSuffixes(new List<string> { "Fyodor Dostoevsky", "Constance Garnett - translator" }));
        }

        [Fact]
        public void WithoutRoleSuffixes_GivesTheSameNamesWhateverTheBylineOrder()
        {
            // B00EZAXAF8 credits the same pair the other way round.
            var asListed = new List<string> { "Fyodor Dostoevsky", "Constance Garnett - translator" };
            var reversed = new List<string> { "Constance Garnett - translator", "Fyodor Dostoevsky" };

            Assert.Equal(
                AuthorCredits.WithoutRoleSuffixes(asListed).OrderBy(n => n, StringComparer.Ordinal),
                AuthorCredits.WithoutRoleSuffixes(reversed).OrderBy(n => n, StringComparer.Ordinal));
        }

        [Fact]
        public void WithoutRoleSuffixes_FixesABookCreditedToNobodyButContributors()
        {
            // 1094179574 and the single-credit form of the same shape. This is the case the
            // rejected rule could not touch: something had to survive, so it returned the list
            // untouched and the book kept exactly the problem being fixed. There are dozens of
            // these for every handful of the co-credited kind.
            Assert.Equal(
                new[] { "Lisa Morton", "Leslie S. Klinger" },
                AuthorCredits.WithoutRoleSuffixes(new List<string> { "Lisa Morton - editor", "Leslie S. Klinger - editor" }));
            Assert.Equal(
                new[] { "Stephen Mitchell" },
                AuthorCredits.WithoutRoleSuffixes(new List<string> { "Stephen Mitchell - translator" }));
        }

        [Fact]
        public void WithoutRoleSuffixes_LeavesASingleAuthorAndAGenuinePairAlone()
        {
            // Two controls in one. Neither list contains a role, so neither may change at all.
            Assert.Equal(
                new[] { "Edgar Rice Burroughs" },
                AuthorCredits.WithoutRoleSuffixes(new List<string> { "Edgar Rice Burroughs" }));
            Assert.Equal(
                new[] { "O. Henry", "William Sydney Porter" },
                AuthorCredits.WithoutRoleSuffixes(new List<string> { "O. Henry", "William Sydney Porter" }));
        }

        [Fact]
        public void WithoutRoleSuffixes_CollapsesTheDuplicateThatRemovingARoleCanCreate()
        {
            // The one problem this rule creates that the rejected one did not. Somebody credited
            // both as author and as translator arrives as two different strings and would leave
            // as the same string twice. First occurrence wins, so byline order survives.
            Assert.Equal(
                new[] { "Samuel Butler" },
                AuthorCredits.WithoutRoleSuffixes(new List<string> { "Samuel Butler", "Samuel Butler - translator" }));
            Assert.Equal(
                new[] { "Homer", "Samuel Butler" },
                AuthorCredits.WithoutRoleSuffixes(new List<string> { "Homer", "Samuel Butler - translator", "Samuel Butler" }));
        }

        [Fact]
        public void WithoutRoleSuffixes_DropsSeveralRolesAtOnce()
        {
            // 1541437438. One author and two different kinds of contributor, all three kept.
            Assert.Equal(
                new[] { "Tacitus", "J. C. Yardley", "Anthony A. Barrett" },
                AuthorCredits.WithoutRoleSuffixes(new List<string>
                {
                    "Tacitus",
                    "J. C. Yardley - translated",
                    "Anthony A. Barrett - introduction"
                }));
        }

        [Fact]
        public void WithoutRoleSuffixes_HandlesNothingToClean()
        {
            Assert.Empty(AuthorCredits.WithoutRoleSuffixes(new List<string>()));
            Assert.Empty(AuthorCredits.WithoutRoleSuffixes(null));
        }

        [Fact]
        public void WithoutRoleSuffixes_PutsTheAuthorFirstWhenTheProviderDidNot()
        {
            // The stored order is load-bearing and this is why. Once a role is removed nothing
            // downstream can tell a translator from an author, and four separate paths pick an
            // author by taking Authors[0] off the stored row: the library path planner, the
            // rename service, the manual import planner and the search result classifier.
            //
            // A review caught the earlier version storing a reversed byline in arrival order.
            // Those four then named the folder after the translator while the add path, which
            // still sees the provider's own strings, named it after the author. They disagreed
            // about the same book, so rename kept proposing to move it.
            Assert.Equal(
                new[] { "Fyodor Dostoevsky", "Constance Garnett" },
                AuthorCredits.WithoutRoleSuffixes(new List<string> { "Constance Garnett - translator", "Fyodor Dostoevsky" }));

            // The control: where the provider already put the author first, nothing moves.
            Assert.Equal(
                new[] { "Fyodor Dostoevsky", "Constance Garnett" },
                AuthorCredits.WithoutRoleSuffixes(new List<string> { "Fyodor Dostoevsky", "Constance Garnett - translator" }));

            // And the second control, which stops this being solved by sorting: two credits
            // that name no role keep the order they arrived in.
            Assert.Equal(
                new[] { "O. Henry", "William Sydney Porter" },
                AuthorCredits.WithoutRoleSuffixes(new List<string> { "O. Henry", "William Sydney Porter" }));
        }

        [Fact]
        public void WithoutRoleSuffixes_AgreesWithPrimaryOnItsFirstEntry()
        {
            // Stated directly, because the agreement is the property being bought rather than
            // an incidental one. Every consumer that reads Authors[0] has to get what Primary
            // would have said, since none of them can consult the role any more.
            var bylines = new[]
            {
                new List<string> { "Constance Garnett - translator", "Fyodor Dostoevsky" },
                new List<string> { "Fyodor Dostoevsky", "Constance Garnett - translator" },
                new List<string> { "Lewis Carroll (Illustrated)", "John Tenniel" },
                new List<string> { "Lisa Morton - editor", "Leslie S. Klinger - editor" },
                new List<string> { "O. Henry", "William Sydney Porter" },
                new List<string> { "Stephen Mitchell - translator" },
            };

            foreach (var byline in bylines)
            {
                Assert.Equal(AuthorCredits.Primary(byline), AuthorCredits.WithoutRoleSuffixes(byline)[0]);
            }
        }

        [Fact]
        public void Primary_IsNotFooledByAnEditionDescriptorIntoDemotingTheAuthor()
        {
            // The two questions this class answers need different amounts of caution, and a
            // review found an earlier revision using one answer for both.
            //
            // Removing "(Illustrated)" from a name is worth doing whether or not it was really
            // a role, because either reading gives the same string. Treating it as a role when
            // deciding WHO WROTE THE BOOK is not, because it hands authorship to whoever is
            // credited next. That is the same severity as deleting them.
            Assert.Equal(
                "Lewis Carroll",
                AuthorCredits.Primary(new List<string> { "Lewis Carroll (Illustrated)", "John Tenniel" }));
            Assert.Equal(
                "Miguel de Cervantes",
                AuthorCredits.Primary(new List<string> { "Miguel de Cervantes (adapted)", "Peter Motteux" }));

            // So the strict predicate says no and the loose removal still says yes.
            Assert.False(AuthorCredits.IsRoleCredit("Lewis Carroll (Illustrated)"));
            Assert.Equal("Lewis Carroll", AuthorCredits.StripRole("Lewis Carroll (Illustrated)"));

            // The control that keeps the strict predicate useful: a bracket naming a person, or
            // a participle carrying "by", is a credit and must still be read as one.
            Assert.True(AuthorCredits.IsRoleCredit("Ned Asta (Illustrator)"));
            Assert.True(AuthorCredits.IsRoleCredit("A. M. Sheridan Smith(Translated by)"));
            Assert.Equal(
                "Homer",
                AuthorCredits.Primary(new List<string> { "A. M. Sheridan Smith(Translated by)", "Homer" }));
        }

        [Fact]
        public void StripRole_KeepsGoingUntilThereIsNoRoleLeft()
        {
            // One pass is not a fixed point. Each regex is anchored at the end and replaces
            // once, so removing an outer tail can expose an inner one that nothing has looked
            // at. Both of these came back still carrying a role before the loop.
            Assert.Equal("Constance Garnett", AuthorCredits.StripRole("Constance Garnett (editor) - translator"));
            Assert.Equal("Constance Garnett", AuthorCredits.StripRole("Constance Garnett - translator - editor"));

            // The control: a name with nothing to remove is not chewed by the extra passes.
            Assert.Equal("Hector Hugh Munro (Saki)", AuthorCredits.StripRole("Hector Hugh Munro (Saki)"));
        }

        [Fact]
        public void StripRole_RemovesAnAnnotatedEditionDescriptor()
        {
            // "(Annotated)" was named in the source as motivating the loose bracket rule and was
            // missing from the vocabulary, so it never actually stripped.
            Assert.Equal("Mark Twain", AuthorCredits.StripRole("Mark Twain (Annotated)"));
        }

        [Fact]
        public void Primary_PicksTheAuthorWhicheverEndOfTheBylineItIsOn()
        {
            // Removing roles does not settle this by itself: both names come out clean and the
            // translator can still be first, so the role has to be read before it is removed.
            Assert.Equal(
                "Fyodor Dostoevsky",
                AuthorCredits.Primary(new List<string> { "Fyodor Dostoevsky", "Constance Garnett - translator" }));
            Assert.Equal(
                "Fyodor Dostoevsky",
                AuthorCredits.Primary(new List<string> { "Constance Garnett - translator", "Fyodor Dostoevsky" }));
        }

        [Fact]
        public void Primary_CleansTheContributorWhenEveryCreditNamesARole()
        {
            Assert.Equal(
                "Lisa Morton",
                AuthorCredits.Primary(new List<string> { "Lisa Morton - editor", "Leslie S. Klinger - editor" }));
        }

        [Fact]
        public void Primary_StillTakesTheFirstOfTwoPlainCredits()
        {
            Assert.Equal("O. Henry", AuthorCredits.Primary(new List<string> { "O. Henry", "William Sydney Porter" }));
        }

        [Fact]
        public void Primary_ReturnsNothingWhenThereIsNothingToChoose()
        {
            Assert.Null(AuthorCredits.Primary(new List<string>()));
            Assert.Null(AuthorCredits.Primary(null));
        }
    }
}
