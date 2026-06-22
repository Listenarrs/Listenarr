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
using System.Text;
using Listenarr.Infrastructure.DependencyInjection.Downloads;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks.Api;

namespace Listenarr.Tests.Features.Api.Services.Search.Providers
{
    [Trait("Name", "MyAnonamouseCookieTests")]
    [Trait("Category", "IndexerSearchProvider")]
    public class MyAnonamouseCookieTests : BaseTests
    {
        private Indexer _indexer = new IndexerBuilder().Build();

        // Redirects are application-visible through a dedicated client shared by primary/retry implementation and tests.
        // Behavior: Register the production download clients -> resolve the dedicated MyAnonamouseTorrent handler -> automatic redirects are disabled.
        // Primary failure mode: Automatic redirects bypass application redirect validation.
        // Proof obligation: The production MyAnonamouseTorrent named client exposes redirects to application code by setting AllowAutoRedirect=false.
        // Verification items:
        // - Resolve the exact MyAnonamouseTorrent named handler from production registration.
        // - Assert the primary transport does not follow redirects automatically.
        [Fact]
        public void DedicatedTorrentClient_DisablesAutomaticRedirects()
        {
            var services = new ServiceCollection();
            services.AddDownloadHttpClients();
            using var provider = services.BuildServiceProvider();

            var handlerFactory = provider.GetRequiredService<IHttpMessageHandlerFactory>();
            var handler = handlerFactory.CreateHandler(DownloadRegistrationExtensions.MyAnonamouseTorrentClientName);
            var primaryHandler = GetPrimaryHandler(handler);

            var allowAutoRedirect = primaryHandler switch
            {
                HttpClientHandler httpClientHandler => httpClientHandler.AllowAutoRedirect,
                SocketsHttpHandler socketsHttpHandler => socketsHttpHandler.AllowAutoRedirect,
                _ => throw new InvalidOperationException(
                    $"Unexpected primary handler type: {primaryHandler.GetType().FullName}")
            };

            Assert.False(allowAutoRedirect);
        }

        // Redirects are application-visible through a dedicated client shared by primary/retry implementation and tests.
        // Behavior: Build the shared test container -> send an unmatched request through MyAnonamouseTorrent -> the strict MyAnonamouseApiMock records and rejects it without network access.
        // Primary failure mode: Tests resolve a different named client handler and an unmatched request reaches the real network.
        // Proof obligation: The shared test container binds MyAnonamouseTorrent to the singleton MyAnonamouseApiMock with FailOnUnexpectedCalls behavior enabled.
        // Verification items:
        // - Resolve the named client and mock from the shared ServiceCollectionBuilder container.
        // - Assert an unmatched request is recorded by MyAnonamouseApiMock and fails with its deterministic strict-boundary exception.
        [Fact]
        public async Task DedicatedTorrentClient_UsesStrictHermeticMockInSharedTestContainer()
        {
            var services = new ServiceCollectionBuilder().Build();
            await using var provider = services.BuildServiceProvider();
            var clientFactory = provider.GetRequiredService<IHttpClientFactory>();
            var mock = provider.GetRequiredService<MyAnonamouseApiMock>();
            using var client = clientFactory.CreateClient(DownloadRegistrationExtensions.MyAnonamouseTorrentClientName);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.GetAsync("https://www.myanonamouse.net/history/unexpected"));

            Assert.Equal(
                "Unexpected MyAnonamouse API request: GET https://www.myanonamouse.net/history/unexpected",
                exception.Message);
            Assert.Equal(
                "https://www.myanonamouse.net/history/unexpected",
                mock.GetLastRequest().RequestUri?.AbsoluteUri);
        }

        public override async Task InitializeAsync()
        {
            _services.AddHttpClient("", client =>
            {
                client.BaseAddress = new Uri("https://www.myanonamouse.net");
            })
                .AddHttpMessageHandler<MyAnonamouseApiMock>();
            Init();

            _indexer = await _indexerRepository.AddAsync(new IndexerBuilder()
                .WithId(1)
                .WithName("MyAnonamouse1")
                .WithUrl("https://www.myanonamouse.net")
                .WithImplementation("MyAnonamouse")
                .WithType("Torrent")
                .WithEnabled()
                .WithInteractiveSearch()
                .WithSetting("mam_id", "old_mam")
                .Build());
        }

        private static HttpMessageHandler GetPrimaryHandler(HttpMessageHandler handler)
        {
            while (handler is DelegatingHandler delegatingHandler)
            {
                handler = delegatingHandler.InnerHandler
                    ?? throw new InvalidOperationException("Delegating handler has no inner handler.");
            }

            return handler;
        }

