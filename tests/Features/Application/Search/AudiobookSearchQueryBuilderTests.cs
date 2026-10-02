using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Search
{
    [Trait("Name", "AudiobookSearchQueryBuilderTests")]
    [Trait("Category", "AudiobookSearchQueryBuilder")]
    public sealed class AudiobookSearchQueryBuilderTests : BaseTests
    {
        private static Audiobook Book(string? title, string? author = null, string? series = null)
        {
            return new Audiobook
            {
                Title = title,
                Authors = author == null ? null : new List<string> { author },
                Series = series
            };
        }

        [Fact]
        [Trait("Method", "Build")]
        public void Build_BothEntryPointsProduceTheSameQuery()
        {
            // The automatic sweep and the download path used to build queries from
            // different field sets. Whatever the shared builder decides, they agree.
            var audiobook = Book("The Wonderful Wizard of Oz", "L. Frank Baum", "Oz");
            var classifier = new AutomaticSearchResultClassifier(
                Mock.Of<ILogger>());

            var automaticQuery = classifier.BuildSearchQuery(audiobook);
            var downloadQuery = DownloadSearchQueryBuilder.Build(audiobook);

            Assert.Equal(automaticQuery, downloadQuery);
            Assert.Equal("The Wonderful Wizard of Oz L. Frank Baum", downloadQuery);
        }

        [Fact]
        [Trait("Method", "Build")]
        public void Build_BothEntryPointsAgreeWhenTheSeriesIsNotInTheTitle()
        {
            // The agreement must not be an accident of one example. A series the title
            // does not mention is still left out, and both paths say the same thing.
            var audiobook = Book("Dracula", "Bram Stoker", "Gothic Horror");
            var classifier = new AutomaticSearchResultClassifier(
                Mock.Of<ILogger>());

            var automaticQuery = classifier.BuildSearchQuery(audiobook);
            var downloadQuery = DownloadSearchQueryBuilder.Build(audiobook);

            Assert.Equal(automaticQuery, downloadQuery);
            Assert.Equal("Dracula Bram Stoker", downloadQuery);
        }

        [Theory]
        [Trait("Method", "Build")]
        // Following Readarr, whose BookSearchCriteria has no series field, the series
        // never enters the query, whether or not the title already contains it.
        [InlineData("The Wonderful Wizard of Oz", "L. Frank Baum", "Oz", "The Wonderful Wizard of Oz L. Frank Baum")]
        [InlineData("Ozymandias", "Percy Bysshe Shelley", "Oz", "Ozymandias Percy Bysshe Shelley")]
        [InlineData("Dracula", "Bram Stoker", "Gothic Horror", "Dracula Bram Stoker")]
        public void Build_NeverAppendsTheSeries(string title, string author, string series, string expected)
        {
            Assert.Equal(expected, AudiobookSearchQueryBuilder.Build(Book(title, author, series)));
        }

        [Fact]
        [Trait("Method", "Build")]
        public void Build_StripsAnEditionAnnotationFromTheTitle()
        {
            var query = AudiobookSearchQueryBuilder.Build(
                Book("The Marvelous Land of Oz (Unabridged)", "L. Frank Baum", "Oz"));

            Assert.Equal("The Marvelous Land of Oz L. Frank Baum", query);
        }

        [Fact]
        [Trait("Method", "Build")]
        public void Build_OmitsMissingFieldsWithoutLeavingSeparators()
        {
            Assert.Equal("Frankenstein", AudiobookSearchQueryBuilder.Build(Book("Frankenstein")));
            Assert.Equal(
                "Frankenstein Mary Shelley",
                AudiobookSearchQueryBuilder.Build(Book("Frankenstein", "Mary Shelley", "   ")));
            Assert.Equal(
                "Frankenstein",
                AudiobookSearchQueryBuilder.Build(Book("Frankenstein", "   ")));
            Assert.Equal(string.Empty, AudiobookSearchQueryBuilder.Build(Book(null)));
        }

        [Theory]
        [Trait("Method", "BuildQueryTitle")]
        [InlineData("Moby Dick (Unabridged)", "Moby Dick")]
        [InlineData("Moby Dick [Unabridged]", "Moby Dick")]
        [InlineData("Moby Dick (unabridged)", "Moby Dick")]
        [InlineData("Moby Dick (Abridged)", "Moby Dick")]
        [InlineData("Moby Dick (Unabridged Edition)", "Moby Dick")]
        [InlineData("Moby Dick (Dramatized Adaptation)", "Moby Dick")]
        [InlineData("Moby Dick (Dramatised Adaptation)", "Moby Dick")]
        [InlineData("Moby Dick (Audio Drama)", "Moby Dick")]
        [InlineData("Moby Dick (Unabridged) (Dramatized)", "Moby Dick")]
        public void BuildQueryTitle_RemovesDelimitedEditionAnnotations(string title, string expected)
        {
            Assert.Equal(expected, AudiobookSearchQueryBuilder.BuildQueryTitle(title));
        }

        [Theory]
        [Trait("Method", "BuildQueryTitle")]
        // A part number says which half of the work is wanted. Removing it would turn a
        // search for one release into a search for either, so it stays.
        [InlineData("Les Miserables (Part 1 of 5)")]
        [InlineData("The Marvelous Land of Oz, Book 2")]
        // Delimited, but not an edition annotation. These are title words.
        [InlineData("Hamlet (Prince of Denmark)")]
        [InlineData("The Rime of the Ancient Mariner (1834 Text)")]
        // No delimiters, so nothing is a candidate for removal at all.
        [InlineData("Unabridged Dictionary of the English Language")]
        [InlineData("An Abridged History of Rome")]
        public void BuildQueryTitle_KeepsTitleTextItMustNotRemove(string title)
        {
            Assert.Equal(title, AudiobookSearchQueryBuilder.BuildQueryTitle(title));
        }

        [Fact]
        [Trait("Method", "BuildQueryTitle")]
        public void BuildQueryTitle_KeepsTheStoredTitleWhenStrippingWouldEmptyIt()
        {
            Assert.Equal("(Unabridged)", AudiobookSearchQueryBuilder.BuildQueryTitle("(Unabridged)"));
        }

        [Fact]
        [Trait("Method", "BuildQueryTitle")]
        public void BuildQueryTitle_CollapsesWhitespaceLeftBehindByAStrippedAnnotation()
        {
            Assert.Equal(
                "Twenty Thousand Leagues Under the Sea",
                AudiobookSearchQueryBuilder.BuildQueryTitle(
                    "Twenty Thousand Leagues  (Unabridged) Under the Sea"));
            Assert.Equal(
                "Treasure Island",
                AudiobookSearchQueryBuilder.BuildQueryTitle("Treasure Island, (Unabridged)"));
        }

        [Fact]
        [Trait("Method", "BuildQueryTitle")]
        public void BuildQueryTitle_ReturnsEmptyForAnAbsentTitle()
        {
            Assert.Equal(string.Empty, AudiobookSearchQueryBuilder.BuildQueryTitle(null));
            Assert.Equal(string.Empty, AudiobookSearchQueryBuilder.BuildQueryTitle("   "));
        }
    }
}
