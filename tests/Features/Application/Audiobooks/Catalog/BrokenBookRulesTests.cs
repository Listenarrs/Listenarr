using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Audiobooks.Catalog
{
    [Trait("Name", "BrokenBookRulesTests")]
    [Trait("Category", "Application")]
    public sealed class BrokenBookRulesTests : BaseTests
    {
        private static readonly string[] Authors = ["Stephen King"];

        [Theory]
        [InlineData("B075ZTX494")]
        [InlineData("1713555042")]
        public void FindReasons_BookAudibleCanBeAskedFor_HasNone(string asin)
        {
            Assert.Empty(BrokenBookRules.FindReasons("Carrie", Authors, asin, "fr"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        public void FindReasons_NoTitle(string? title)
        {
            Assert.Equal(
                [BrokenBookReasons.MissingTitle],
                BrokenBookRules.FindReasons(title, Authors, "B075ZTX494", "fr"));
        }

        [Fact]
        public void FindReasons_NoAuthor()
        {
            string[] expected = [BrokenBookReasons.MissingAuthor];
            Assert.Equal(expected, BrokenBookRules.FindReasons("Carrie", null, "B075ZTX494", "fr"));
            Assert.Equal(expected, BrokenBookRules.FindReasons("Carrie", [], "B075ZTX494", "fr"));
            Assert.Equal(expected, BrokenBookRules.FindReasons("Carrie", [" ", null], "B075ZTX494", "fr"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData(" ")]
        public void FindReasons_NoAsin_RegionNotReported(string? asin)
        {
            Assert.Equal(
                [BrokenBookReasons.MissingAsin],
                BrokenBookRules.FindReasons("Carrie", Authors, asin, null));
        }

        [Theory]
        [InlineData("not an asin")]
        [InlineData("B075ZTX49")]
        public void FindReasons_NotAnAsin(string asin)
        {
            Assert.Equal(
                [BrokenBookReasons.InvalidAsin],
                BrokenBookRules.FindReasons("Carrie", Authors, asin, "fr"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void FindReasons_AsinWithoutRegion(string? region)
        {
            Assert.Equal(
                [BrokenBookReasons.MissingRegion],
                BrokenBookRules.FindReasons("Carrie", Authors, "B075ZTX494", region));
        }

        [Fact]
        public void FindReasons_SeveralProblems_ListsEach()
        {
            Assert.Equal(
                [BrokenBookReasons.MissingTitle, BrokenBookReasons.MissingAuthor, BrokenBookReasons.MissingAsin],
                BrokenBookRules.FindReasons("", [], null, null));
        }
    }
}