        [Fact]
        [Trait("Method", "SearchAsync")]
        public async Task SearchMyAnonamouse_Persists_MamIdFromSetCookie()
        {
            // Ensure initial mam_id present
            Assert.Contains("old_mam", _indexer.AdditionalSettings);

            var searchProvider = MockUtils.CreateMyAnonamouseSearchProvider(_provider);

            await searchProvider.SearchAsync(_indexer, "Test Title", null, null);

            // Verify the db indexer was updated with new mam_id
            var updated = await _indexerRepository.GetByIdAsync(_indexer.Id);
            Assert.Contains("new_mam", updated.AdditionalSettings);

            // Verify injected HttpClient received the cookie on subsequent calls when BaseAddress differs
            _indexer.Url = "https://another-host.example";
            await searchProvider.SearchAsync(_indexer, "Test Title", null, null);

            // Ensure the request includes a Cookie header with the new mam_id
            var messageHandler = _provider.GetRequiredService<MyAnonamouseApiMock>();
            var request = messageHandler.GetLastRequest();
            Assert.True(request.Headers.Contains("Cookie"));
            var cookie = request.Headers.GetValues("Cookie");
            Assert.True(cookie.Any(v => v.Contains("mam_id=new_mam")), "Expected Cookie header with new mam_id");
        }

        [Fact]
        [Trait("Method", "TryPrepareMyAnonamouseTorrentAsync")]
        public async Task TryPrepareMyAnonamouseTorrent_SendsCookieWhenHostDiffers()
        {
            // Build a SearchResult that references the indexer and uses a different host for torrent URL
            var searchResult = new SearchResult
            {
                Title = "Test Book",
                TorrentUrl = "https://47.39.239.96/tor/download.php/abc",
                IndexerId = _indexer.Id,
                TorrentFileContent = null
            };

            await _provider.GetRequiredService<MyAnonamouseTorrentPreparationService>()
                .PrepareAsync(searchResult);

            var handler = _provider.GetRequiredService<MyAnonamouseApiMock>();
            var capturedRequest = handler.GetLastRequest();
            Assert.NotNull(capturedRequest);
            Assert.True(capturedRequest.Headers.TryGetValues("Cookie", out var cookieVals));
            Assert.Contains("mam_id=old_mam", cookieVals.First());

            // Also assert the torrent content was saved into SearchResult
            Assert.NotNull(searchResult.TorrentFileContent);
            Assert.NotEmpty(searchResult.TorrentFileContent);
        }

        [Fact]
        [Trait("Method", "TryPrepareMyAnonamouseTorrentAsync")]
        public async Task TryPrepareMyAnonamouseTorrent_SetsHostHeaderWhenHostDiffers()
        {
            // Build a SearchResult that references the indexer and uses a different host for torrent URL
            var sr = new SearchResult
            {
                Title = "Test Book",
                TorrentUrl = "https://47.39.239.96/tor/download.php/abc",
                IndexerId = _indexer.Id,
                TorrentFileContent = null
            };

            await _provider.GetRequiredService<MyAnonamouseTorrentPreparationService>()
                .PrepareAsync(sr);

            var handler = _provider.GetRequiredService<MyAnonamouseApiMock>();
            var capturedRequest = handler.GetLastRequest();
            Assert.NotNull(capturedRequest);
            // Assert Host header was set to the indexer host
            Assert.Equal("www.myanonamouse.net", capturedRequest.Headers.Host);
        }

        [Fact]
        [Trait("Method", "TryPrepareMyAnonamouseTorrentAsync")]
        public async Task TryPrepareMyAnonamouseTorrent_FollowsRedirectAndPreservesHeaders()
        {
            var sr = new SearchResult
            {
                Title = "Test Book",
                TorrentUrl = "https://www.myanonamouse.net/tor/redirectstart",
                IndexerId = _indexer.Id,
                TorrentFileContent = null
            };

            await _provider.GetRequiredService<MyAnonamouseTorrentPreparationService>()
                .PrepareAsync(sr);

            var handler = _provider.GetRequiredService<MyAnonamouseApiMock>();
            var capturedRequest = handler.GetLastRequest();
            Assert.NotNull(capturedRequest);
            // The redirected request should have Host set to indexer host
            Assert.Equal("www.myanonamouse.net", capturedRequest.Headers.Host);
            // The redirected request should include the updated mam_id from the redirect response
            Assert.True(capturedRequest.Headers.TryGetValues("Cookie", out var cookieVals));
            Assert.Contains("mam_id=redirect_mam", cookieVals.First());

            // Note: persistence of mam_id to the database is handled; functional behavior verified below.
            Assert.NotNull(sr.TorrentFileContent);
            Assert.NotEmpty(sr.TorrentFileContent);
        }

