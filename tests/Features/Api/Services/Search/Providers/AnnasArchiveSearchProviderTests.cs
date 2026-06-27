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
    public class AnnasArchiveSearchProviderTests
    {
        // Representative captured HTML snippet reflecting Anna's Archive result structure.
        // Each result is an <a> with href=/md5/{hash}, containing title (font-bold),
        // author (italic), and a metadata line with language [CODE], format, size.
        private const string SampleHtml = """
            <!DOCTYPE html>
            <html>
            <body>
            <div>
              <a class="js-vim-focus" href="/md5/abc123def456abc1abc1abc1abc1abc1">
                <div>
                  <div><span class="font-bold">Harry Potter and the Philosopher's Stone</span></div>
                  <div class="italic">J.K. Rowling</div>
                  <div><span>English [EN], MP3, 356.2MB, fiction</span></div>
                </div>
              </a>
              <a href="/md5/def789012345def9def9def9def9def9">
                <div>
                  <div><span class="font-bold">The Hobbit</span></div>
                  <div class="italic">J.R.R. Tolkien</div>
                  <div><span>English [EN], M4B, 234.5MB, fantasy</span></div>
                </div>
              </a>
            </div>
            </body>
            </html>
            """;

        private static Indexer MakeIndexer(int id = 1) => new()
        {
            Id = id,
            Name = "Anna's Archive Test",
            Url = "https://annas-archive.org",
            Implementation = "AnnasArchive",
            Type = "Usenet",
            IsEnabled = true,
        };

        [Fact]
        public void ParseSearchResults_Extracts_Title_Author_Format_Size_Language()
        {
            var provider = new AnnasArchiveSearchProvider(new HttpClient(), NullLogger<AnnasArchiveSearchProvider>.Instance);
            var indexer = MakeIndexer();

            var results = provider.ParseSearchResults(SampleHtml, "https://annas-archive.org", indexer);

            Assert.Equal(2, results.Count);

            var first = results[0];
            Assert.Equal("Harry Potter and the Philosopher's Stone", first.Title);
            Assert.Equal("J.K. Rowling", first.Artist);
            Assert.Equal("MP3", first.Format);
            Assert.Equal("English", first.Language);
            // 356.2 MB = 356.2 × 1_048_576 = 373_477_171 bytes (approximately)
            Assert.InRange(first.Size, 373_000_000L, 374_000_000L);

            var second = results[1];
            Assert.Equal("The Hobbit", second.Title);
            Assert.Equal("J.R.R. Tolkien", second.Artist);
            Assert.Equal("M4B", second.Format);
        }

        [Fact]
        public void ParseSearchResults_Builds_Correct_DDL_Urls()
        {
            var provider = new AnnasArchiveSearchProvider(new HttpClient(), NullLogger<AnnasArchiveSearchProvider>.Instance);
            var indexer = MakeIndexer(42);

            var results = provider.ParseSearchResults(SampleHtml, "https://annas-archive.org", indexer);

            Assert.NotEmpty(results);
            var first = results[0];
            Assert.Equal("DDL", first.DownloadType);
            Assert.Equal("https://annas-archive.org/fast-download/abc123def456abc1abc1abc1abc1abc1", first.TorrentUrl);
            Assert.Equal("https://annas-archive.org/md5/abc123def456abc1abc1abc1abc1abc1", first.ResultUrl);
            Assert.Equal(42, first.IndexerId);
            Assert.Equal("AnnasArchive", first.IndexerImplementation);
        }

        [Fact]
        public void ParseSearchResults_Deduplicates_Same_Md5()
        {
            var duplicateHtml = """
                <html><body>
                  <a href="/md5/aaabbbcccdddeeefffaaabbbcccdddeee">
                    <span class="font-bold">Duplicate Book</span>
                  </a>
                  <a href="/md5/aaabbbcccdddeeefffaaabbbcccdddeee">
                    <span class="font-bold">Duplicate Book</span>
                  </a>
                </body></html>
                """;

            var provider = new AnnasArchiveSearchProvider(new HttpClient(), NullLogger<AnnasArchiveSearchProvider>.Instance);
            var results = provider.ParseSearchResults(duplicateHtml, "https://annas-archive.org", MakeIndexer());

            Assert.Single(results);
        }

        [Fact]
        public void ParseSearchResults_Returns_Empty_On_Malformed_Html()
        {
            var provider = new AnnasArchiveSearchProvider(new HttpClient(), NullLogger<AnnasArchiveSearchProvider>.Instance);

            // Malformed HTML with no /md5/ links
            var results = provider.ParseSearchResults("<html><body><p>no results</p></body></html>", "https://annas-archive.org", MakeIndexer());

            Assert.Empty(results);
        }

        [Fact]
        public void ParseSearchResults_Returns_Empty_On_Empty_Html()
        {
            var provider = new AnnasArchiveSearchProvider(new HttpClient(), NullLogger<AnnasArchiveSearchProvider>.Instance);
            var results = provider.ParseSearchResults(string.Empty, "https://annas-archive.org", MakeIndexer());
            Assert.Empty(results);
        }

        [Fact]
        public void ParseSearchResults_Skips_Items_With_No_Title()
        {
            var html = """
                <html><body>
                  <a href="/md5/validmd5hash0000000000000000000000">
                    <!-- no title node -->
                    <div class="italic">Some Author</div>
                  </a>
                </body></html>
                """;

            var provider = new AnnasArchiveSearchProvider(new HttpClient(), NullLogger<AnnasArchiveSearchProvider>.Instance);
            var results = provider.ParseSearchResults(html, "https://annas-archive.org", MakeIndexer());

            Assert.Empty(results);
        }

        [Theory]
        [InlineData("English [EN], MP3, 356.2MB, fiction", "MP3", 373_477_171L, "English")]
        [InlineData("German [DE], M4B, 1.2GB, audiobook", "M4B", 1_288_490_188L, "German")]
        [InlineData("French [FR], FLAC, 512KB, short", "FLAC", 524_288L, "French")]
        [InlineData("no format here, just text", "", 0L, "")]
        [InlineData("", "", 0L, "")]
        public void ParseMetadataFromInnerText_Extracts_Tokens_Correctly(string innerText, string expectedFormat, long expectedSizeApprox, string expectedLanguage)
        {
            var (format, size, language) = AnnasArchiveSearchProvider.ParseMetadataFromInnerText(innerText);

            Assert.Equal(expectedFormat, format);
            Assert.Equal(expectedLanguage, language);
            if (expectedSizeApprox > 0)
                Assert.InRange(size, (long)(expectedSizeApprox * 0.99), (long)(expectedSizeApprox * 1.01));
            else
                Assert.Equal(0L, size);
        }

        [Fact]
        public async Task SearchAsync_Returns_Empty_On_Http_Failure()
        {
            var handler = new DelegatingHandlerStub(_ =>
                new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

            using var client = new HttpClient(handler);
            var provider = new AnnasArchiveSearchProvider(client, NullLogger<AnnasArchiveSearchProvider>.Instance);
            var indexer = MakeIndexer();

            // Must not throw; must return empty list
            var results = await provider.SearchAsync(indexer, "test query");

            Assert.Empty(results);
        }

        [Fact]
        public async Task SearchAsync_Uses_Custom_Mirror_Url()
        {
            Uri? capturedUri = null;
            var handler = new DelegatingHandlerStub(req =>
            {
                capturedUri = req.RequestUri;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new System.Net.Http.StringContent("<html><body></body></html>") };
            });

            using var client = new HttpClient(handler);
            var provider = new AnnasArchiveSearchProvider(client, NullLogger<AnnasArchiveSearchProvider>.Instance);
            var indexer = MakeIndexer();
            indexer.Url = "https://annas-archive.se";

            await provider.SearchAsync(indexer, "dune");

            Assert.NotNull(capturedUri);
            Assert.StartsWith("https://annas-archive.se/", capturedUri!.ToString());
        }

        [Fact]
        public async Task SearchAsync_Uses_Default_Url_When_Indexer_Url_Is_Blank()
        {
            Uri? capturedUri = null;
            var handler = new DelegatingHandlerStub(req =>
            {
                capturedUri = req.RequestUri;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new System.Net.Http.StringContent("<html><body></body></html>") };
            });

            using var client = new HttpClient(handler);
            var provider = new AnnasArchiveSearchProvider(client, NullLogger<AnnasArchiveSearchProvider>.Instance);
            var indexer = MakeIndexer();
            indexer.Url = "";  // blank → should fall back to default

            await provider.SearchAsync(indexer, "dune");

            Assert.NotNull(capturedUri);
            Assert.StartsWith("https://annas-archive.org/", capturedUri!.ToString());
        }

        // Reusable delegating handler stub (same pattern as IndexersNewznabParsingTests)
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
