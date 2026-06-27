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

using Microsoft.Extensions.Logging.Abstractions;
using System.Net;

namespace Listenarr.Tests.Features.Api.Services.Search.Providers
{
    public class ZLibrarySearchProviderTests
    {
        // Representative captured HTML snippet reflecting Z-Library result structure.
        // Each result is a book card (.resItemBox) with a link to /book/{id}/{hash},
        // author in .authors, and property values for format, language, size.
        private const string SampleHtml = """
            <!DOCTYPE html>
            <html>
            <body>
            <div class="catalog">
              <div class="resItemBox">
                <h3 itemprop="name"><a href="/book/12345/abcdefgh">Dune</a></h3>
                <div class="authors"><a href="/author/frank-herbert">Frank Herbert</a></div>
                <div class="property"><div class="property_value">mp3</div></div>
                <div class="property"><div class="property_value">English</div></div>
                <div class="property"><div class="property_value">156 MB</div></div>
              </div>
              <div class="resItemBox">
                <h3 itemprop="name"><a href="/book/67890/ijklmnop">Foundation</a></h3>
                <div class="authors"><a href="/author/isaac-asimov">Isaac Asimov</a></div>
                <div class="property"><div class="property_value">m4b</div></div>
                <div class="property"><div class="property_value">English</div></div>
                <div class="property"><div class="property_value">234 MB</div></div>
              </div>
            </div>
            </body>
            </html>
            """;

        private static Indexer MakeIndexer(int id = 1, string? cookie = null) => new()
        {
            Id = id,
            Name = "Z-Library Test",
            Url = "https://z-lib.id",
            ApiKey = cookie ?? "",
            Implementation = "ZLibrary",
            Type = "Usenet",
            IsEnabled = true,
        };

        [Fact]
        public void ParseSearchResults_Extracts_Title_Author_Format_Size_Language()
        {
            var provider = new ZLibrarySearchProvider(new HttpClient(), NullLogger<ZLibrarySearchProvider>.Instance);
            var indexer = MakeIndexer();

            var results = provider.ParseSearchResults(SampleHtml, "https://z-lib.id", indexer);

            Assert.Equal(2, results.Count);

            var first = results[0];
            Assert.Equal("Dune", first.Title);
            Assert.Equal("Frank Herbert", first.Artist);
            Assert.Equal("MP3", first.Format);
            Assert.Equal("English", first.Language);
            // 156 MB = 156 × 1_048_576 = 163_577_856
            Assert.InRange(first.Size, 163_000_000L, 164_000_000L);

            var second = results[1];
            Assert.Equal("Foundation", second.Title);
            Assert.Equal("Isaac Asimov", second.Artist);
            Assert.Equal("M4B", second.Format);
        }

        [Fact]
        public void ParseSearchResults_Builds_Correct_DDL_Urls()
        {
            var provider = new ZLibrarySearchProvider(new HttpClient(), NullLogger<ZLibrarySearchProvider>.Instance);
            var indexer = MakeIndexer(7);

            var results = provider.ParseSearchResults(SampleHtml, "https://z-lib.id", indexer);

            Assert.NotEmpty(results);
            var first = results[0];
            Assert.Equal("DDL", first.DownloadType);
            Assert.Equal("https://z-lib.id/book/12345/abcdefgh", first.ResultUrl);
            Assert.Equal("https://z-lib.id/dl/12345/abcdefgh", first.TorrentUrl);
            Assert.Equal(7, first.IndexerId);
            Assert.Equal("ZLibrary", first.IndexerImplementation);
        }

        [Fact]
        public void ParseSearchResults_Returns_Empty_On_Malformed_Html()
        {
            var provider = new ZLibrarySearchProvider(new HttpClient(), NullLogger<ZLibrarySearchProvider>.Instance);
            var results = provider.ParseSearchResults("<html><body><p>no books here</p></body></html>", "https://z-lib.id", MakeIndexer());
            Assert.Empty(results);
        }

        [Fact]
        public void ParseSearchResults_Returns_Empty_On_Empty_Html()
        {
            var provider = new ZLibrarySearchProvider(new HttpClient(), NullLogger<ZLibrarySearchProvider>.Instance);
            var results = provider.ParseSearchResults(string.Empty, "https://z-lib.id", MakeIndexer());
            Assert.Empty(results);
        }

