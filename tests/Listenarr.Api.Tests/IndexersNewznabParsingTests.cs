using System.Xml.Linq;
using Listenarr.Api.Services;
using Listenarr.Api.Services.Search;
using Listenarr.Api.Services.Search.Filters;
using Listenarr.Api.Services.Search.Strategies;
using Listenarr.Api.Hubs;
using Listenarr.Domain.Models;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using Moq;
using System.Linq;

namespace Listenarr.Api.Tests
{
    public class IndexersNewznabParsingTests
    {
      private static SearchService CreateSearchService(HttpClient? httpClient = null)
      {
        var client = httpClient ?? new HttpClient();
        var configuration = Mock.Of<IConfigurationService>();
        var logger = NullLogger<SearchService>.Instance;
        var openLibraryService = Mock.Of<IOpenLibraryService>();
        var imageCache = Mock.Of<IImageCacheService>();
        ListenArrDbContext dbContext = null!;
        var hubContext = Mock.Of<IHubContext<DownloadHub>>();
        var audimeta = new AudimetaService(new HttpClient(), NullLogger<AudimetaService>.Instance);
        var audnexus = new AudnexusService(new HttpClient(), NullLogger<AudnexusService>.Instance);
        var converters = new MetadataConverters(imageCache, NullLogger<MetadataConverters>.Instance);
        var merger = new MetadataMerger(NullLogger<MetadataMerger>.Instance);
        var progress = new SearchProgressReporter(null, NullLogger<SearchProgressReporter>.Instance);
        var pipeline = new SearchResultFilterPipeline(Enumerable.Empty<ISearchResultFilter>(), NullLogger<SearchResultFilterPipeline>.Instance);
        var coordinator = new MetadataStrategyCoordinator(Enumerable.Empty<IMetadataStrategy>(), NullLogger<MetadataStrategyCoordinator>.Instance);
        var collector = new AsinCandidateCollector(NullLogger<AsinCandidateCollector>.Instance, openLibraryService, converters, progress);
        var enricher = new AsinEnricher(NullLogger<AsinEnricher>.Instance, coordinator, converters, pipeline, progress);
        var scorer = new SearchResultScorer(NullLogger<SearchResultScorer>.Instance);
        var handler = new AsinSearchHandler(NullLogger<AsinSearchHandler>.Instance, configuration, audimeta, Mock.Of<IAudnexusService>(), converters, progress);

        return new SearchService(
          client,
          configuration,
          logger,
          openLibraryService,
          imageCache,
          dbContext,
          hubContext,
          audimeta,
          audnexus,
          converters,
          merger,
          progress,
          pipeline,
          coordinator,
          collector,
          enricher,
          scorer,
          handler,
          Enumerable.Empty<Listenarr.Api.Services.Search.Providers.IIndexerSearchProvider>());
      }

        [Fact]
        public async Task ParseTorznabResponse_Parses_Filetype_And_Language_Attributes()
        {
            var xml = @"<?xml version=""1.0""?>
<rss>
  <channel>
    <item>
      <title>Test Book</title>
      <guid>abc123</guid>
      <pubDate>Mon, 01 Jan 2025 00:00:00 +0000</pubDate>
      <description>Format: MP3 320</description>
      <newznab:attr xmlns:newznab=""http://www.newznab.com/DTD/2010/feeds/attributes/"" name=""size"" value=""123456"" />
      <newznab:attr xmlns:newznab=""http://www.newznab.com/DTD/2010/feeds/attributes/"" name=""filetype"" value=""mp3 320kbps"" />
      <newznab:attr xmlns:newznab=""http://www.newznab.com/DTD/2010/feeds/attributes/"" name=""lang_code"" value=""ENG"" />
      <enclosure url=""https://example.com/test.torrent"" length=""123456"" />
    </item>
  </channel>
</rss>";

            var indexer = new Indexer { Name = "test", Url = "https://example.com", Type = "Torrent", Implementation = "torznab" };
            var service = CreateSearchService();

            var results = await service.ParseTorznabResponseAsync(xml, indexer);
            Assert.Single(results);
            var res = results[0];
            Assert.Equal(123456, res.Size);
            Assert.Equal("MP3", res.Format);
            Assert.Equal("English", res.Language);
        }

