using Listenarr.Application.Interfaces;
using Listenarr.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Listenarr.Tests.Features.Application.Common
{
    [Trait("Name", "NamingPatternServiceTests")]
    [Trait("Category", "NamingPatternService")]
    public class NamingPatternServiceTests : BaseTests
    {
        /// <summary>
        /// Empty patterns are valid for folder paths and should not synthesize an "Unknown" segment.
        /// </summary>
        [Fact]
        public void ApplyNamingPattern_EmptyPattern_ReturnsEmptyPath()
        {
            var service = _provider.GetRequiredService<INamingPatternService>();
            var variables = new Dictionary<string, object>
            {
                { "Author", "Author One" },
                { "Title", "Detail Book" }
            };

            var result = service.ApplyNamingPattern(string.Empty, variables);

            Assert.Equal(string.Empty, result);
            Assert.DoesNotContain("Unknown", result, StringComparison.OrdinalIgnoreCase);
        }
    }
}
