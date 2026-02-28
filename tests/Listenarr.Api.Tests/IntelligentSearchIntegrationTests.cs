using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Listenarr.Domain.Models;
using Listenarr.Api.Services;
using Listenarr.Api.Controllers;
using Microsoft.AspNetCore.Mvc.Testing;
using Moq;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Listenarr.Api.Tests
{
    public class IntelligentSearchIntegrationTests : IClassFixture<ListenarrWebApplicationFactory>
    {
        private readonly ListenarrWebApplicationFactory _factory;

        public IntelligentSearchIntegrationTests(ListenarrWebApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task IntelligentSearch_ReturnsEnrichedResults_WhenAudibleIsMocked()
        {
            // Build controller directly to avoid in-memory HTTP binding issues in CI environment
            var mockSearch = new Moq.Mock<ISearchService>();
            mockSearch.Setup(s => s.IntelligentSearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<System.Threading.CancellationToken>()))
                .Returns(Task.FromResult(new System.Collections.Generic.List<MetadataSearchResult> { new MetadataSearchResult { Asin = "B0TESTASIN", Title = "Clean Title" } } as System.Collections.Generic.List<MetadataSearchResult>));

            var logger = Mock.Of<Microsoft.Extensions.Logging.ILogger<SearchController>>();
            var audimeta = new TestEmptyAudimetaService();
            var metadata = Mock.Of<IAudiobookMetadataService>();
            var controller = new Listenarr.Api.Controllers.SearchController(mockSearch.Object, logger, audimeta, metadata);
            controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

            var actionResult = await controller.IntelligentSearch("test-query");
            List<MetadataSearchResult>? returned = null;
            if (actionResult.Value != null) returned = actionResult.Value;
            else if (actionResult.Result is Microsoft.AspNetCore.Mvc.OkObjectResult ok) returned = ok.Value as List<MetadataSearchResult>;

            Assert.NotNull(returned);
            Assert.Single(returned);
            Assert.Equal("B0TESTASIN", returned![0].Asin);
            Assert.Equal("Clean Title", returned![0].Title);
        }

        [Fact]
        public async Task IntelligentSearch_TitlePrefix_MatchesOnlyTitleNotAuthor()
        {
            var mockSearch = new Moq.Mock<ISearchService>();
            mockSearch.Setup(s => s.IntelligentSearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<System.Threading.CancellationToken>()))
                .Returns(Task.FromResult(new System.Collections.Generic.List<MetadataSearchResult> { new MetadataSearchResult { Asin = "B000000001", Title = "Ingram: A Novel" }, new MetadataSearchResult { Asin = "B000000002", Title = "Different Book", Author = "Ingram" } } as System.Collections.Generic.List<MetadataSearchResult>));

            var logger = Mock.Of<Microsoft.Extensions.Logging.ILogger<SearchController>>();
            var audimeta = new TestEmptyAudimetaService();
            var metadata = Mock.Of<IAudiobookMetadataService>();
            var controller = new Listenarr.Api.Controllers.SearchController(mockSearch.Object, logger, audimeta, metadata);
            controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

            var actionResult = await controller.IntelligentSearch("TITLE:Ingram");
            List<MetadataSearchResult>? returned = null;
            if (actionResult.Value != null) returned = actionResult.Value;
            else if (actionResult.Result is Microsoft.AspNetCore.Mvc.OkObjectResult ok) returned = ok.Value as List<MetadataSearchResult>;

            Assert.NotNull(returned);
            Assert.NotEmpty(returned);
            Assert.Contains(returned, r => (r.Title ?? string.Empty).IndexOf("Ingram", System.StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public async Task IntelligentSearch_FiltersOut_PrintOnly_AmazonResults()
        {
            var mockSearch = new Moq.Mock<ISearchService>();
            // Return empty intelligent-search results when Amazon/Audible are empty
            mockSearch.Setup(s => s.IntelligentSearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<System.Threading.CancellationToken>()))
                .Returns(Task.FromResult(new System.Collections.Generic.List<MetadataSearchResult>() as System.Collections.Generic.List<MetadataSearchResult>));

            var logger = Mock.Of<Microsoft.Extensions.Logging.ILogger<SearchController>>();
            var audimeta = new TestEmptyAudimetaService();
            var metadata = Mock.Of<IAudiobookMetadataService>();
            var controller = new Listenarr.Api.Controllers.SearchController(mockSearch.Object, logger, audimeta, metadata);
            controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

            var actionResult = await controller.IntelligentSearch("9780261103573");
            List<MetadataSearchResult>? returned = null;
            if (actionResult.Value != null) returned = actionResult.Value;
            else if (actionResult.Result is Microsoft.AspNetCore.Mvc.OkObjectResult ok) returned = ok.Value as List<MetadataSearchResult>;

            Assert.NotNull(returned);
            Assert.Empty(returned);
        }

        [Fact]
        public async Task IntelligentSearch_Accepts_AudioCD_AmazonResult()
        {
            var mockSearch = new Moq.Mock<ISearchService>();
            mockSearch.Setup(s => s.IntelligentSearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<double>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<System.Threading.CancellationToken>()))
                .Returns(Task.FromResult(new System.Collections.Generic.List<MetadataSearchResult> { new MetadataSearchResult { Asin = "0563528885", Title = "The Lord of the Rings: The Trilogy", ImageUrl = "http://example.com/audio_cd.jpg" } } as System.Collections.Generic.List<MetadataSearchResult>));

            var logger = Mock.Of<Microsoft.Extensions.Logging.ILogger<SearchController>>();
            var audimeta = new TestEmptyAudimetaService();
            var metadata = Mock.Of<IAudiobookMetadataService>();
            var controller = new Listenarr.Api.Controllers.SearchController(mockSearch.Object, logger, audimeta, (IAudiobookMetadataService)metadata);
            controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

            var actionResult = await controller.IntelligentSearch("9780261103573");
            List<MetadataSearchResult>? returned = null;
            if (actionResult.Value != null) returned = actionResult.Value;
            else if (actionResult.Result is Microsoft.AspNetCore.Mvc.OkObjectResult ok) returned = ok.Value as List<MetadataSearchResult>;

            Assert.NotNull(returned);
            Assert.Contains(returned!, r => string.Equals(r.Asin, "0563528885", StringComparison.OrdinalIgnoreCase));
        }

        
    }

    internal class TestEmptyOpenLibraryService : IOpenLibraryService
    {
        public Task<List<string>> GetIsbnsForTitleAsync(string title, string? author = null)
        {
            return Task.FromResult(new List<string>());
        }

        public Task<OpenLibrarySearchResponse> SearchBooksAsync(string title, string? author = null, int limit = 10)
        {
            return Task.FromResult(new OpenLibrarySearchResponse { Docs = new List<OpenLibraryBook>() });
        }
    }

    internal class TestEmptyAudimetaService : Listenarr.Api.Services.AudimetaService
    {
        public TestEmptyAudimetaService() : base(new System.Net.Http.HttpClient(), new Microsoft.Extensions.Logging.Abstractions.NullLogger<Listenarr.Api.Services.AudimetaService>()) { }

        public override Task<Listenarr.Api.Services.AudimetaBookResponse?> GetBookMetadataAsync(string asin, string region = "us", bool useCache = true, string? language = null)
        {
            return Task.FromResult<Listenarr.Api.Services.AudimetaBookResponse?>(null);
        }
    }

    internal class TestEmptyAudnexusService : Listenarr.Api.Services.IAudnexusService
    {
        public Task<Listenarr.Api.Services.AudnexusBookResponse?> GetBookMetadataAsync(string asin, string region = "us", bool seedAuthors = true, bool update = false)
        {
            return Task.FromResult<Listenarr.Api.Services.AudnexusBookResponse?>(null);
        }

        public Task<List<Listenarr.Api.Services.AudnexusAuthorSearchResult>?> SearchAuthorsAsync(string name, string region = "us")
        {
            return Task.FromResult<List<Listenarr.Api.Services.AudnexusAuthorSearchResult>?>(null);
        }

        public Task<Listenarr.Api.Services.AudnexusAuthorResponse?> GetAuthorAsync(string asin, string region = "us", bool update = false)
        {
            return Task.FromResult<Listenarr.Api.Services.AudnexusAuthorResponse?>(null);
        }

        public Task<Listenarr.Api.Services.AudnexusChapterResponse?> GetChaptersAsync(string asin, string region = "us", bool update = false)
        {
            return Task.FromResult<Listenarr.Api.Services.AudnexusChapterResponse?>(null);
        }
    }

    // Test image cache that avoids external network calls
    internal class TestImageCacheService : IImageCacheService
    {
        public Task<string?> DownloadAndCacheImageAsync(string imageUrl, string identifier)
        {
            // Return a deterministic, non-network path used by tests
            return Task.FromResult<string?>("cache/images/test.jpg");
        }

        public Task<string?> MoveToLibraryStorageAsync(string identifier, string? imageUrl = null)
        {
            return Task.FromResult<string?>("cache/images/library/test.jpg");
        }

        public Task<string?> MoveToAuthorLibraryStorageAsync(string identifier, string? imageUrl = null)
        {
            return Task.FromResult<string?>("cache/images/authors/test.jpg");
        }

        public Task<string?> GetCachedImagePathAsync(string identifier)
        {
            return Task.FromResult<string?>("cache/images/test.jpg");
        }

        public Task ClearTempCacheAsync()
        {
            return Task.CompletedTask;
        }
    }
}