        [Fact]
        [Trait("Method", "TryPrepareMyAnonamouseTorrentAsync")]
        public async Task TryPrepareMyAnonamouseTorrent_AbortsWhenTrackerReturnsUnrecognizedHostError()
        {
            var sr = new SearchResult
            {
                Title = "Test Book",
                TorrentUrl = "https://www.myanonamouse.net/tor/download.php/me+IG7...",
                IndexerId = _indexer.Id,
                TorrentFileContent = null
            };

            await _provider.GetRequiredService<MyAnonamouseTorrentPreparationService>()
                .PrepareAsync(sr);

            // Since the tracker returned an error HTML page, the torrent should not be cached/uploaded
            Assert.Null(sr.TorrentFileContent);

            var handler = _provider.GetRequiredService<MyAnonamouseApiMock>();
            var capturedRequest = handler.GetLastRequest();
            Assert.NotNull(capturedRequest);
        }

        [Fact]
        [Trait("Method", "TryPrepareMyAnonamouseTorrentAsync")]
        public async Task TryPrepareMyAnonamouseTorrent_Caches_Bytes_Accessible_Via_Controller()
        {
            var sr = new SearchResult
            {
                Title = "Test Book",
                TorrentUrl = "https://www.myanonamouse.net/tor/download.php/dummy",
                IndexerId = _indexer.Id,
                TorrentFileContent = null
            };

            var downloadId = Guid.NewGuid().ToString();

            var downloadService = _provider.GetRequiredService<DownloadService>();
            await _provider.GetRequiredService<MyAnonamouseTorrentPreparationService>()
                .PrepareAsync(sr, downloadId);

            // Now create a DownloadsController and request the cached torrent
            var downloadsController = MockUtils.CreateDownloadsController(_provider);
            var result = downloadsController.GetCachedTorrent(downloadId);
            Assert.IsType<Microsoft.AspNetCore.Mvc.FileContentResult>(result);
            var fileResult = (Microsoft.AspNetCore.Mvc.FileContentResult)result;
            Assert.Equal("application/x-bittorrent", fileResult.ContentType);
            Assert.Equal("file.torrent", fileResult.FileDownloadName);
            Assert.Equal(Encoding.UTF8.GetBytes("dummy-torrent-bytes"), fileResult.FileContents);
        }

        [Fact]
        [Trait("Method", "TryPrepareMyAnonamouseTorrentAsync")]
        public async Task TryPrepareMyAnonamouseTorrent_Caches_Announces_Accessible_Via_Controller()
        {
            var sr = new SearchResult
            {
                Title = "Test Book",
                TorrentUrl = "https://www.myanonamouse.net/tor/download.php/abc",
                IndexerId = _indexer.Id,
                TorrentFileContent = null
            };

            var downloadId = Guid.NewGuid().ToString();

            var downloadService = _provider.GetRequiredService<DownloadService>();
            await _provider.GetRequiredService<MyAnonamouseTorrentPreparationService>()
                .PrepareAsync(sr, downloadId);

            // Now request announces from the sync DownloadsController helper
            var downloadsController = MockUtils.CreateDownloadsController(_provider);
            var result = downloadsController.GetCachedAnnounces(downloadId);
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
            var ok = (Microsoft.AspNetCore.Mvc.OkObjectResult)result;
            Assert.NotNull(ok.Value);

            // Also assert via service accessor
            var announces = await downloadService.GetCachedAnnouncesAsync(downloadId);
            Assert.NotNull(announces);
            // Expect mam_id to be appended to announce URLs for MyAnonamouse so trackers that require passkey accept them
            Assert.Contains(announces, a => a.IndexOf("mam_id=old_mam", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        [Trait("Method", "NormalizeMamId")]
        public void NormalizeMamId_HandlesVariousEncodings()
        {
            // Test raw mam_id (no encoding)
            Assert.Equal("abc123", MyAnonamouseHelper.NormalizeMamId("abc123"));

            // Test single-encoded (e.g., from URL)
            Assert.Equal("abc%2Bdef%3D%3D", MyAnonamouseHelper.NormalizeMamId("abc%2Bdef%3D%3D"));

            // Test double-encoded (problematic case) - should decode to single-encoded
            Assert.Equal("abc%2Bdef%3D%3D", MyAnonamouseHelper.NormalizeMamId("abc%252Bdef%253D%253D"));

            // Test triple-encoded
            Assert.Equal("abc%2Bdef%3D%3D", MyAnonamouseHelper.NormalizeMamId("abc%25252Bdef%25253D%25253D"));

            // Test empty/null
            Assert.Equal("", MyAnonamouseHelper.NormalizeMamId(""));
            Assert.Null(MyAnonamouseHelper.NormalizeMamId(null));
        }
    }
}
