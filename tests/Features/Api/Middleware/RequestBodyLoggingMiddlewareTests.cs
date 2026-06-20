using Listenarr.Api.Middleware;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Middleware
{
    [Trait("Name", "RequestBodyLoggingMiddlewareTests")]
    [Trait("Category", "Middleware")]
    public class RequestBodyLoggingMiddlewareTests : BaseTests
    {
        [Fact]
        public void RedactSensitiveJsonFields_RemovesDownloadUrlQueryStrings()
        {
            const string body =
                """{"torrentUrl":"https://indexer.example.com/download?apikey=secret&link=value","resultUrl":"https://indexer.example.com/result?token=secret","magnetLink":"magnet:?xt=urn:btih:ABCDEF"}""";

            var redacted = RequestBodyLoggingMiddleware.RedactSensitiveJsonFields(body);

            Assert.Contains("\"torrentUrl\":\"https://indexer.example.com/download\"", redacted, StringComparison.Ordinal);
            Assert.Contains("\"resultUrl\":\"https://indexer.example.com/result\"", redacted, StringComparison.Ordinal);
            Assert.DoesNotContain("apikey", redacted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", redacted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", redacted, StringComparison.OrdinalIgnoreCase);
        }
    }
}
