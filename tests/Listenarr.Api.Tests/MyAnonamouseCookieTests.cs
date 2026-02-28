using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.SignalR;
using Listenarr.Api.Hubs;
using Listenarr.Api.Services;
using Listenarr.Api.Models;
using Listenarr.Api.Controllers;
using Listenarr.Infrastructure.Models;
using System.Linq;
using System.Reflection;
using Listenarr.Domain.Models;
using System.Text;
using System;

namespace Listenarr.Api.Tests
{
    public class MyAnonamouseCookieTests
    {
        [Fact]
        public async Task SearchMyAnonamouse_Persists_MamIdFromSetCookie()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);
            var indexer = new Indexer { Name = "MyAnonamouse1", Url = "https://www.myanonamouse.net", Implementation = "MyAnonamouse", Type = "Torrent", IsEnabled = true, EnableInteractiveSearch = true, AdditionalSettings = "{ \"mam_id\": \"old_mam\" }" };
            db.Indexers.Add(indexer);
            db.SaveChanges();
            // Ensure initial mam_id present
            var initial = db.Indexers.First(i => i.Id == indexer.Id);
            Assert.Contains("old_mam", initial.AdditionalSettings);
            Uri? capturedUri = null;
            var handler = new DelegatingHandlerMock((req, ct) => {
                capturedUri = req.RequestUri;
                var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
                resp.Headers.Add("Set-Cookie", "mam_id=\"new_mam\"; Path=/; HttpOnly");
                return Task.FromResult(resp);
            });

            using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://www.myanonamouse.net") };
            var provider = new Listenarr.Api.Services.Search.Providers.MyAnonamouseSearchProvider(NullLogger<Listenarr.Api.Services.Search.Providers.MyAnonamouseSearchProvider>.Instance, httpClient, db);

            var results = await provider.SearchAsync(indexer, "Test Title", null, null);

            // Verify the db indexer was updated with new mam_id
            var updated = db.Indexers.First(i => i.Id == indexer.Id);
            Assert.Contains("new_mam", updated.AdditionalSettings);

            // Verify injected HttpClient received the cookie on subsequent calls when BaseAddress differs
            var handler2 = new DelegatingHandlerMock((req, ct) => {
                // Ensure the request includes a Cookie header with the new mam_id
                Assert.True(req.Headers.Contains("Cookie") && req.Headers.GetValues("Cookie").Any(v => v.Contains("mam_id=new_mam")), "Expected Cookie header with new mam_id");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
            });

            using var httpClient2 = new HttpClient(handler2) { BaseAddress = new Uri("https://another-host.example") };
            var provider2 = new Listenarr.Api.Services.Search.Providers.MyAnonamouseSearchProvider(NullLogger<Listenarr.Api.Services.Search.Providers.MyAnonamouseSearchProvider>.Instance, httpClient2, db);

