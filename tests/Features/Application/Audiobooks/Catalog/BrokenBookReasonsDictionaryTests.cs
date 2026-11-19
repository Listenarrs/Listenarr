using System.Text.RegularExpressions;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Audiobooks.Catalog
{
    [Trait("Name", "BrokenBookReasonsDictionaryTests")]
    [Trait("Category", "Application")]
    public sealed class BrokenBookReasonsDictionaryTests : BaseTests
    {
        // The front end shows a label for each code the server gives: both dictionaries must list the
        // same codes
        [Fact]
        public void FrontEndLabels_CoverExactlyTheServerReasons()
        {
            var labelsFile = Path.Combine(
                TestUtils.FindRepositoryRoot(),
                "fe",
                "src",
                "utils",
                "brokenBookReasons.ts");
            var source = File.ReadAllText(labelsFile);
            var dictionary = Regex.Match(
                source,
                @"BROKEN_REASON_LABELS = \{(?<entries>.*?)\}",
                RegexOptions.Singleline);
            Assert.True(dictionary.Success, "BROKEN_REASON_LABELS not found in brokenBookReasons.ts");

            var frontEndCodes = Regex.Matches(dictionary.Groups["entries"].Value, @"^\s*(\w+):", RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value)
                .Order()
                .ToArray();

            Assert.Equal(BrokenBookReasons.All.Order().ToArray(), frontEndCodes);
        }

        [Fact]
        public void FindReasons_OnlyGivesCodesFromTheDictionary()
        {
            var reasons = BrokenBookRules.FindReasons(null, null, null, null)
                .Concat(BrokenBookRules.FindReasons("Carrie", ["Stephen King"], "not an asin", null))
                .Concat(BrokenBookRules.FindReasons("Carrie", ["Stephen King"], "B075ZTX494", null));

            Assert.All(reasons, reason => Assert.Contains(reason, BrokenBookReasons.All));
        }
    }
}