        [Fact]
        public void ParseSearchResults_Deduplicates_Same_DetailUrl()
        {
            var html = """
                <html><body>
                  <div class="resItemBox">
                    <h3><a href="/book/99999/zzzzzzz">Same Book</a></h3>
                  </div>
                  <div class="resItemBox">
                    <h3><a href="/book/99999/zzzzzzz">Same Book</a></h3>
                  </div>
                </body></html>
                """;

            var provider = new ZLibrarySearchProvider(new HttpClient(), NullLogger<ZLibrarySearchProvider>.Instance);
            var results = provider.ParseSearchResults(html, "https://z-lib.id", MakeIndexer());

            Assert.Single(results);
        }

        [Theory]
        [InlineData("/book/12345/abcdefgh", "https://z-lib.id", "https://z-lib.id/dl/12345/abcdefgh")]
        [InlineData("/book/99999/xyzxyzxyz", "https://z-library.sk", "https://z-library.sk/dl/99999/xyzxyzxyz")]
        [InlineData("https://z-lib.id/book/12345/abcdefgh", "https://z-lib.id", "https://z-lib.id/dl/12345/abcdefgh")]
        [InlineData("", "https://z-lib.id", "")]
        [InlineData("/no-book-path/here", "https://z-lib.id", "")]
        public void BuildDownloadUrl_Constructs_Dl_Path_Correctly(string bookHref, string baseUrl, string expectedDownloadUrl)
        {
            var result = ZLibrarySearchProvider.BuildDownloadUrl(baseUrl, bookHref);
            Assert.Equal(expectedDownloadUrl, result);
        }

        [Fact]
        public async Task SearchAsync_Returns_Empty_When_Url_Is_Blank()
        {
            var provider = new ZLibrarySearchProvider(new HttpClient(), NullLogger<ZLibrarySearchProvider>.Instance);
            var indexer = MakeIndexer();
            indexer.Url = "";

            var results = await provider.SearchAsync(indexer, "dune");

            Assert.Empty(results);
        }

        [Fact]
        public async Task SearchAsync_Returns_Empty_On_Http_Failure()
        {
            var handler = new DelegatingHandlerStub(_ =>
                new HttpResponseMessage(HttpStatusCode.Forbidden));

            using var client = new HttpClient(handler);
            var provider = new ZLibrarySearchProvider(client, NullLogger<ZLibrarySearchProvider>.Instance);

            var results = await provider.SearchAsync(MakeIndexer(), "foundation");

            Assert.Empty(results);
        }

        [Fact]
        public async Task SearchAsync_Sends_Cookie_Header_When_ApiKey_Set()
        {
            string? capturedCookie = null;
            var handler = new DelegatingHandlerStub(req =>
            {
                capturedCookie = req.Headers.TryGetValues("Cookie", out var vals) ? string.Join(";", vals) : null;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new System.Net.Http.StringContent("<html><body></body></html>") };
            });

            using var client = new HttpClient(handler);
            var provider = new ZLibrarySearchProvider(client, NullLogger<ZLibrarySearchProvider>.Instance);
            var indexer = MakeIndexer(cookie: "remix_userid=12345; remix_userkey=abc");

            await provider.SearchAsync(indexer, "dune");

            Assert.NotNull(capturedCookie);
            Assert.Contains("remix_userid=12345", capturedCookie);
        }

        [Fact]
        public async Task SearchAsync_Omits_Cookie_Header_When_ApiKey_Blank()
        {
            bool cookiePresent = false;
            var handler = new DelegatingHandlerStub(req =>
            {
                cookiePresent = req.Headers.Contains("Cookie");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new System.Net.Http.StringContent("<html><body></body></html>") };
            });

            using var client = new HttpClient(handler);
            var provider = new ZLibrarySearchProvider(client, NullLogger<ZLibrarySearchProvider>.Instance);
            var indexer = MakeIndexer();  // no cookie

            await provider.SearchAsync(indexer, "dune");

            Assert.False(cookiePresent);
        }

        private class DelegatingHandlerStub : DelegatingHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
            public DelegatingHandlerStub(Func<HttpRequestMessage, HttpResponseMessage> responder)
            {
                _responder = responder ?? throw new ArgumentNullException(nameof(responder));
            }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(_responder(request));
        }
    }
}