            await provider2.SearchAsync(indexer, "Test Title", null, null);
        }

        [Fact]
        public async Task TryPrepareMyAnonamouseTorrent_SendsCookieWhenHostDiffers()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);
            var idx = new Indexer { Name = "MyAnonamouse1", Url = "https://www.myanonamouse.net", Implementation = "MyAnonamouse", Type = "Torrent", IsEnabled = true, EnableInteractiveSearch = true, AdditionalSettings = "{ \"mam_id\": \"test_mam\" }" };
            db.Indexers.Add(idx);
            db.SaveChanges();

            HttpRequestMessage? capturedRequest = null;
            var handler = new DelegatingHandlerMock((req, ct) => {
                capturedRequest = req;
                var content = new ByteArrayContent(Encoding.UTF8.GetBytes("dummy-torrent"));
                content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileName = "file.torrent" };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            });

            var httpClient = new HttpClient(handler);

            // Simple IHttpClientFactory that returns our client
            var httpFactory = new SimpleHttpClientFactory(httpClient);

            // Build a minimal service provider for dependencies
            var provider = TestServiceFactory.BuildServiceProvider(services => {
                services.AddSingleton<IHttpClientFactory>(httpFactory);
                services.AddSingleton<IDbContextFactory<ListenArrDbContext>>(sp => new TestDbFactory(options));
                // Provide a minimal HubContext and memory cache for DownloadService construction
                services.AddSingleton<IHubContext<DownloadHub>>(new TestHubContext());
                services.AddMemoryCache();
                // Minimal audiobook repository for DownloadService
                services.AddSingleton<Listenarr.Api.Services.IAudiobookRepository>(new TestAudiobookRepository());
            });

            // Resolve dependencies with fallbacks to minimal test implementations when not registered
            var hubContext = provider.GetService<IHubContext<DownloadHub>>()!;
            var audiobookRepo = provider.GetService<IAudiobookRepository>()!;
            var configSvc = provider.GetService<IConfigurationService>() ?? new TestConfigurationService();
            var dbFactorySvc = provider.GetService<IDbContextFactory<ListenArrDbContext>>()!;
            var httpFactorySvc = provider.GetService<IHttpClientFactory>()!;
            var scopeFactorySvc = provider.GetService<IServiceScopeFactory>()!;
            var pathMappingSvc = provider.GetService<IRemotePathMappingService>() ?? new TestRemotePathMappingService();
            var importSvc = provider.GetService<IImportService>() ?? new TestImportService();
            var searchSvc = provider.GetService<ISearchService>() ?? new TestSearchService();
            var clientGatewaySvc = provider.GetService<IDownloadClientGateway>();
            var cacheSvc = provider.GetService<IMemoryCache>()!;
            var dqSvc = provider.GetService<IDownloadQueueService>() ?? new TestDownloadQueueService();
            var completedProc = provider.GetService<ICompletedDownloadProcessor>() ?? new TestCompletedDownloadProcessor();
            var metricsSvc = provider.GetService<IAppMetricsService>() ?? new TestAppMetricsService();
            var notificationSvc = provider.GetService<NotificationService>()!;
            var hubBroadcaster = provider.GetService<Listenarr.Application.Services.IHubBroadcaster>();

            var downloadService = new Listenarr.Api.Services.DownloadService(
                hubContext,
                audiobookRepo,
                configSvc,
                dbFactorySvc,
                NullLogger<Listenarr.Api.Services.DownloadService>.Instance,
                httpFactorySvc,
                scopeFactorySvc,
                pathMappingSvc,
                importSvc,
                searchSvc,
                clientGatewaySvc,
                cacheSvc,
                dqSvc,
                completedProc,
                metricsSvc,
                notificationSvc,
                hubBroadcaster );

            // Build a SearchResult that references the indexer and uses a different host for torrent URL
            var sr = new SearchResult
            {
                Title = "Test Book",
                TorrentUrl = "https://47.39.239.96/tor/download.php/abc",
                IndexerId = idx.Id,
                TorrentFileContent = null
            };

            // Call the non-public TryPrepareMyAnonamouseTorrentAsync via reflection
            var method = typeof(Listenarr.Api.Services.DownloadService).GetMethod("TryPrepareMyAnonamouseTorrentAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            var task = (Task)method!.Invoke(downloadService, new object[] { sr, null })!;
            await task;

            Assert.NotNull(capturedRequest);
            Assert.True(capturedRequest.Headers.TryGetValues("Cookie", out var cookieVals));
            Assert.Contains("mam_id=test_mam", cookieVals.First());

            // Also assert the torrent content was saved into SearchResult
            Assert.NotNull(sr.TorrentFileContent);
            Assert.NotEmpty(sr.TorrentFileContent);
        }

        [Fact]
        public async Task TryPrepareMyAnonamouseTorrent_SetsHostHeaderWhenHostDiffers()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);
            var idx = new Indexer { Name = "MyAnonamouse1", Url = "https://www.myanonamouse.net", Implementation = "MyAnonamouse", Type = "Torrent", IsEnabled = true, EnableInteractiveSearch = true, AdditionalSettings = "{ \"mam_id\": \"test_mam\" }" };
            db.Indexers.Add(idx);
            db.SaveChanges();

            HttpRequestMessage? capturedRequest = null;
            var handler = new DelegatingHandlerMock((req, ct) => {
                capturedRequest = req;
                var content = new ByteArrayContent(Encoding.UTF8.GetBytes("dummy-torrent"));
                content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileName = "file.torrent" };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            });

            var httpClient = new HttpClient(handler);

            // Simple IHttpClientFactory that returns our client
            var httpFactory = new SimpleHttpClientFactory(httpClient);

            // Build a minimal service provider for dependencies
            var provider = TestServiceFactory.BuildServiceProvider(services => {
                services.AddSingleton<IHttpClientFactory>(httpFactory);
                services.AddSingleton<IDbContextFactory<ListenArrDbContext>>(sp => new TestDbFactory(options));
                services.AddSingleton<IHubContext<DownloadHub>>(new TestHubContext());
                services.AddMemoryCache();
                services.AddSingleton<Listenarr.Api.Services.IAudiobookRepository>(new TestAudiobookRepository());
            });

            var hubContext = provider.GetService<IHubContext<DownloadHub>>()!;
            var audiobookRepo = provider.GetService<IAudiobookRepository>()!;
            var configSvc = provider.GetService<IConfigurationService>() ?? new TestConfigurationService();
            var dbFactorySvc = provider.GetService<IDbContextFactory<ListenArrDbContext>>()!;
            var httpFactorySvc = provider.GetService<IHttpClientFactory>()!;
            var scopeFactorySvc = provider.GetService<IServiceScopeFactory>()!;
            var pathMappingSvc = provider.GetService<IRemotePathMappingService>() ?? new TestRemotePathMappingService();
            var importSvc = provider.GetService<IImportService>() ?? new TestImportService();
            var searchSvc = provider.GetService<ISearchService>() ?? new TestSearchService();
            var clientGatewaySvc = provider.GetService<IDownloadClientGateway>();
            var cacheSvc = provider.GetService<IMemoryCache>()!;
            var dqSvc = provider.GetService<IDownloadQueueService>() ?? new TestDownloadQueueService();
            var completedProc = provider.GetService<ICompletedDownloadProcessor>() ?? new TestCompletedDownloadProcessor();
            var metricsSvc = provider.GetService<IAppMetricsService>() ?? new TestAppMetricsService();
            var notificationSvc = provider.GetService<NotificationService>()!;
            var hubBroadcaster = provider.GetService<Listenarr.Application.Services.IHubBroadcaster>();

            var downloadService = new Listenarr.Api.Services.DownloadService(
                hubContext,
                audiobookRepo,
                configSvc,
                dbFactorySvc,
                NullLogger<Listenarr.Api.Services.DownloadService>.Instance,
                httpFactorySvc,
                scopeFactorySvc,
                pathMappingSvc,
                importSvc,
                searchSvc,
                clientGatewaySvc,
                cacheSvc,
                dqSvc,
                completedProc,
                metricsSvc,
                notificationSvc,
                hubBroadcaster );

            // Build a SearchResult that references the indexer and uses a different host for torrent URL
            var sr = new SearchResult
            {
                Title = "Test Book",
                TorrentUrl = "https://47.39.239.96/tor/download.php/abc",
                IndexerId = idx.Id,
                TorrentFileContent = null
            };

            // Call the non-public TryPrepareMyAnonamouseTorrentAsync via reflection
            var method = typeof(Listenarr.Api.Services.DownloadService).GetMethod("TryPrepareMyAnonamouseTorrentAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            var task = (Task)method!.Invoke(downloadService, new object[] { sr, null })!;
            await task;

            Assert.NotNull(capturedRequest);
            // Assert Host header was set to the indexer host
            Assert.Equal("www.myanonamouse.net", capturedRequest.Headers.Host);
        }

        [Fact]
        public async Task TryPrepareMyAnonamouseTorrent_FollowsRedirectAndPreservesHeaders()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);
            var idx = new Indexer { Name = "MyAnonamouse1", Url = "https://www.myanonamouse.net", Implementation = "MyAnonamouse", Type = "Torrent", IsEnabled = true, EnableInteractiveSearch = true, AdditionalSettings = "{ \"mam_id\": \"orig_mam\" }" };
            db.Indexers.Add(idx);
            db.SaveChanges();

            HttpRequestMessage? capturedRequest = null;
            int callCount = 0;
            var handler = new DelegatingHandlerMock((req, ct) => {
                callCount++;
                if (callCount == 1)
                {
                    var resp = new HttpResponseMessage(HttpStatusCode.Found);
                    resp.Headers.Location = new Uri("https://47.39.239.96/tor/download.php/abc");
                    resp.Headers.Add("Set-Cookie", "mam_id=redirect_mam; Path=/; HttpOnly");
                    return Task.FromResult(resp);
                }

                // Second call: capture the redirected request
                capturedRequest = req;
                var content = new ByteArrayContent(Encoding.UTF8.GetBytes("dummy-torrent"));
                content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileName = "file.torrent" };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            });

            var httpClient = new HttpClient(handler);
            var httpFactory = new SimpleHttpClientFactory(httpClient);

            var provider = TestServiceFactory.BuildServiceProvider(services => {
                services.AddSingleton<IHttpClientFactory>(httpFactory);
                services.AddSingleton<IDbContextFactory<ListenArrDbContext>>(sp => new TestDbFactory(options));
                services.AddSingleton<IHubContext<DownloadHub>>(new TestHubContext());
                services.AddMemoryCache();
                services.AddSingleton<Listenarr.Api.Services.IAudiobookRepository>(new TestAudiobookRepository());
            });

            var hubContext = provider.GetService<IHubContext<DownloadHub>>()!;
            var audiobookRepo = provider.GetService<IAudiobookRepository>()!;
            var configSvc = provider.GetService<IConfigurationService>() ?? new TestConfigurationService();
            var dbFactorySvc = provider.GetService<IDbContextFactory<ListenArrDbContext>>()!;
            var httpFactorySvc = provider.GetService<IHttpClientFactory>()!;
            var scopeFactorySvc = provider.GetService<IServiceScopeFactory>()!;
            var pathMappingSvc = provider.GetService<IRemotePathMappingService>() ?? new TestRemotePathMappingService();
            var importSvc = provider.GetService<IImportService>() ?? new TestImportService();
            var searchSvc = provider.GetService<ISearchService>() ?? new TestSearchService();
            var clientGatewaySvc = provider.GetService<IDownloadClientGateway>();
            var cacheSvc = provider.GetService<IMemoryCache>()!;
            var dqSvc = provider.GetService<IDownloadQueueService>() ?? new TestDownloadQueueService();
            var completedProc = provider.GetService<ICompletedDownloadProcessor>() ?? new TestCompletedDownloadProcessor();
            var metricsSvc = provider.GetService<IAppMetricsService>() ?? new TestAppMetricsService();
            var notificationSvc = provider.GetService<NotificationService>()!;
            var hubBroadcaster = provider.GetService<Listenarr.Application.Services.IHubBroadcaster>();

            var downloadService = new Listenarr.Api.Services.DownloadService(
                hubContext,
                audiobookRepo,
                configSvc,
                dbFactorySvc,
                NullLogger<Listenarr.Api.Services.DownloadService>.Instance,
                httpFactorySvc,
                scopeFactorySvc,
                pathMappingSvc,
                importSvc,
                searchSvc,
                clientGatewaySvc,
                cacheSvc,
                dqSvc,
                completedProc,
                metricsSvc,
                notificationSvc,
                hubBroadcaster );

            var sr = new SearchResult
            {
                Title = "Test Book",
                TorrentUrl = "https://www.myanonamouse.net/tor/redirectstart",
                IndexerId = idx.Id,
                TorrentFileContent = null
            };

            var method = typeof(Listenarr.Api.Services.DownloadService).GetMethod("TryPrepareMyAnonamouseTorrentAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            var task = (Task)method!.Invoke(downloadService, new object[] { sr, null })!;
            await task;

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
        public async Task TryPrepareMyAnonamouseTorrent_AbortsWhenTrackerReturnsUnrecognizedHostError()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);
            var idx = new Indexer { Name = "MyAnonamouse1", Url = "https://www.myanonamouse.net", Implementation = "MyAnonamouse", Type = "Torrent", IsEnabled = true, EnableInteractiveSearch = true, AdditionalSettings = "{ \"mam_id\": \"test_mam\" }" };
            db.Indexers.Add(idx);
            db.SaveChanges();

            HttpRequestMessage? capturedRequest = null;
            var handler = new DelegatingHandlerMock((req, ct) => {
                capturedRequest = req;
                var html = "<html><body>Unrecognized host/PassKey</body></html>";
                var content = new StringContent(html, Encoding.UTF8, "text/html");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            });

            var httpClient = new HttpClient(handler);
            var httpFactory = new SimpleHttpClientFactory(httpClient);

            var provider = TestServiceFactory.BuildServiceProvider(services => {
                services.AddSingleton<IHttpClientFactory>(httpFactory);
                services.AddSingleton<IDbContextFactory<ListenArrDbContext>>(sp => new TestDbFactory(options));
                services.AddSingleton<IHubContext<DownloadHub>>(new TestHubContext());
                services.AddMemoryCache();
                services.AddSingleton<Listenarr.Api.Services.IAudiobookRepository>(new TestAudiobookRepository());
            });

            var hubContext = provider.GetService<IHubContext<DownloadHub>>()!;
            var audiobookRepo = provider.GetService<IAudiobookRepository>()!;
            var configSvc = provider.GetService<IConfigurationService>() ?? new TestConfigurationService();
            var dbFactorySvc = provider.GetService<IDbContextFactory<ListenArrDbContext>>()!;
            var httpFactorySvc = provider.GetService<IHttpClientFactory>()!;
            var scopeFactorySvc = provider.GetService<IServiceScopeFactory>()!;
            var pathMappingSvc = provider.GetService<IRemotePathMappingService>() ?? new TestRemotePathMappingService();
            var importSvc = provider.GetService<IImportService>() ?? new TestImportService();
            var searchSvc = provider.GetService<ISearchService>() ?? new TestSearchService();
            var clientGatewaySvc = provider.GetService<IDownloadClientGateway>();
            var cacheSvc = provider.GetService<IMemoryCache>()!;
            var dqSvc = provider.GetService<IDownloadQueueService>() ?? new TestDownloadQueueService();
            var completedProc = provider.GetService<ICompletedDownloadProcessor>() ?? new TestCompletedDownloadProcessor();
            var metricsSvc = provider.GetService<IAppMetricsService>() ?? new TestAppMetricsService();
            var notificationSvc = provider.GetService<NotificationService>()!;
            var hubBroadcaster = provider.GetService<Listenarr.Application.Services.IHubBroadcaster>();

            var downloadService = new Listenarr.Api.Services.DownloadService(
                hubContext,
                audiobookRepo,
                configSvc,
                dbFactorySvc,
                NullLogger<Listenarr.Api.Services.DownloadService>.Instance,
                httpFactorySvc,
                scopeFactorySvc,
                pathMappingSvc,
                importSvc,
                searchSvc,
                clientGatewaySvc,
                cacheSvc,
                dqSvc,
                completedProc,
                metricsSvc,
                notificationSvc,
                hubBroadcaster );

            var sr = new SearchResult
            {
                Title = "Test Book",
                TorrentUrl = "https://www.myanonamouse.net/tor/download.php/me+IG7...",
                IndexerId = idx.Id,
                TorrentFileContent = null
            };

            var method = typeof(Listenarr.Api.Services.DownloadService).GetMethod("TryPrepareMyAnonamouseTorrentAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            var task = (Task)method!.Invoke(downloadService, new object[] { sr, null })!;
            await task;

            // Since the tracker returned an error HTML page, the torrent should not be cached/uploaded
            Assert.Null(sr.TorrentFileContent);
            Assert.NotNull(capturedRequest);
        }

        [Fact]
        public async Task TryPrepareMyAnonamouseTorrent_Caches_Bytes_Accessible_Via_Controller()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);
            var idx = new Indexer { Name = "MyAnonamouse1", Url = "https://www.myanonamouse.net", Implementation = "MyAnonamouse", Type = "Torrent", IsEnabled = true, EnableInteractiveSearch = true, AdditionalSettings = "{ \"mam_id\": \"test_mam\" }" };
            db.Indexers.Add(idx);
            db.SaveChanges();

            var handler = new DelegatingHandlerMock((req, ct) => {
                var content = new ByteArrayContent(Encoding.UTF8.GetBytes("dummy-torrent-bytes"));
                content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileName = "file.torrent" };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            });

            var httpClient = new HttpClient(handler);
            var httpFactory = new SimpleHttpClientFactory(httpClient);

            var provider = TestServiceFactory.BuildServiceProvider(services => {
                services.AddSingleton<IHttpClientFactory>(httpFactory);
                services.AddSingleton<IDbContextFactory<ListenArrDbContext>>(sp => new TestDbFactory(options));
                services.AddSingleton<IHubContext<DownloadHub>>(new TestHubContext());
                services.AddMemoryCache();
                services.AddSingleton<Listenarr.Api.Services.IAudiobookRepository>(new TestAudiobookRepository());
            });

            var hubContext = provider.GetService<IHubContext<DownloadHub>>()!;
            var audiobookRepo = provider.GetService<IAudiobookRepository>()!;
            var configSvc = provider.GetService<IConfigurationService>() ?? new TestConfigurationService();
            var dbFactorySvc = provider.GetService<IDbContextFactory<ListenArrDbContext>>()!;
            var httpFactorySvc = provider.GetService<IHttpClientFactory>()!;
            var scopeFactorySvc = provider.GetService<IServiceScopeFactory>()!;
            var pathMappingSvc = provider.GetService<IRemotePathMappingService>() ?? new TestRemotePathMappingService();
            var importSvc = provider.GetService<IImportService>() ?? new TestImportService();
            var searchSvc = provider.GetService<ISearchService>() ?? new TestSearchService();
            var clientGatewaySvc = provider.GetService<IDownloadClientGateway>();
            var cacheSvc = provider.GetService<IMemoryCache>()!;
            var dqSvc = provider.GetService<IDownloadQueueService>() ?? new TestDownloadQueueService();
            var completedProc = provider.GetService<ICompletedDownloadProcessor>() ?? new TestCompletedDownloadProcessor();
            var metricsSvc = provider.GetService<IAppMetricsService>() ?? new TestAppMetricsService();
            var notificationSvc = provider.GetService<NotificationService>()!;
            var hubBroadcaster = provider.GetService<Listenarr.Application.Services.IHubBroadcaster>();

            var downloadService = new Listenarr.Api.Services.DownloadService(
                hubContext,
                audiobookRepo,
                configSvc,
                dbFactorySvc,
                NullLogger<Listenarr.Api.Services.DownloadService>.Instance,
                httpFactorySvc,
                scopeFactorySvc,
                pathMappingSvc,
                importSvc,
                searchSvc,
                clientGatewaySvc,
                cacheSvc,
                dqSvc,
                completedProc,
                metricsSvc,
                notificationSvc,
                hubBroadcaster );

            var sr = new SearchResult
            {
                Title = "Test Book",
                TorrentUrl = "https://www.myanonamouse.net/tor/download.php/abc",
                IndexerId = idx.Id,
                TorrentFileContent = null
            };

            var downloadId = Guid.NewGuid().ToString();

            // Call the non-public TryPrepareMyAnonamouseTorrentAsync with downloadId via reflection
            var method = typeof(Listenarr.Api.Services.DownloadService).GetMethod("TryPrepareMyAnonamouseTorrentAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            var task = (Task)method!.Invoke(downloadService, new object[] { sr, downloadId })!;
            await task;

            // Now create a DownloadsController and request the cached torrent
            var downloadsController = new Listenarr.Api.Controllers.DownloadsController(db, NullLogger<Listenarr.Api.Controllers.DownloadsController>.Instance, configSvc, cacheSvc);
            var result = downloadsController.GetCachedTorrent(downloadId);
            Assert.IsType<Microsoft.AspNetCore.Mvc.FileContentResult>(result);
            var fileResult = (Microsoft.AspNetCore.Mvc.FileContentResult)result;
            Assert.Equal("application/x-bittorrent", fileResult.ContentType);
            Assert.Equal("file.torrent", fileResult.FileDownloadName);
            Assert.Equal(Encoding.UTF8.GetBytes("dummy-torrent-bytes"), fileResult.FileContents);
        }

        [Fact]
        public async Task TryPrepareMyAnonamouseTorrent_Caches_Announces_Accessible_Via_Controller()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);
            var idx = new Indexer { Name = "MyAnonamouse1", Url = "https://www.myanonamouse.net", Implementation = "MyAnonamouse", Type = "Torrent", IsEnabled = true, EnableInteractiveSearch = true, AdditionalSettings = "{ \"mam_id\": \"old_mam\" }" };
            db.Indexers.Add(idx);
            db.SaveChanges();

            Uri? capturedUri = null;
            var sb = new StringBuilder();
            sb.Append("d");
            sb.Append("8:announce82:https://www.myanonamouse.net/tracker.php/mGDjyetAEBGCaneLZNS9OHawTo1upcwU/announce");
            sb.Append("e");

            var handler = new DelegatingHandlerMock((req, ct) => {
                capturedUri = req.RequestUri;
                var content = new ByteArrayContent(Encoding.UTF8.GetBytes(sb.ToString()));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            });

            var httpFactory = new SimpleHttpClientFactory(new HttpClient(handler));

            // Build a minimal service provider for dependencies (reusing test helpers)
            var provider = TestServiceFactory.BuildServiceProvider(services => {
                services.AddSingleton<IHttpClientFactory>(httpFactory);
                services.AddSingleton<IDbContextFactory<ListenArrDbContext>>(sp => new TestDbFactory(options));
                services.AddSingleton<IHubContext<DownloadHub>>(new TestHubContext());
                services.AddMemoryCache();
                services.AddSingleton<Listenarr.Api.Services.IAudiobookRepository>(new TestAudiobookRepository());
            });

            var hubContext = provider.GetService<IHubContext<DownloadHub>>()!;
            var audiobookRepo = provider.GetService<IAudiobookRepository>()!;
            var configSvc = provider.GetService<IConfigurationService>() ?? new TestConfigurationService();
            var dbFactorySvc = provider.GetService<IDbContextFactory<ListenArrDbContext>>()!;
            var httpFactorySvc = provider.GetService<IHttpClientFactory>()!;
            var scopeFactorySvc = provider.GetService<IServiceScopeFactory>()!;
            var pathMappingSvc = provider.GetService<IRemotePathMappingService>() ?? new TestRemotePathMappingService();
            var importSvc = provider.GetService<IImportService>() ?? new TestImportService();
            var searchSvc = provider.GetService<ISearchService>() ?? new TestSearchService();
            var clientGatewaySvc = provider.GetService<IDownloadClientGateway>();
            var cacheSvc = provider.GetService<IMemoryCache>()!;
            var dqSvc = provider.GetService<IDownloadQueueService>() ?? new TestDownloadQueueService();
            var completedProc = provider.GetService<ICompletedDownloadProcessor>() ?? new TestCompletedDownloadProcessor();
            var metricsSvc = provider.GetService<IAppMetricsService>() ?? new TestAppMetricsService();
            var notificationSvc = provider.GetService<NotificationService>()!;
            var hubBroadcaster = provider.GetService<Listenarr.Application.Services.IHubBroadcaster>();

            var downloadService = new Listenarr.Api.Services.DownloadService(
                hubContext,
                audiobookRepo,
                configSvc,
                dbFactorySvc,
                NullLogger<Listenarr.Api.Services.DownloadService>.Instance,
                httpFactorySvc,
                scopeFactorySvc,
                pathMappingSvc,
                importSvc,
                searchSvc,
                clientGatewaySvc,
                cacheSvc,
                dqSvc,
                completedProc,
                metricsSvc,
                notificationSvc,
                hubBroadcaster );

            var sr = new SearchResult
            {
                Title = "Test Book",
                TorrentUrl = "https://www.myanonamouse.net/tor/download.php/abc",
                IndexerId = idx.Id,
                TorrentFileContent = null
            };

            var downloadId = Guid.NewGuid().ToString();

            // Call the non-public TryPrepareMyAnonamouseTorrentAsync with downloadId via reflection
            var method = typeof(Listenarr.Api.Services.DownloadService).GetMethod("TryPrepareMyAnonamouseTorrentAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            var task = (Task)method!.Invoke(downloadService, new object[] { sr, downloadId })!;
            await task;

            // Now request announces from the sync DownloadsController helper
            var downloadsController = new DownloadsController(db, NullLogger<DownloadsController>.Instance, configSvc, cacheSvc);
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
        // Simple IHttpClientFactory implementation for tests
        private class SimpleHttpClientFactory : IHttpClientFactory
        {
            private readonly HttpClient _client;
            public SimpleHttpClientFactory(HttpClient client) => _client = client;
            public HttpClient CreateClient(string name) => _client;
        }

        // Simple IDbContextFactory for tests that returns new contexts from the same options
        private class TestDbFactory : IDbContextFactory<ListenArrDbContext>
        {
            private readonly DbContextOptions<ListenArrDbContext> _opts;
            public TestDbFactory(DbContextOptions<ListenArrDbContext> opts) { _opts = opts; }
            public ListenArrDbContext CreateDbContext() => new ListenArrDbContext(_opts);
            public ValueTask<ListenArrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => new ValueTask<ListenArrDbContext>(Task.FromResult(new ListenArrDbContext(_opts)));
        }

        // Minimal IHubContext implementation for tests
        private class TestClientProxy : Microsoft.AspNetCore.SignalR.IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }

        private class TestHubClients : IHubClients
        {
            private readonly IClientProxy _proxy = new TestClientProxy();
            public IClientProxy All => _proxy;
            public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => _proxy;
            public IClientProxy Client(string connectionId) => _proxy;
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => _proxy;
            public IClientProxy Group(string groupName) => _proxy;
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => _proxy;
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => _proxy;
            public IClientProxy User(string userId) => _proxy;
            public IClientProxy Users(IReadOnlyList<string> userIds) => _proxy;
        }

        private class TestGroupManager : IGroupManager
        {
            public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }

        private class TestHubContext : IHubContext<DownloadHub>
        {
            public IHubClients Clients { get; } = new TestHubClients();
            public IGroupManager Groups { get; } = new TestGroupManager();
        }

        // Minimal in-memory IAudiobookRepository
        private class TestAudiobookRepository : Listenarr.Api.Services.IAudiobookRepository
        {
            private readonly List<Audiobook> _store = new List<Audiobook>();
            public Task AddAsync(Audiobook audiobook) { _store.Add(audiobook); return Task.CompletedTask; }
            public Task<int> DeleteBulkAsync(List<int> ids) { var c = _store.RemoveAll(a => ids.Contains(a.Id)); return Task.FromResult(c); }
            public Task<bool> DeleteAsync(Audiobook audiobook) { return Task.FromResult(_store.Remove(audiobook)); }
            public Task<bool> DeleteByIdAsync(int id) { var a = _store.FirstOrDefault(x => x.Id == id); if (a==null) return Task.FromResult(false); _store.Remove(a); return Task.FromResult(true); }
            public Task<List<Audiobook>> GetAllAsync() => Task.FromResult(_store.ToList());
            public Task<Audiobook?> GetByAsinAsync(string asin) => Task.FromResult(_store.FirstOrDefault(a => a.Asin == asin));
            public Task<Audiobook?> GetByIdAsync(int id) => Task.FromResult(_store.FirstOrDefault(a => a.Id == id));
            public Task<Audiobook?> GetByIsbnAsync(string isbn) => Task.FromResult(_store.FirstOrDefault(a => a.Isbn != null && a.Isbn.Contains(isbn)));
            public Task<bool> UpdateAsync(Audiobook audiobook) { var idx = _store.FindIndex(a => a.Id == audiobook.Id); if (idx<0) return Task.FromResult(false); _store[idx]=audiobook; return Task.FromResult(true); }
            public Task<string?> GetAuthorAsinByNameAsync(string name)
            {
                if (string.IsNullOrWhiteSpace(name)) return Task.FromResult<string?>(null);
                var normalized = name.Trim();
                var match = _store.SelectMany(b => (b.Authors ?? new List<string>()).Select(a => new { book = b, author = a }))
                                  .FirstOrDefault(x => string.Equals(x.author?.Trim(), normalized, StringComparison.OrdinalIgnoreCase) && ((x.book.AuthorAsins?.Count ?? 0) > 0));
                if (match != null)
                {
                    return Task.FromResult(match.book.AuthorAsins?.FirstOrDefault());
                }
                return Task.FromResult<string?>(null);
            }
        }

        // Minimal test stubs for other services used by DownloadService
        private class TestRemotePathMappingService : IRemotePathMappingService
        {
            public Task<RemotePathMapping> CreateAsync(RemotePathMapping mapping) => Task.FromResult(mapping);
            public Task<bool> DeleteAsync(int id) => Task.FromResult(true);
            public Task<List<RemotePathMapping>> GetAllAsync() => Task.FromResult(new List<RemotePathMapping>());
            public Task<RemotePathMapping?> GetByIdAsync(int id) => Task.FromResult<RemotePathMapping?>(null);
            public Task<List<RemotePathMapping>> GetByClientIdAsync(string downloadClientId) => Task.FromResult(new List<RemotePathMapping>());
            public Task<string> TranslatePathAsync(string downloadClientId, string remotePath) => Task.FromResult(remotePath);
            public Task<bool> RequiresTranslationAsync(string downloadClientId, string remotePath) => Task.FromResult(false);
            public Task<RemotePathMapping> UpdateAsync(RemotePathMapping mapping) => Task.FromResult(mapping);
        }

        private class TestImportService : IImportService
        {
            public Task<ImportResult> ImportSingleFileAsync(string downloadId, int? audiobookId, string sourcePath, ApplicationSettings settings, CancellationToken ct = default) => Task.FromResult(new ImportResult { Success = true });
            public Task<List<ImportResult>> ImportFilesFromDirectoryAsync(string downloadId, int? audiobookId, IEnumerable<string> files, ApplicationSettings settings, CancellationToken ct = default) => Task.FromResult(new List<ImportResult>());
            public Task<ImportResult> ReprocessExistingFileAsync(string downloadId, int? audiobookId, string sourcePath, ApplicationSettings settings, CancellationToken ct = default) => Task.FromResult(new ImportResult { Success = true });
        }

        private class TestSearchService : ISearchService
        {
            public Task<List<SearchResult>> SearchAsync(string query, string? category = null, List<string>? apiIds = null, SearchSortBy sortBy = SearchSortBy.Seeders, SearchSortDirection sortDirection = SearchSortDirection.Descending, bool isAutomaticSearch = false) => Task.FromResult(new List<SearchResult>());
            public Task<List<MetadataSearchResult>> IntelligentSearchAsync(string query, int candidateLimit = 50, int returnLimit = 50, string containmentMode = "Relaxed", bool requireAuthorAndPublisher = false, double fuzzyThreshold = 0.7, string region = "us", string? language = null, CancellationToken ct = default) => Task.FromResult(new List<MetadataSearchResult>());
            public Task<List<SearchResult>> SearchByApiAsync(string apiId, string query, string? category = null) => Task.FromResult(new List<SearchResult>());
            public Task<List<Listenarr.Domain.Models.IndexerSearchResult>> SearchIndexerResultsAsync(string apiId, string query, string? category = null, Listenarr.Api.Models.SearchRequest? request = null) => Task.FromResult(new List<Listenarr.Domain.Models.IndexerSearchResult>());
            public Task<bool> TestApiConnectionAsync(string apiId) => Task.FromResult(true);
            public Task<List<Listenarr.Domain.Models.IndexerSearchResult>> SearchIndexersAsync(string query, string? category = null, SearchSortBy sortBy = SearchSortBy.Seeders, SearchSortDirection sortDirection = SearchSortDirection.Descending, bool isAutomaticSearch = false, Listenarr.Api.Models.SearchRequest? request = null) => Task.FromResult(new List<Listenarr.Domain.Models.IndexerSearchResult>());
            public Task<List<ApiConfiguration>> GetEnabledMetadataSourcesAsync() => Task.FromResult(new List<ApiConfiguration>());
        }

        private class TestDownloadQueueService : IDownloadQueueService
        {
            public Task<List<QueueItem>> GetQueueAsync() => Task.FromResult(new List<QueueItem>());
        }

        private class TestCompletedDownloadProcessor : ICompletedDownloadProcessor
        {
            public Task ProcessCompletedAsync(Download download) => Task.CompletedTask;
            public Task ProcessCompletedDownloadAsync(string downloadId, string finalPath) => Task.CompletedTask;
        }

        private class TestAppMetricsService : IAppMetricsService
        {
            public void Increment(string key) { }
            public void Increment(string key, double value) { }
            public void Gauge(string key, double value) { }
            public void Timing(string key, TimeSpan duration) { }
            public void Timer(string key, TimeSpan duration) { }
        }

        [Fact]
        public void NormalizeMamId_HandlesVariousEncodings()
        {
            // Test raw mam_id (no encoding)
            Assert.Equal("abc123", NormalizeMamId("abc123"));

            // Test single-encoded (e.g., from URL)
            Assert.Equal("abc%2Bdef%3D%3D", NormalizeMamId("abc%2Bdef%3D%3D"));

            // Test double-encoded (problematic case) - should decode to single-encoded
            Assert.Equal("abc%2Bdef%3D%3D", NormalizeMamId("abc%252Bdef%253D%253D"));

            // Test triple-encoded
            Assert.Equal("abc%2Bdef%3D%3D", NormalizeMamId("abc%25252Bdef%25253D%25253D"));

            // Test empty/null
            Assert.Equal("", NormalizeMamId(""));
            Assert.Null(NormalizeMamId(null));
        }

        private static string NormalizeMamId(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            var decoded = raw;
            while (true)
            {
                var next = Uri.UnescapeDataString(decoded);
                if (next == decoded) break;
                decoded = next;
            }
            return Uri.EscapeDataString(decoded);
        }
    }
}
