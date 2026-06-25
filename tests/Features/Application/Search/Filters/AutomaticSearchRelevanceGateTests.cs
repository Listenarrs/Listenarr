using Listenarr.Tests.Common;
using Listenarr.Application.Search.Filters;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Search.Filters
{
    [Trait("Name", "AutomaticSearchRelevanceGateTests")]
    [Trait("Category", "Search")]
    public class AutomaticSearchRelevanceGateTests : BaseTests
    {
        private const string GoodOmensStoredTitle = "Good Omens: The Nice and Accurate Prophecies of Agnes Nutter, Witch";
        private const string CopperfieldStoredTitle = "David Copperfield: The Personal History, Adventures, Experience and Observation of David Copperfield the Younger";

        private static AutomaticSearchRelevanceGate NewGate() => new(NullLogger<AutomaticSearchRelevanceGate>.Instance);

        private static Audiobook Book(string title, params string[] authors) =>
            new() { Title = title, Authors = authors.ToList() };

        private static RelevanceVerdict Judge(string resultTitle, string? resultArtist, string audiobookTitle, params string[] authors) =>
            AutomaticSearchRelevanceGate.Evaluate(resultTitle, resultArtist, audiobookTitle, authors);

        [Fact]
        public void Evaluate_PattersonConcert_IsRejectedOnTitle()
        {
            // Live false positive: a concert recording sharing only the surname "Patterson".
            var verdict = Judge("Patterson Hood - Live at Largo (2019)", null,
                "Dog Diaries: A Middle School Story", "James Patterson", "Steven Butler");

            Assert.False(verdict.IsRelevant);
            Assert.Equal(AutomaticSearchRelevanceGate.TitleRejectionReason, verdict.Reason);
            Assert.Equal(0.0, verdict.MainTitleRelevance);
            Assert.True(verdict.AuthorCorroborated); // the surname alone corroborates, but never substitutes for the title
        }

        [Theory]
        [InlineData("David Copperfield - Charles Dickens (Unabridged) [MP3]")]
        [InlineData("Charles Dickens - David Copperfield (Audiobook, 2020)")]
        public void Evaluate_DavidCopperfield_EitherNamingOrder_IsRelevant(string resultTitle)
        {
            Assert.True(Judge(resultTitle, null, "David Copperfield", "Charles Dickens").IsRelevant);
        }

        [Fact]
        public void ComputeTitleRelevance_PunctuationAndCaseInsensitive()
        {
            var relevance = AutomaticSearchRelevanceGate.ComputeTitleRelevance(
                resultTitle: "MISTBORN: THE FINAL EMPIRE — Brandon Sanderson",
                resultArtist: null,
                audiobookTitle: "Mistborn - The Final Empire");

            Assert.Equal(1.0, relevance);
        }

        [Fact]
        public void Evaluate_NoSignificantAudiobookTokens_FailsOpen()
        {
            // Stop-word-only audiobook side cannot be judged — do not reject.
            var verdict = AutomaticSearchRelevanceGate.Evaluate("Some Random Result", null, "The And", authors: null);

            Assert.True(verdict.IsRelevant);
            Assert.Equal(1.0, verdict.FullTitleRelevance);
        }

        [Fact]
        public void IsAuthorCorroborated_StructuredArtistField_Counts()
        {
            // MyAnonamouse returns the author in Artist, not inside Title.
            Assert.True(AutomaticSearchRelevanceGate.IsAuthorCorroborated("Dune", "Frank Herbert", new[] { "Frank Herbert" }));
            Assert.False(AutomaticSearchRelevanceGate.IsAuthorCorroborated("Dune", "", new[] { "Frank Herbert" }));
        }

        [Fact]
        public void IsRelevant_BelowThreshold_Rejects()
        {
            var result = new SearchResult { Title = "Patterson Hood - Live at Largo" };

            Assert.False(NewGate().IsRelevant(result, Book("Dog Diaries", "James Patterson")));
        }

        [Fact]
        public void IsRelevant_AboveThreshold_Keeps()
        {
            var result = new SearchResult { Title = "Charles Dickens - David Copperfield (Audiobook)" };

            Assert.True(NewGate().IsRelevant(result, Book("David Copperfield", "Charles Dickens")));
        }

        [Theory]
        // Review regressions (#591): exact title/author matches whose titles contain words the
        // metadata-page filters treat as product-like ("ring") or promotional ("search"). The
        // gate judges relevance only, so these must pass.
        [InlineData("J. R. R. Tolkien - The Lord of the Rings [M4B]", "", "The Lord of the Rings", "J. R. R. Tolkien")]
        [InlineData("Viktor Frankl - Man's Search for Meaning [M4B]", "", "Man's Search for Meaning", "Viktor E. Frankl")]
        // Provider returns title and author separately (MyAnonamouse author_info).
        [InlineData("Dune", "Frank Herbert", "Dune", "Frank Herbert")]
        // Control: author embedded in the title.
        [InlineData("Frank Herbert - Dune [M4B]", "", "Dune", "Frank Herbert")]
        // Long stored subtitle must not dilute a release that names the main title.
        [InlineData("Good Omens [M4B]", "", GoodOmensStoredTitle, "Neil Gaiman")]
        [InlineData("Good Omens", "Neil Gaiman & Terry Pratchett", GoodOmensStoredTitle, "Neil Gaiman")]
        [InlineData("Charles Dickens - David Copperfield [M4B]", "", CopperfieldStoredTitle, "Charles Dickens")]
        // Apostrophe-omitted release names, as produced by the indexer query sanitizer
        // ("Ender's Game" is searched as "Enders Game"); straight and curly controls.
        [InlineData("Enders Game [M4B]", "", "Ender's Game", "Orson Scott Card")]
        [InlineData("Ender's Game [M4B]", "", "Ender's Game", "Orson Scott Card")]
        [InlineData("Ender\u2019s Game [M4B]", "", "Ender\u2019s Game", "Orson Scott Card")]
        [InlineData("Enders Game [M4B]", "", "Ender\u2019s Game", "Orson Scott Card")]
        // Native-script title and author.
        [InlineData("Война и мир [M4B]", "", "Война и мир", "Лев Толстой")]
        [InlineData("Лев Толстой - Война и мир", "", "Война и мир", "Лев Толстой")]
        // NFC stored title vs NFD release title, and the reverse ("é" vs "e" + U+0301).
        [InlineData("Les Mise\u0301rables [M4B]", "", "Les Misérables", "Victor Hugo")]
        [InlineData("Les Misérables [M4B]", "", "Les Mise\u0301rables", "Victor Hugo")]
        public void IsRelevant_ExactMatches_AreKept(string resultTitle, string resultArtist, string audiobookTitle, string author)
        {
            var result = new SearchResult { Title = resultTitle, Artist = resultArtist };

            Assert.True(NewGate().IsRelevant(result, Book(audiobookTitle, author)));
        }

        [Theory]
        // A different book by the same author must not pass on the author's name alone, however
        // long the stored title is.
        [InlineData("Charles Dickens - Oliver Twist [M4B]", "", CopperfieldStoredTitle, "Charles Dickens")]
        [InlineData("Oliver Twist", "Charles Dickens", "David Copperfield", "Charles Dickens")]
        [InlineData("Лев Толстой - Анна Каренина [M4B]", "", "Война и мир", "Лев Толстой")]
        [InlineData("Neil Gaiman - Neverwhere [M4B]", "", GoodOmensStoredTitle, "Neil Gaiman")]
        [InlineData("Orson Scott Card - Speaker for the Dead [M4B]", "", "Ender's Game", "Orson Scott Card")]
        public void IsRelevant_SameAuthorDifferentBook_IsRejected(string resultTitle, string resultArtist, string audiobookTitle, string author)
        {
            var verdict = AutomaticSearchRelevanceGate.Evaluate(resultTitle, resultArtist, audiobookTitle, new[] { author });

            Assert.False(verdict.IsRelevant);
            Assert.Equal(AutomaticSearchRelevanceGate.TitleRejectionReason, verdict.Reason);
            Assert.True(verdict.AuthorCorroborated);
        }

        [Theory]
        // A generic title with several credited contributors: any one of them corroborates.
        [InlineData("Stories", "Neil Gaiman")]
        [InlineData("Stories - Neil Gaiman & Al Sarrantonio (eds) [M4B]", "")]
        [InlineData("Stories", "Jodi Picoult")]
        public void IsRelevant_GenericTitle_AnyOfSeveralCreditedAuthors_Corroborates(string resultTitle, string resultArtist)
        {
            var verdict = Judge(resultTitle, resultArtist, "Stories",
                "Neil Gaiman", "Al Sarrantonio", "Joe Hill", "Jodi Picoult", "Michael Moorcock", "Joanne Harris");

            Assert.True(verdict.IsRelevant);
            Assert.Equal(1.0, verdict.MainTitleRelevance);
            Assert.True(verdict.AuthorCorroborated);
        }

        [Theory]
        // NFC/NFD equivalence must hold for the structured artist field too.
        [InlineData("Antoine de Saint-Exupéry", "Antoine de Saint-Exupe\u0301ry")]
        [InlineData("Antoine de Saint-Exupe\u0301ry", "Antoine de Saint-Exupéry")]
        [InlineData("Antoine de Saint-Exupéry", "Antoine de Saint-Exupéry")]
        public void IsRelevant_GenericTitle_ArtistFieldInEitherUnicodeForm_Corroborates(string storedAuthor, string resultArtist)
        {
            var result = new SearchResult { Title = "Prince", Artist = resultArtist };

            Assert.True(NewGate().IsRelevant(result, Book("Prince", storedAuthor)));
            Assert.False(NewGate().IsRelevant(new SearchResult { Title = "Prince" }, Book("Prince", storedAuthor)));
        }

        [Theory]
        // Live false positives: a generic single-word title matched only on the title word inside a
        // longer, different book's title — with no author overlap — must be rejected.
        [InlineData("Mark Johnson - Wasted: A Childhood Stolen, An Innocence Betrayed, A Life Redeemed",
            "Betrayed", "Lindsay Buroker")]
        [InlineData("Entrepreneurial Bootcamp 03 - The Seven Nuts & Bolts of the Pent Family Vision - Arnold Pent",
            "The Vision", "Dean Koontz")]
        public void IsRelevant_GenericTitleWordOnly_NoAuthorOverlap_IsRejected(
            string resultTitle, string audiobookTitle, string author)
        {
            var verdict = Judge(resultTitle, null, audiobookTitle, author);

            // The title word itself is present, so the rejection is for the missing author.
            Assert.Equal(1.0, verdict.MainTitleRelevance);
            Assert.False(verdict.AuthorCorroborated);
            Assert.False(verdict.IsRelevant);
            Assert.Equal(AutomaticSearchRelevanceGate.AuthorRejectionReason, verdict.Reason);
        }

        [Fact]
        public void IsRelevant_GenericTitle_WithAuthorInTitle_IsKept()
        {
            var result = new SearchResult { Title = "Betrayed - Lindsay Buroker (Unabridged) [M4B]" };

            Assert.True(NewGate().IsRelevant(result, Book("Betrayed", "Lindsay Buroker")));
        }

        [Fact]
        public void IsRelevant_GenericTitle_WithAuthorInArtistField_IsKept()
        {
            // Corroboration must accept the structured author field, not only the title text.
            var result = new SearchResult { Title = "Betrayed", Artist = "Lindsay Buroker" };

            Assert.True(NewGate().IsRelevant(result, Book("Betrayed", "Lindsay Buroker")));
        }

        [Fact]
        public void IsRelevant_DistinctiveMultiWordTitle_AuthorOmitted_IsKept()
        {
            // The main title "Mistborn" is a single word, but the full title is distinctive, so an
            // author-less release that names all of it passes without corroboration.
            var result = new SearchResult { Title = "Mistborn - The Final Empire (Unabridged) [M4B]" };

            Assert.True(NewGate().IsRelevant(result, Book("Mistborn: The Final Empire", "Brandon Sanderson")));
        }

        [Theory]
        // Review round 5 (#591): a generic subtitle must not let a single shared title word bypass
        // the author check — "Dune: A Novel" is judged exactly like "Dune". The one shared word is
        // "dune" (author reason) or nothing at all once "novel"/"edition" are discounted (title reason).
        [InlineData("Dune", "John Doe", "Dune: A Novel", AutomaticSearchRelevanceGate.AuthorRejectionReason)]
        [InlineData("Dune: A Novel", "John Doe", "Dune: A Novel", AutomaticSearchRelevanceGate.AuthorRejectionReason)]
        [InlineData("Dune (Unabridged Edition)", "", "Dune: A Novel", AutomaticSearchRelevanceGate.AuthorRejectionReason)]
        [InlineData("The Shining: A Novel", "Stephen King", "Dune: A Novel", AutomaticSearchRelevanceGate.TitleRejectionReason)]
        [InlineData("Stephen King - The Shining: A Novel [M4B]", "", "Dune: A Novel", AutomaticSearchRelevanceGate.TitleRejectionReason)]
        [InlineData("The Shining: A Novel, Unabridged Edition", "Stephen King", "Dune: A Novel, Unabridged Edition", AutomaticSearchRelevanceGate.TitleRejectionReason)]
        public void IsRelevant_GenericSubtitle_DoesNotBypassAuthorCheck(
            string resultTitle, string resultArtist, string audiobookTitle, string expectedReason)
        {
            var verdict = Judge(resultTitle, resultArtist, audiobookTitle, "Frank Herbert");

            Assert.False(verdict.IsRelevant);
            Assert.Equal(expectedReason, verdict.Reason);
            Assert.False(verdict.AuthorCorroborated);
        }

        [Theory]
        // The same stored title with the author present passes, in either field.
        [InlineData("Dune", "Frank Herbert")]
        [InlineData("Dune: A Novel", "Frank Herbert")]
        [InlineData("Frank Herbert - Dune [M4B]", "")]
        public void IsRelevant_GenericSubtitle_AuthorCorroborated_IsKept(string resultTitle, string resultArtist)
        {
            Assert.True(Judge(resultTitle, resultArtist, "Dune: A Novel", "Frank Herbert").IsRelevant);
        }

        [Fact]
        public void IsRelevant_GenericSubtitle_NoKnownAuthor_FailsOpen()
        {
            Assert.True(AutomaticSearchRelevanceGate.Evaluate("Dune", "John Doe", "Dune: A Novel", authors: null).IsRelevant);
        }

        [Theory]
        // A match on several title words is meaningful on its own: the subtitle half of
        // "Mistborn: The Final Empire" still passes without the author, as before.
        [InlineData("The Final Empire [M4B]", "")]
        [InlineData("The Final Empire (Unabridged Edition)", "")]
        [InlineData("Mistborn: The Final Empire", "")]
        public void IsRelevant_MeaningfulSubtitleMatch_AuthorOmitted_IsKept(string resultTitle, string resultArtist)
        {
            Assert.True(Judge(resultTitle, resultArtist, "Mistborn: The Final Empire", "Brandon Sanderson").IsRelevant);
        }

        [Fact]
        public void IsRelevant_GenericTitle_NoKnownAuthor_FailsOpen()
        {
            // With no author to corroborate against, the guard cannot judge and does not reject.
            var result = new SearchResult { Title = "Some Other Story That Mentions Betrayed Somewhere" };
            var audiobook = new Audiobook { Title = "Betrayed", Authors = null };

            Assert.True(NewGate().IsRelevant(result, audiobook));
        }

        [Theory]
        [InlineData("Mistborn: The Final Empire", "Mistborn")]
        [InlineData(GoodOmensStoredTitle, "Good Omens")]
        [InlineData("Dog Diaries - A Middle School Story", "Dog Diaries")]
        [InlineData("Harry Potter and the Sorcerer's Stone (Book 1)", "Harry Potter and the Sorcerer's Stone")]
        [InlineData("The Lord of the Rings", "The Lord of the Rings")]
        [InlineData(": Only a Subtitle", ": Only a Subtitle")]
        [InlineData("Full-Cast Drama", "Full-Cast Drama")] // a hyphen without spaces is not a subtitle separator
        public void MainTitle_StripsSubtitle(string stored, string expected)
        {
            Assert.Equal(expected, AutomaticSearchRelevanceGate.MainTitle(stored));
        }

        [Fact]
        public void Apply_KeepsRelevantResultsInOrder_AndDropsTheRest()
        {
            var relevant = new SearchResult { Title = "James Patterson - Dog Diaries [M4B]" };
            var concert = new SearchResult { Title = "Patterson Hood - Live at Largo" };
            var split = new SearchResult { Title = "Dog Diaries", Artist = "James Patterson" };

            var kept = NewGate().Apply(new List<SearchResult> { concert, relevant, split }, Book("Dog Diaries", "James Patterson"));

            Assert.Equal(new[] { relevant, split }, kept);
        }

        [Fact]
        public void Apply_MixedResults_SameAuthorDecoyIsDropped_CorrectReleaseKept()
        {
            var decoy = new SearchResult { Title = "Charles Dickens - Oliver Twist [M4B]" };
            var correct = new SearchResult { Title = "Charles Dickens - David Copperfield [M4B]" };

            var kept = NewGate().Apply(new List<SearchResult> { decoy, correct }, Book(CopperfieldStoredTitle, "Charles Dickens"));

            Assert.Equal(new[] { correct }, kept);
        }

        [Fact]
        public void SignificantTokens_DropsStopWordsAndShortTokens()
        {
            var tokens = SignificantTokens.From("The Lord of the Rings: The Fellowship of a Book").ToList();

            // "the", "of", "a", "book" are stop words; "lord", "rings", "fellowship" survive.
            Assert.DoesNotContain("the", tokens);
            Assert.DoesNotContain("of", tokens);
            Assert.DoesNotContain("a", tokens);
            Assert.DoesNotContain("book", tokens);
            Assert.Contains("lord", tokens);
            Assert.Contains("rings", tokens);
            Assert.Contains("fellowship", tokens);
        }

        [Theory]
        [InlineData("Ender's Game", "enders game")]
        [InlineData("Ender\u2019s Game", "enders game")]
        [InlineData("Ender\u2018s Game", "enders game")]
        [InlineData("Enders Game", "enders game")]
        [InlineData("Full-Cast Drama", "full cast drama")]
        [InlineData("Война и мир", "война мир")] // "и" is a single letter and is dropped
        [InlineData("日本語 オーディオ", "日本語 オーディオ")] // CJK letters are kept intact, including the prolonged-sound mark
        public void SignificantTokens_PreservesUnicodeWords_AndFoldsApostrophesAndHyphensLikeTheQuerySanitizer(string input, string expected)
        {
            Assert.Equal(expected.Split(' '), SignificantTokens.From(input).ToArray());
        }

        [Theory]
        [InlineData("Dune: A Novel", "dune")]
        [InlineData("Mistborn: The Final Empire (Unabridged Edition)", "mistborn final empire")]
        [InlineData("The Book Thief", "thief")]
        [InlineData("A Novel Obsession", "novel obsession")]
        [InlineData("A Novel Obsession: A Novel (Unabridged Edition)", "novel obsession")]
        [InlineData("The Novel", "novel")]
        [InlineData("The Novel Cure", "novel cure")]
        [InlineData("The Last Edition", "last edition")]
        [InlineData("An Abridged History", "abridged history")]
        [InlineData("Dune: A Novel, Unabridged Edition", "dune")]
        [InlineData("Dune: An Exploration of Novels", "dune exploration novels")]
        [InlineData("(Novel)", "novel")]
        [InlineData("Dune Unabridged Edition", "dune")]
        [InlineData("Dune A Novel Unabridged Edition", "dune")]
        [InlineData("Unabridged Edition", "unabridged edition")]
        public void SignificantTokens_DropsOnlySeparatedEditionDescriptors(string input, string expected)
        {
            Assert.Equal(expected.Split(' '), SignificantTokens.FromTitle(input).ToArray());
        }

        [Theory]
        [InlineData("A Novel Obsession", "Caitlin Barasch")]
        [InlineData("The Novel Cure", "Ella Berthoud")]
        [InlineData("The Last Edition", "Someone Else")]
        public void IsRelevant_MainTitleContainsEditionWord_ExactTitleOnlyIsKept(string title, string author)
        {
            Assert.True(Judge(title + " [M4B]", null, title, author).IsRelevant);
        }

        [Fact]
        public void IsRelevant_UnseparatedEditionMetadata_DoesNotSupplyBookIdentity()
        {
            Assert.False(Judge("The Shining Unabridged Edition", "Stephen King",
                "Dune Unabridged Edition", "Frank Herbert").IsRelevant);
        }

        [Fact]
        public void IsRelevant_TheNovel_UnrelatedReleaseIsRejected()
        {
            Assert.False(Judge("Unrelated Work [M4B]", "Other Person", "The Novel", "James A. Michener").IsRelevant);
            Assert.True(Judge("The Novel [M4B]", "James A. Michener", "The Novel", "James A. Michener").IsRelevant);
        }

        [Theory]
        [InlineData("Novel")]
        [InlineData("Unabridged")]
        [InlineData("Edition")]
        public void IsRelevant_AuthorPrefix_DoesNotTurnMainTitleIntoDescriptor(string title)
        {
            Assert.True(Judge("Some Writer - " + title, "Some Writer", title, "Some Writer").IsRelevant);
        }

        [Fact]
        public void IsRelevant_DashSeparatedDescriptors_DoNotReopenSubtitleBypass()
        {
            Assert.False(Judge("Other Writer - The Shining - A Novel - Unabridged Edition", "Other Writer",
                "Dune - A Novel - Unabridged Edition", "Frank Herbert").IsRelevant);
        }

        [Theory]
        [InlineData("A Novel Obsession", "Caitlin Barasch", "Obsession [M4B]", "Novel Writer")]
        [InlineData("Paper Trail", "Alice Morgan", "Unrelated Work [M4B]", "Paper Trail")]
        public void IsRelevant_ArtistDoesNotSupplyTitleEvidence(string title, string author, string release, string artist)
        {
            Assert.False(Judge(release, artist, title, author).IsRelevant);
        }

        [Fact]
        public void IsRelevant_DescriptorWordAuthorSuffix_IsNotDiscarded()
        {
            Assert.True(Judge("Dune - Novel", null, "Dune", "Novel").IsRelevant);
            Assert.True(Judge("Dune", "Novel", "Dune", "Novel").IsRelevant);
            Assert.True(Judge("Dune - Some Unabridged Edition", null, "Dune", "Some Unabridged Edition").IsRelevant);
        }

        [Fact]
        public void IsRelevant_SubtitleDescriptor_IsNotAuthorEvidence()
        {
            Assert.False(Judge("Dune: A Novel", "John Doe", "Dune", "Novel").IsRelevant);
        }

        [Theory]
        [InlineData(" - ")]
        [InlineData(" − ")]
        [InlineData("-")]
        [InlineData("−")]
        [InlineData("，")]
        public void IsRelevant_IdentifiedAuthorPrefix_DoesNotSupplyMissingTitleWord(string separator)
        {
            Assert.False(Judge("Novel Writer" + separator + "Obsession", "Novel Writer",
                "A Novel Obsession", "Caitlin Barasch").IsRelevant);
            Assert.Equal(1.0, AutomaticSearchRelevanceGate.ComputeTitleRelevance(
                "Novel Writer - A Novel Obsession", "Novel Writer", "A Novel Obsession"));
        }

        [Fact]
        public void IsRelevant_TitleFirstRelease_InferredArtistDoesNotDiscardTitleOrCredit()
        {
            Assert.True(Judge("David Copperfield - Charles Dickens", "David Copperfield",
                "David Copperfield", "Charles Dickens").IsRelevant);
            Assert.True(Judge("Dune - Novel", "Dune", "Dune", "Novel").IsRelevant);
        }

        [Fact]
        public void IsRelevant_CommaEditionSuffix_DoesNotHideAuthorPrefixedMainTitle()
        {
            Assert.True(Judge("Some Writer - Novel, Unabridged Edition", "Some Writer",
                "Novel, Unabridged Edition", "Some Writer").IsRelevant);
        }

        [Theory]
        [InlineData("Dune − A Novel − Unabridged Edition", "The Shining − A Novel − Unabridged Edition")]
        [InlineData("Dune：A Novel，Unabridged Edition", "The Shining：A Novel，Unabridged Edition")]
        [InlineData("Dune（A Novel）（Unabridged Edition）", "The Shining（A Novel）（Unabridged Edition）")]
        public void IsRelevant_UnicodeEditionSeparators_DoNotSupplyIdentity(string title, string release)
        {
            Assert.False(Judge(release, "Stephen King", title, "Frank Herbert").IsRelevant);
        }

        [Theory]
        [InlineData("Novel-Obsession")]
        [InlineData("Obsession-Novel")]
        public void IsRelevant_CompoundMainTitle_IsNotAnEditionSuffix(string title)
        {
            Assert.True(Judge(title, null, title, "Some Writer").IsRelevant);
        }

        [Theory]
        [InlineData("Misérables", "Mise\u0301rables")]          // NFC vs NFD
        [InlineData("Saint-Exupéry", "Saint-Exupe\u0301ry")]
        [InlineData("Zoë", "Zoe\u0308")]
        [InlineData("Åsa Larsson", "A\u030asa Larsson")]
        public void SignificantTokens_EquivalentUnicodeEncodings_ProduceIdenticalTokens(string composed, string decomposed)
        {
            Assert.NotEqual(composed, decomposed); // the inputs really differ byte-wise
            Assert.Equal(SignificantTokens.From(composed).ToArray(), SignificantTokens.From(decomposed).ToArray());
        }
    }
}
