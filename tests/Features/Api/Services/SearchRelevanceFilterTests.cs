using Listenarr.Application.Search.Filters;

namespace Listenarr.Tests.Features.Api.Services
{
    [Trait("Name", "SearchRelevanceFilterTests")]
    [Trait("Category", "Search")]
    public class SearchRelevanceFilterTests
    {
        [Fact]
        public void ComputeRelevance_PattersonHoodConcert_VsJamesPattersonBook_IsBelowThreshold()
        {
            // Real bug report: a Patterson Hood concert recording matched James Patterson's
            // "Dog Diaries" on the single shared "Patterson" surname.
            var relevance = RelevanceFilter.ComputeRelevance(
                resultTitle: "Patterson Hood - Live at Largo (2007) [FLAC]",
                audiobookTitle: "Dog Diaries: A Middle School Story",
                authors: new[] { "James Patterson", "Steven Butler" });

            Assert.True(relevance < RelevanceFilter.DefaultMinRelevance,
                $"Relevance {relevance:P0} should be below the {RelevanceFilter.DefaultMinRelevance:P0} threshold.");
        }

        [Fact]
        public void ComputeRelevance_DavidCopperfield_TitleFirstNaming_PassesThreshold()
        {
            var relevance = RelevanceFilter.ComputeRelevance(
                resultTitle: "David Copperfield - Charles Dickens (Unabridged) [MP3]",
                audiobookTitle: "David Copperfield",
                authors: new[] { "Charles Dickens" });

            Assert.True(relevance >= RelevanceFilter.DefaultMinRelevance);
        }

        [Fact]
        public void ComputeRelevance_DavidCopperfield_AuthorFirstNaming_PassesThreshold()
        {
            var relevance = RelevanceFilter.ComputeRelevance(
                resultTitle: "Charles Dickens - David Copperfield (Audiobook, 2020)",
                audiobookTitle: "David Copperfield",
                authors: new[] { "Charles Dickens" });

            Assert.True(relevance >= RelevanceFilter.DefaultMinRelevance);
        }

        [Fact]
        public void ComputeRelevance_PunctuationAndCaseInsensitive()
        {
            var relevance = RelevanceFilter.ComputeRelevance(
                resultTitle: "MISTBORN: THE FINAL EMPIRE — Brandon Sanderson",
                audiobookTitle: "Mistborn - The Final Empire",
                authors: new[] { "Brandon Sanderson" });

            Assert.Equal(1.0, relevance);
        }

        [Fact]
        public void ComputeRelevance_NoSignificantAudiobookTokens_FailsOpen()
        {
            // Stop-word-only audiobook side returns 1.0 — cannot judge, do not reject.
            var relevance = RelevanceFilter.ComputeRelevance(
                resultTitle: "Some Random Result",
                audiobookTitle: "The And",
                authors: null);

            Assert.Equal(1.0, relevance);
        }

        [Fact]
        public void ShouldFilter_NoAudiobookContext_FailsOpen()
        {
            var filter = new RelevanceFilter();
            var result = new SearchResult { Title = "Something Unrelated" };

            // Must return false without an audiobook — whether the argument is omitted
            // (defaulted) or passed explicitly null — so manual search keeps every result.
            Assert.False(filter.ShouldFilter(result));
            Assert.False(filter.ShouldFilter(result, audiobook: null));
        }

        [Fact]
        public void ShouldFilter_BelowThreshold_RejectsResult()
        {
            var filter = new RelevanceFilter();
            var result = new SearchResult { Title = "Patterson Hood - Live at Largo" };
            var ab = new Audiobook { Title = "Dog Diaries", Authors = new List<string> { "James Patterson" } };

            Assert.True(filter.ShouldFilter(result, ab));
        }

        [Fact]
        public void ShouldFilter_AboveThreshold_KeepsResult()
        {
            var filter = new RelevanceFilter();
            var result = new SearchResult { Title = "Charles Dickens - David Copperfield (Audiobook)" };
            var ab = new Audiobook { Title = "David Copperfield", Authors = new List<string> { "Charles Dickens" } };

            Assert.False(filter.ShouldFilter(result, ab));
        }

        [Theory]
        // Live false positives: a generic single-word title matched only on the title word inside a
        // longer, different book's title — with no author overlap — must now be rejected.
        [InlineData("Mark Johnson - Wasted: A Childhood Stolen, An Innocence Betrayed, A Life Redeemed",
            "Betrayed", "Lindsay Buroker")]
        [InlineData("Entrepreneurial Bootcamp 03 - The Seven Nuts & Bolts of the Pent Family Vision - Arnold Pent",
            "The Vision", "Dean Koontz")]
        public void ShouldFilter_GenericTitleWordOnly_NoAuthorOverlap_IsRejected(
            string resultTitle, string audiobookTitle, string author)
        {
            var filter = new RelevanceFilter();
            var result = new SearchResult { Title = resultTitle };
            var ab = new Audiobook { Title = audiobookTitle, Authors = new List<string> { author } };

            // The combined ratio alone clears 0.30 (1 of 3 tokens), but the author-corroboration
            // guard rejects it because no author token appears in the result.
            Assert.True(RelevanceFilter.ComputeRelevance(resultTitle, audiobookTitle, new[] { author })
                >= RelevanceFilter.DefaultMinRelevance);
            Assert.True(filter.ShouldFilter(result, ab));
        }

        [Fact]
        public void ShouldFilter_GenericTitle_WithAuthorPresent_IsKept()
        {
            // The correct release names the author, so corroboration passes.
            var filter = new RelevanceFilter();
            var result = new SearchResult { Title = "Betrayed - Lindsay Buroker (Unabridged) [M4B]" };
            var ab = new Audiobook { Title = "Betrayed", Authors = new List<string> { "Lindsay Buroker" } };

            Assert.False(filter.ShouldFilter(result, ab));
        }

        [Fact]
        public void ShouldFilter_DistinctiveMultiWordTitle_AuthorOmitted_IsKept()
        {
            // A multi-word distinctive title (more than MaxGenericTitleTokens significant tokens)
            // can stand on its own — the guard does not require the author, so an author-less
            // release still passes.
            var filter = new RelevanceFilter();
            var result = new SearchResult { Title = "Mistborn - The Final Empire (Unabridged) [M4B]" };
            var ab = new Audiobook { Title = "Mistborn: The Final Empire", Authors = new List<string> { "Brandon Sanderson" } };

            Assert.False(filter.ShouldFilter(result, ab));
        }

        [Fact]
        public void ShouldFilter_GenericTitle_NoKnownAuthor_FailsOpen()
        {
            // With no author to corroborate against, the guard cannot judge and does not reject.
            var filter = new RelevanceFilter();
            var result = new SearchResult { Title = "Some Other Story That Mentions Betrayed Somewhere" };
            var ab = new Audiobook { Title = "Betrayed", Authors = null };

            Assert.False(filter.ShouldFilter(result, ab));
        }

        [Theory]
        [InlineData("Mark Johnson - Wasted: An Innocence Betrayed", "Betrayed", "Lindsay Buroker", true)]
        [InlineData("Betrayed by Lindsay Buroker", "Betrayed", "Lindsay Buroker", false)] // author present
        [InlineData("Mistborn The Final Empire", "Mistborn The Final Empire", "Brandon Sanderson", false)] // distinctive
        public void RequiresAuthorCorroboration_Cases(
            string resultTitle, string audiobookTitle, string author, bool expected)
        {
            Assert.Equal(expected,
                RelevanceFilter.RequiresAuthorCorroboration(resultTitle, audiobookTitle, new[] { author }));
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
    }
}