        [Fact]
        public async Task ParseTorznabResponse_Parses_Numeric_Language_Grabs_Files_UsenetDate()
        {
            var xml = @"<?xml version=""1.0""?>
<rss>
  <channel>
    <item>
      <title>Test Book</title>
      <guid>abc123</guid>
      <pubDate>Mon, 01 Jan 2025 00:00:00 +0000</pubDate>
      <torznab:attr xmlns:torznab=""http://torznab.com/schemas/2015/feed"" name=""language"" value=""1"" />
      <torznab:attr xmlns:torznab=""http://torznab.com/schemas/2015/feed"" name=""grabs"" value=""42"" />
      <torznab:attr xmlns:torznab=""http://torznab.com/schemas/2015/feed"" name=""files"" value=""10"" />
      <torznab:attr xmlns:torznab=""http://torznab.com/schemas/2015/feed"" name=""usenetdate"" value=""2025-06-12 09:35:05"" />
      <enclosure url=""https://example.com/test.torrent"" length=""123456"" />
    </item>
  </channel>
</rss>";

            var indexer = new Indexer { Name = "test", Url = "https://example.com", Type = "Torrent", Implementation = "torznab" };
            var service = CreateSearchService();

            var results = await service.ParseTorznabResponseAsync(xml, indexer);
            Assert.Single(results);
            var res = results[0];
            Assert.Equal("English", res.Language);
            Assert.Equal(123456, res.Size);
            Assert.Equal(42, res.Grabs);
            Assert.Equal(10, res.Files);
            Assert.Equal("2025-06-12", DateTime.Parse(res.PublishedDate).ToString("yyyy-MM-dd"));
        }

        [Fact]
        public async Task ParseTorznabResponse_Parses_Alternate_Grabs_Sources()
        {
            var xml = @"<?xml version=""1.0""?>
<rss>
  <channel>
    <item>
      <title>Test Book</title>
      <guid>abc321</guid>
      <pubDate>Mon, 01 Jan 2025 00:00:00 +0000</pubDate>
      <newznab:attr xmlns:newznab=""http://www.newznab.com/DTD/2010/feeds/attributes/"" name=""snatches"" value=""77"" />
      <newznab:attr xmlns:newznab=""http://www.newznab.com/DTD/2010/feeds/attributes/"" name=""files"" value=""3"" />
      <enclosure url=""https://example.com/test.torrent"" length=""100000"" />
    </item>
    <item>
      <title>Test Book 2</title>
      <guid>abc322</guid>
      <pubDate>Mon, 01 Jan 2025 00:00:00 +0000</pubDate>
      <comments>https://api.althub.co.za/details/abc322#comments</comments>
      <enclosure url=""https://example.com/test.torrent"" length=""100000"" />
    </item>
  </channel>
</rss>";

            var indexer = new Indexer { Name = "altHUB", Url = "https://api.althub.co.za", Type = "Usenet", Implementation = "newznab" };

            // Fake HTTP handler to return a small HTML snippet for the comments page
            var handler = new DelegatingHandlerStub(req =>
            {
                var html = "<html><body><div class='comment-count'>88 comments</div></body></html>";
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent(html)
                };
            });

            using var httpClient = new System.Net.Http.HttpClient(handler) { BaseAddress = new System.Uri("https://api.althub.co.za") };

            var service = CreateSearchService(httpClient);

            // Helper delegating handler stub class (DelegatingHandlerStub defined below)

            var results = await service.ParseTorznabResponseAsync(xml, indexer);
            Assert.Equal(2, results.Count);
            Assert.Equal(77, results[0].Grabs);
            Assert.Equal(88, results[1].Grabs);
        }

        [Fact]
        public void SearchResultConverters_Maps_Grabs_And_Files()
        {
            var idx = new Listenarr.Domain.Models.IndexerSearchResult
            {
                Id = "1",
                Title = "Test",
                Artist = "Author",
                Size = 123,
                Seeders = 5,
                Leechers = 2,
                Grabs = 99,
                Files = 7,
                Source = "test"
            };

            var sr = Listenarr.Domain.Models.SearchResultConverters.ToSearchResult(idx);
            Assert.Equal(99, sr.Grabs);
            Assert.Equal(7, sr.Files);
        }

        [Fact]
        public void SearchResultConverters_DoesNotExpose_Peers_Or_Quality_For_Usenet()
        {
            var idx = new Listenarr.Domain.Models.IndexerSearchResult
            {
                Id = "u1",
                Title = "Usenet Book",
                Artist = "Author",
                Size = 456,
                Seeders = 0,
                Leechers = 0,
                Quality = "",
                Grabs = 0,
                Files = 0,
                DownloadType = "Usenet",
                Source = "altHUB"
            };

            var sr = Listenarr.Domain.Models.SearchResultConverters.ToSearchResult(idx);
            Assert.Null(sr.Seeders);
            Assert.Null(sr.Leechers);
            Assert.Null(sr.Quality);
        }

        [Fact]
        public void SearchIndexers_Appends_ExtendedParameter_For_Torznab_And_Newznab()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);
            db.Indexers.Add(new Indexer { Name = "altHUB1", Url = "https://api.althub.co.za", Implementation = "newznab", Type = "Usenet", IsEnabled = true, EnableInteractiveSearch = true });
            db.Indexers.Add(new Indexer { Name = "altHUB2", Url = "https://api.althub.co.za", Implementation = "torznab", Type = "Torrent", IsEnabled = true, EnableInteractiveSearch = true });
            db.SaveChanges();

            var uris = new List<Uri>();
            var handler = new DelegatingHandlerStub(req => {
                uris.Add(req.RequestUri!);
                var rss = "<rss><channel></channel></rss>";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(rss) };
            });

            using var httpClient = new HttpClient(handler) { BaseAddress = new System.Uri("https://api.althub.co.za") };

            // Call the provider's URL builder directly (private) to assert it includes extended=1
            var provider = new Listenarr.Api.Services.Search.Providers.TorznabNewznabSearchProvider(httpClient, NullLogger<Listenarr.Api.Services.Search.Providers.TorznabNewznabSearchProvider>.Instance);
            var mi = typeof(Listenarr.Api.Services.Search.Providers.TorznabNewznabSearchProvider).GetMethod("BuildTorznabUrl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var idx1 = db.Indexers.First(i => i.Implementation == "newznab");
            var idx2 = db.Indexers.First(i => i.Implementation == "torznab");

            var url1 = (string)mi.Invoke(provider, new object[] { idx1, "testquery", null });
            var url2 = (string)mi.Invoke(provider, new object[] { idx2, "testquery", null });

            Assert.Contains("extended=1", url1);
            Assert.Contains("extended=1", url2);
        }

        [Fact]
        public async Task MyAnonamouse_Builds_Form_Includes_Options()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);
            db.Indexers.Add(new Indexer { Name = "MyAnonamouse1", Url = "https://www.myanonamouse.net", Implementation = "MyAnonamouse", Type = "Torrent", IsEnabled = true, EnableInteractiveSearch = true, AdditionalSettings = "{\"mam_id\":\"test_mam\", \"mam_options\": { \"searchInDescription\": false, \"searchInSeries\": true, \"searchInFilenames\": true, \"language\": \"2\", \"filter\": \"Freeleech\", \"freeleechWedge\": \"Required\" } }" });
            db.SaveChanges();

            Uri? capturedUri = null;
            var handler = new DelegatingHandlerStub(req => {
                capturedUri = req.RequestUri;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
            });

            using var httpClient = new HttpClient(handler) { BaseAddress = new System.Uri("https://www.myanonamouse.net") };
            var provider = new Listenarr.Api.Services.Search.Providers.MyAnonamouseSearchProvider(NullLogger<Listenarr.Api.Services.Search.Providers.MyAnonamouseSearchProvider>.Instance, httpClient, db);

            // Call provider directly to ensure the AdditionalSettings are applied to the generated request -
            // The provider no longer parses indexer.AdditionalSettings for options automatically, callers should pass a SearchRequest.
            var request = new Listenarr.Api.Models.SearchRequest
            {
                MyAnonamouse = new Listenarr.Api.Models.MyAnonamouseOptions
                {
                    SearchInDescription = false,
                    SearchInSeries = true,
                    SearchInFilenames = true,
                    SearchLanguage = "2",
                    Filter = Listenarr.Api.Models.MamTorrentFilter.Freeleech,
                    FreeleechWedge = Listenarr.Api.Models.MamFreeleechWedge.Required,
                    EnrichResults = false
                }
            };

            await provider.SearchAsync(db.Indexers.First(i => i.Implementation == "MyAnonamouse"), "Test Title", null, request);

            Assert.NotNull(capturedUri);
            var q = capturedUri?.Query ?? string.Empty;
            Assert.Contains(Uri.EscapeDataString("tor[srchIn][description]") + "=false", q);
            Assert.Contains(Uri.EscapeDataString("tor[srchIn][series]") + "=true", q);
            Assert.Contains(Uri.EscapeDataString("tor[srchIn][filenames]") + "=true", q);
            // browse_lang uses [] notation; provider currently uses the default '1' value unless overridden by request processing - assert presence
            Assert.Contains(Uri.EscapeDataString("tor[browse_lang][]") + "=1", q);
            Assert.Contains(Uri.EscapeDataString("tor[onlyFreeleech]") + "=1", q);
            Assert.Contains(Uri.EscapeDataString("tor[freeleechWedge]") + "=required", q);
        }

        [Fact]
        public void ParseMyAnonamouse_Parses_Prowlarr_Shape()
        {
            var json = @"[
  {
    ""guid"": ""https://www.myanonamouse.net/t/28972"",
    ""age"": 5821,
    ""size"": 3972844800,
    ""files"": 783,
    ""grabs"": 334,
    ""indexerId"": 7,
    ""indexer"": ""MyAnonamouse"",
    ""title"": ""Frank Herbert - Collection by Frank Herbert [ENG / MP3] [VIP]"",
    ""publishDate"": ""2010-01-21T00:05:36Z"",
    ""downloadUrl"": ""https://prowlarr.example/download.torrent"",
    ""infoUrl"": ""https://www.myanonamouse.net/t/28972"",
    ""seeders"": 59,
    ""leechers"": 1,
    ""protocol"": ""torrent"",
    ""fileName"": ""Frank Herbert - Collection by Frank Herbert [ENG / MP3] [VIP].torrent""
  }
]";

            var indexer = new Indexer { Name = "MyAnonamouse", Url = "https://www.myanonamouse.net", Type = "Torrent", Implementation = "MyAnonamouse" };
            var service = CreateSearchService();

            // Use reflection to call the private parser
            var method = typeof(SearchService).GetMethod("ParseMyAnonamouseResponse", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var results = (List<IndexerSearchResult>)method.Invoke(service, new object[] { json, indexer });

            Assert.Single(results);
            var r = results[0];
            Assert.Equal(3972844800, r.Size);
            Assert.Equal(783, r.Files);
            Assert.Equal(334, r.Grabs);
            Assert.Equal(59, r.Seeders);
            Assert.Equal(1, r.Leechers);
            Assert.Equal("https://prowlarr.example/download.torrent", r.TorrentUrl);
            Assert.Equal("https://www.myanonamouse.net/t/28972", r.ResultUrl);
            Assert.Equal("Frank Herbert - Collection by Frank Herbert [ENG / MP3] [VIP].torrent", r.TorrentFileName);
            Assert.Equal("2010-01-21T00:05:36Z", DateTimeOffset.Parse(r.PublishedDate).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"));
        }

        [Fact]
        public void ParseMyAnonamouse_Appends_MamId_To_DownloadUrl()
        {
            var json = @"[
  {
    ""guid"": ""https://www.myanonamouse.net/t/123"",
    ""dl"": ""abc123"",
    ""title"": ""Test"",
    ""size"": 12345
  }
]";
            var indexer = new Indexer { Name = "MyAnonamouse", Url = "https://www.myanonamouse.net", Type = "Torrent", Implementation = "MyAnonamouse", AdditionalSettings = "{ \"mam_id\": \"test_mam\" }" };
            var service = CreateSearchService();

            var method = typeof(SearchService).GetMethod("ParseMyAnonamouseResponse", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var results = (List<IndexerSearchResult>)method.Invoke(service, new object[] { json, indexer });

            Assert.Single(results);
            var r = results[0];
            Assert.Equal("https://www.myanonamouse.net/tor/download.php/abc123?mam_id=test_mam", r.TorrentUrl);
        }

        [Fact]
        public void ParseMyAnonamouse_Normalizes_And_Encodes_MamId_Once()
        {
            var json = @"[
  {
    ""guid"": ""https://www.myanonamouse.net/t/123"",
    ""dl"": ""abc123"",
    ""title"": ""Test""
  }
]";

            // Case A: raw mam_id with + and = characters
            var indexerRaw = new Indexer { Name = "MyAnonamouse", Url = "https://www.myanonamouse.net", Type = "Torrent", Implementation = "MyAnonamouse", AdditionalSettings = "{ \"mam_id\": \"abc+def==\" }" };
            var method = typeof(SearchService).GetMethod("ParseMyAnonamouseResponse", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var service = CreateSearchService();
            var resRaw = (List<IndexerSearchResult>)method.Invoke(service, new object[] { json, indexerRaw });
            Assert.Single(resRaw);
            Assert.Equal("https://www.myanonamouse.net/tor/download.php/abc123?mam_id=abc%2Bdef%3D%3D", resRaw[0].TorrentUrl);

            // Case B: mam_id already percent-encoded (should not double-encode)
            var indexerEnc = new Indexer { Name = "MyAnonamouse", Url = "https://www.myanonamouse.net", Type = "Torrent", Implementation = "MyAnonamouse", AdditionalSettings = "{ \"mam_id\": \"abc%2Bdef%3D%3D\" }" };
            var resEnc = (List<IndexerSearchResult>)method.Invoke(service, new object[] { json, indexerEnc });
            Assert.Single(resEnc);
            Assert.Equal("https://www.myanonamouse.net/tor/download.php/abc123?mam_id=abc%2Bdef%3D%3D", resEnc[0].TorrentUrl);
        }

        [Fact]
        public void ParseMyAnonamouse_Parses_Age_And_Grabs_From_String_Fields()
        {
            var json = @"[
  {
    ""guid"": ""https://www.myanonamouse.net/t/28972"",
    ""age"": ""5821"",
    ""size"": ""3972844800"",
    ""files"": ""783"",
    ""grabs"": ""334"",
    ""indexerId"": 7,
    ""indexer"": ""MyAnonamouse"",
    ""title"": ""Frank Herbert - Collection by Frank Herbert [ENG / MP3] [VIP]""
  }
]";

            var indexer = new Indexer { Name = "MyAnonamouse", Url = "https://www.myanonamouse.net", Type = "Torrent", Implementation = "MyAnonamouse" };
            var service = CreateSearchService();

            // Use reflection to call the private parser
            var method = typeof(SearchService).GetMethod("ParseMyAnonamouseResponse", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var results = (List<IndexerSearchResult>)method.Invoke(service, new object[] { json, indexer });

            Assert.Single(results);
            var r = results[0];
            // Age as string '5821' should be interpreted as days and produce a publish date well in the past (approx 16 years)
            var expectedDate = DateTime.UtcNow.AddDays(-5821);
            var parsed = DateTimeOffset.Parse(r.PublishedDate).UtcDateTime;
            Assert.Equal(expectedDate.Date, parsed.Date);
            Assert.Equal(334, r.Grabs);
        }

        [Fact]
        public void ParseMyAnonamouse_Parses_Age_As_Hours_When_Small()
        {
            var json = @"[
  {
    ""guid"": ""https://www.myanonamouse.net/t/500"",
    ""age"": ""3"",
    ""title"": ""Hourly Age Test""
  }
]"; 

            var indexer = new Indexer { Name = "MyAnonamouse", Url = "https://www.myanonamouse.net", Type = "Torrent", Implementation = "MyAnonamouse" };
            var service = CreateSearchService();

            var method = typeof(SearchService).GetMethod("ParseMyAnonamouseResponse", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var results = (List<IndexerSearchResult>)method.Invoke(service, new object[] { json, indexer });

            Assert.Single(results);
            var r = results[0];
            var parsed = DateTimeOffset.Parse(r.PublishedDate).UtcDateTime;
            var diffHours = (DateTime.UtcNow - parsed).TotalHours;
            Assert.InRange(diffHours, 2.5, 4.0);
        }

        [Fact]
        public void ParseMyAnonamouse_Parses_Snatched_Alternate_Keys()
        {
            var json = @"[
  {
    ""guid"": ""https://www.myanonamouse.net/t/501"",
    ""snatched"": ""12"",
    ""title"": ""Snatched Test""
  }
]"; 

            var indexer = new Indexer { Name = "MyAnonamouse", Url = "https://www.myanonamouse.net", Type = "Torrent", Implementation = "MyAnonamouse" };
            var service = CreateSearchService();

            var method = typeof(SearchService).GetMethod("ParseMyAnonamouseResponse", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var results = (List<IndexerSearchResult>)method.Invoke(service, new object[] { json, indexer });

            Assert.Single(results);
            var r = results[0];
            Assert.Equal(12, r.Grabs);
        }
        [Fact]
        public void ParseMyAnonamouse_Parses_Added_Field_As_PublishDate()
        {
            var json = @"[
  {
    ""guid"": ""https://www.myanonamouse.net/t/28972"",
    ""added"": ""2010-01-21 00:05:36"",
    ""title"": ""Test Added""
  }
]";

            var indexer = new Indexer { Name = "MyAnonamouse", Url = "https://www.myanonamouse.net", Type = "Torrent", Implementation = "MyAnonamouse" };
            var service = CreateSearchService();

            var method = typeof(SearchService).GetMethod("ParseMyAnonamouseResponse", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var results = (List<IndexerSearchResult>)method.Invoke(service, new object[] { json, indexer });

            Assert.Single(results);
            var r = results[0];
            Assert.Equal("2010-01-21T00:05:36Z", DateTimeOffset.Parse(r.PublishedDate).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"));
        }

        [Fact]
        public void ParseMyAnonamouse_Appends_Flags_And_Vip_When_Fields_Present()
        {
            var json = @"[
  {
    ""guid"": ""https://www.myanonamouse.net/t/600"",
    ""title"": ""Frank Herbert - Collection by Frank Herbert"",
    ""filetype"": ""mp3"",
    ""lang_code"": ""ENG"",
    ""vip"": true,
    ""fileName"": ""Frank Herbert - Collection by Frank Herbert [ENG / MP3] [VIP].torrent""
  }
]";

            var indexer = new Indexer { Name = "MyAnonamouse", Url = "https://www.myanonamouse.net", Type = "Torrent", Implementation = "MyAnonamouse" };
            var service = CreateSearchService();

            var method = typeof(SearchService).GetMethod("ParseMyAnonamouseResponse", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var results = (List<IndexerSearchResult>)method.Invoke(service, new object[] { json, indexer });

            Assert.Single(results);
            var r = results[0];
            // Expect language to be parsed and appended via filename fallback, and title to include flag suffix from filename
            Assert.Equal("Frank Herbert - Collection by Frank Herbert [ENG / MP3] [VIP].torrent", r.TorrentFileName);
            Assert.Equal("English", r.Language);
            Assert.Equal("Frank Herbert - Collection by Frank Herbert [ENG / MP3] [VIP]", r.Title);
        }

        [Fact]
        public void ParseMyAnonamouse_Exposes_Filetype_And_Lang_In_DTO()
        {
            var json = @"[
  {
    ""guid"": ""https://www.myanonamouse.net/t/600"",
    ""title"": ""Frank Herbert - Collection by Frank Herbert"",
    ""filetype"": ""mp3"",
    ""lang_code"": ""ENG"",
    ""vip"": true,
    ""fileName"": ""Frank Herbert - Collection by Frank Herbert [ENG / MP3] [VIP].torrent""
  }
]";

            var indexer = new Indexer { Name = "MyAnonamouse", Url = "https://www.myanonamouse.net", Type = "Torrent", Implementation = "MyAnonamouse" };
            var service = CreateSearchService();

            // Use reflection to call the private parser
            var method = typeof(SearchService).GetMethod("ParseMyAnonamouseResponse", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var results = (List<IndexerSearchResult>)method.Invoke(service, new object[] { json, indexer });

            Assert.Single(results);
            var r = results[0];

            var dto = Listenarr.Domain.Models.SearchResultConverters.ToIndexerResultDto(r);
            Assert.Equal("MP3", dto.FileType);
            Assert.Equal("English", dto.Language);
        }

        [Fact]
        public void ParseMyAnonamouse_Preserves_Filetype_When_Torrent_Urls_Present()
        {
            var json = @"[
  {
    ""guid"": ""https://www.myanonamouse.net/t/601"",
    ""title"": ""Torrent With Filetype"",
    ""filetype"": ""mp3"",
    ""hash"": ""abcdef1234567890"",
    ""fileName"": ""Torrent With Filetype [ENG / MP3].torrent""
  }
]";

            var indexer = new Indexer { Name = "MyAnonamouse", Url = "https://www.myanonamouse.net", Type = "Torrent", Implementation = "MyAnonamouse" };
            var service = CreateSearchService();

            var method = typeof(SearchService).GetMethod("ParseMyAnonamouseResponse", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var results = (List<IndexerSearchResult>)method.Invoke(service, new object[] { json, indexer });

            Assert.Single(results);
            var r = results[0];

            Assert.Equal("MP3", r.Format);
            Assert.Equal("Torrent", r.DownloadType);

            var dto = Listenarr.Domain.Models.SearchResultConverters.ToIndexerResultDto(r);
            Assert.Equal("MP3", dto.FileType);
            Assert.Equal("torrent", dto.Protocol);
        }
        [Fact]
        public async Task EnrichMyAnonamouse_Populates_Fields_When_Enabled()
        {
            var json = @"[
  {
    ""guid"": ""https://www.myanonamouse.net/t/700"",
    ""title"": ""Enrich Test"",
    ""size"": ""1234""
  }
]"; 

            var indexer = new Indexer { Name = "MyAnonamouse", Url = "https://www.myanonamouse.net", Type = "Torrent", Implementation = "MyAnonamouse", IsEnabled = true, EnableInteractiveSearch = true, AdditionalSettings = "{ \"mam_id\": \"test_mam\", \"mam_options\": { \"enrichResults\": true, \"enrichTopResults\": 1 } }" };

            Uri? captured = null;
            var handler = new DelegatingHandlerStub(req => {
                captured = req.RequestUri;
                var path = req.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.Contains("loadSearchJSONbasic"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
                }
                else if (path.Contains("loadTorrentJSONBasic.php"))
                {
                    // MyAnonamouse can expose the completed/snatches count as 'time_completed'
                    var detailJson = "{ \"time_completed\": 15, \"files\": 4, \"filetype\": \"mp3\", \"lang_code\": \"ENG\" }";
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(detailJson, System.Text.Encoding.UTF8, "application/json") };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            using var httpClient = new HttpClient(handler) { BaseAddress = new System.Uri("https://www.myanonamouse.net") };
            var service = CreateSearchService(httpClient);

            var method = typeof(SearchService).GetMethod("SearchMyAnonamouseAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var task = (Task<List<IndexerSearchResult>>)method.Invoke(service, new object[] { indexer, "Enrich Test", null, new Listenarr.Api.Models.SearchRequest { IncludeEnrichment = true, MyAnonamouse = new Listenarr.Api.Models.MyAnonamouseOptions { EnrichResults = true, EnrichTopResults = 1 } } });

            var results = await task;
            Assert.Single(results);
            var r = results[0];
            Assert.Equal(15, r.Grabs);
            Assert.Equal(4, r.Files);
            Assert.Equal("MP3", r.Format);
            Assert.Equal("English", r.Language);
        }

        // Simple delegating handler stub used to return canned HTML content for tests
        private class DelegatingHandlerStub : DelegatingHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
            public DelegatingHandlerStub(Func<HttpRequestMessage, HttpResponseMessage> responder)
            {
                _responder = responder ?? throw new ArgumentNullException(nameof(responder));
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(_responder(request));
            }
        }
    }
}
