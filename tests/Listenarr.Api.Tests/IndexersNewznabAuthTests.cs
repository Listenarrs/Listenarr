using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Listenarr.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Listenarr.Api.Tests
{
    /// <summary>
    /// Tests for Newznab/Torznab authentication validation including XML error parsing
    /// </summary>
    public class IndexersNewznabAuthTests
    {
        private sealed class ControllerContext : IDisposable
        {
            public ControllerContext(ListenArrDbContext db, HttpClient client, Listenarr.Api.Controllers.IndexersController controller)
            {
                Db = db;
                Client = client;
                Controller = controller;
            }

            public ListenArrDbContext Db { get; }

            public HttpClient Client { get; }

            public Listenarr.Api.Controllers.IndexersController Controller { get; }

            public void Dispose()
            {
                Client.Dispose();
                Db.Dispose();
            }
        }

        private class CaptureHandler : HttpMessageHandler
        {
            public HttpRequestMessage? LastRequest { get; private set; }
            private readonly HttpResponseMessage _response;

            public CaptureHandler(HttpResponseMessage response)
            {
                _response = response;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                LastRequest = request;
                return Task.FromResult(_response);
            }
        }

        private static ControllerContext CreateController(HttpMessageHandler handler)
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var db = new ListenArrDbContext(options);
            var client = new HttpClient(handler);
            var controller = new Listenarr.Api.Controllers.IndexersController(
                db,
                NullLogger<Listenarr.Api.Controllers.IndexersController>.Instance,
                client,
                new TestConfigurationService());

            return new ControllerContext(db, client, controller);
        }

        [Fact]
        public async Task TestDraft_Newznab_EmptyApiKey_ReturnsBadRequest()
        {
            // Arrange - Server would return OK, but we should reject before sending request
            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            var handler = new CaptureHandler(resp);
            using var context = CreateController(handler);
            var controller = context.Controller;

            var indexer = new Indexer
            {
                Name = "Test Newznab",
                Protocol = DownloadProtocol.Usenet,
                Implementation = Implementation.Newznab,
                Url = "https://api.example.com/api",
                ApiKey = "", // Empty API key
            };

            // Act
            var result = await controller.TestDraft(indexer);

            // Assert: Should fail before making request
            Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(result);
            Assert.False(indexer.LastTestSuccessful);
            Assert.Contains("API key is required", indexer.LastTestError ?? string.Empty);
            // Handler should NOT have been called
            Assert.Null(handler.LastRequest);
        }

        [Fact]
        public async Task TestDraft_Newznab_NullApiKey_ReturnsBadRequest()
        {
            // Arrange
            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            var handler = new CaptureHandler(resp);
            using var context = CreateController(handler);
            var controller = context.Controller;

            var indexer = new Indexer
            {
                Name = "Test Newznab",
                Protocol = DownloadProtocol.Usenet,
                Implementation = Implementation.Newznab,
                Url = "https://api.example.com/api",
                ApiKey = null, // Null API key
            };

            // Act
            var result = await controller.TestDraft(indexer);

            // Assert
            Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(result);
            Assert.False(indexer.LastTestSuccessful);
            Assert.Contains("API key is required", indexer.LastTestError ?? string.Empty);
            Assert.Null(handler.LastRequest);
        }

        [Fact]
        public async Task TestDraft_Newznab_WhitespaceApiKey_ReturnsBadRequest()
        {
            // Arrange
            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            var handler = new CaptureHandler(resp);
            using var context = CreateController(handler);
            var controller = context.Controller;

            var indexer = new Indexer
            {
                Name = "Test Newznab",
                Protocol = DownloadProtocol.Usenet,
                Implementation = Implementation.Newznab,
                Url = "https://api.example.com/api",
                ApiKey = "   ", // Whitespace-only API key
            };

            // Act
            var result = await controller.TestDraft(indexer);

            // Assert
            Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(result);
            Assert.False(indexer.LastTestSuccessful);
            Assert.Contains("API key is required", indexer.LastTestError ?? string.Empty);
            Assert.Null(handler.LastRequest);
        }

        [Fact]
        public async Task TestDraft_Torznab_EmptyApiKey_ReturnsBadRequest()
        {
            // Arrange - Torznab should also require API key
            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            var handler = new CaptureHandler(resp);
            using var context = CreateController(handler);
            var controller = context.Controller;

            var indexer = new Indexer
            {
                Name = "Test Torznab",
                Protocol = DownloadProtocol.Torrent,
                Implementation = Implementation.Torznab,
                Url = "https://api.example.com/api",
                ApiKey = "",
            };

            // Act
            var result = await controller.TestDraft(indexer);

            // Assert
            Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(result);
            Assert.False(indexer.LastTestSuccessful);
            Assert.Null(handler.LastRequest);
        }

        [Fact]
        public async Task TestDraft_Newznab_UsesSearchEndpointForStricterAuth()
        {
            // Arrange - Return valid RSS XML (search result)
            var validXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<rss version=""2.0"">
    <channel>
        <title>Test Indexer</title>
        <item>
            <title>Test Item</title>
        </item>
    </channel>
</rss>";
            
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(validXml, System.Text.Encoding.UTF8, "application/xml")
            };
            var handler = new CaptureHandler(resp);
            using var context = CreateController(handler);
            var controller = context.Controller;

            var indexer = new Indexer
            {
                Name = "Test Newznab",
                Protocol = DownloadProtocol.Usenet,
                Implementation = Implementation.Newznab,
                Url = "https://api.example.com/api",
                ApiKey = "test_key",
            };

            // Act
            var result = await controller.TestDraft(indexer);

            // Assert: Request should use t=search endpoint (stricter auth than t=caps)
            Assert.NotNull(handler.LastRequest);
            var uri = handler.LastRequest!.RequestUri!.ToString();
            Assert.Contains("t=search", uri, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("apikey=test_key", uri, StringComparison.OrdinalIgnoreCase);
            
            // Should succeed
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
            Assert.True(indexer.LastTestSuccessful);
        }

        [Fact]
        public async Task TestDraft_Newznab_XmlErrorWithInvalidApiKey_ReturnsBadRequest()
        {
            // Arrange - Newznab/Torznab typically returns 200 OK with error XML when API key is invalid
            var errorXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<error code=""100"" description=""Invalid API Key"" />";
            
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(errorXml, System.Text.Encoding.UTF8, "application/xml")
            };
            var handler = new CaptureHandler(resp);
            using var context = CreateController(handler);
            var controller = context.Controller;

            var indexer = new Indexer
            {
                Name = "Test Newznab",
                Protocol = DownloadProtocol.Usenet,
                Implementation = Implementation.Newznab,
                Url = "https://api.example.com/api",
                ApiKey = "bad_key",
            };

            // Act
            var result = await controller.TestDraft(indexer);

            // Assert: Should detect authentication failure from XML error
            Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(result);
            Assert.False(indexer.LastTestSuccessful);
            Assert.Contains("Invalid API Key", indexer.LastTestError ?? string.Empty);
        }

        [Fact]
        public async Task TestDraft_Newznab_XmlErrorWithUnauthorized_ReturnsBadRequest()
        {
            // Arrange - Error with "unauthorized" in description
            var errorXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<error code=""101"" description=""Unauthorized access"" />";
            
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(errorXml, System.Text.Encoding.UTF8, "application/xml")
            };
            var handler = new CaptureHandler(resp);
            using var context = CreateController(handler);
            var controller = context.Controller;

            var indexer = new Indexer
            {
                Name = "Test Newznab",
                Protocol = DownloadProtocol.Usenet,
                Implementation = Implementation.Newznab,
                Url = "https://api.example.com/api",
                ApiKey = "bad_key",
            };

            // Act
            var result = await controller.TestDraft(indexer);

            // Assert
            Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(result);
            Assert.False(indexer.LastTestSuccessful);
            Assert.Contains("Unauthorized", indexer.LastTestError ?? string.Empty);
        }

        [Fact]
        public async Task TestDraft_Newznab_HttpForbidden_DetectsAuthFailure()
        {
            // Arrange - Some indexers return 403 Forbidden for invalid API keys
            var resp = new HttpResponseMessage(HttpStatusCode.Forbidden);
            var handler = new CaptureHandler(resp);
            using var context = CreateController(handler);
            var controller = context.Controller;

            var indexer = new Indexer
            {
                Name = "Test Newznab",
                Protocol = DownloadProtocol.Usenet,
                Implementation = Implementation.Newznab,
                Url = "https://api.example.com/api",
                ApiKey = "bad_key",
            };

            // Act
            var result = await controller.TestDraft(indexer);

            // Assert
            Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(result);
            Assert.False(indexer.LastTestSuccessful);
            Assert.Contains("Authentication failed", indexer.LastTestError ?? string.Empty);
        }

        [Fact]
        public async Task TestDraft_Newznab_HttpUnauthorized_DetectsAuthFailure()
        {
            // Arrange - Some indexers return 401 Unauthorized
            var resp = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            var handler = new CaptureHandler(resp);
            using var context = CreateController(handler);
            var controller = context.Controller;

            var indexer = new Indexer
            {
                Name = "Test Newznab",
                Protocol = DownloadProtocol.Usenet,
                Implementation = Implementation.Newznab,
                Url = "https://api.example.com/api",
                ApiKey = "bad_key",
            };

            // Act
            var result = await controller.TestDraft(indexer);

            // Assert
            Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(result);
            Assert.False(indexer.LastTestSuccessful);
            Assert.Contains("Authentication failed", indexer.LastTestError ?? string.Empty);
        }

        [Fact]
        public async Task TestDraft_Torznab_UsesSearchEndpointForStricterAuth()
        {
            // Arrange - Torznab should behave the same as Newznab
            var validXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<rss version=""2.0"">
    <channel>
        <title>Test Torznab</title>
    </channel>
</rss>";
            
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(validXml, System.Text.Encoding.UTF8, "application/xml")
            };
            var handler = new CaptureHandler(resp);
            using var context = CreateController(handler);
            var controller = context.Controller;

            var indexer = new Indexer
            {
                Name = "Test Torznab",
                Protocol = DownloadProtocol.Torrent,
                Implementation = Implementation.Torznab,
                Url = "https://api.example.com/api",
                ApiKey = "test_key",
            };

            // Act
            var result = await controller.TestDraft(indexer);

            // Assert
            Assert.NotNull(handler.LastRequest);
            var uri = handler.LastRequest!.RequestUri!.ToString();
            Assert.Contains("t=search", uri, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("apikey=test_key", uri, StringComparison.OrdinalIgnoreCase);
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
        }

        [Fact]
        public async Task TestPersisted_Newznab_InvalidApiKey_PersistsFailureToDatabase()
        {
            // Arrange - Create a persisted indexer in database
            var errorXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<error code=""100"" description=""Invalid API Key"" />";
            
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(errorXml, System.Text.Encoding.UTF8, "application/xml")
            };
            var handler = new CaptureHandler(resp);
            
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var db = new ListenArrDbContext(options);
            var indexer = new Indexer
            {
                Name = "Test Newznab",
                Protocol = DownloadProtocol.Usenet,
                Implementation = Implementation.Newznab,
                Url = "https://api.example.com/api",
                ApiKey = "bad_key",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Indexers.Add(indexer);
            await db.SaveChangesAsync();

            using var client = new HttpClient(handler);
            var controller = new Listenarr.Api.Controllers.IndexersController(
                db,
                NullLogger<Listenarr.Api.Controllers.IndexersController>.Instance,
                client,
                new TestConfigurationService());

            // Act - Test persisted indexer
            _ = await controller.Test(indexer.Id);

            // Assert - Check failure was persisted to database
            var dbIndexer = await db.Indexers.FindAsync(indexer.Id);
            Assert.NotNull(dbIndexer);
            Assert.False(dbIndexer!.LastTestSuccessful);
            Assert.Contains("Invalid API Key", dbIndexer.LastTestError ?? string.Empty);
            Assert.NotNull(dbIndexer.LastTestedAt);
        }

        [Fact]
        public async Task TestPersisted_Newznab_ValidApiKey_PersistsSuccessToDatabase()
        {
            // Arrange
            var validXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<rss version=""2.0"">
    <channel>
        <title>Test Indexer</title>
    </channel>
</rss>";
            
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(validXml, System.Text.Encoding.UTF8, "application/xml")
            };
            var handler = new CaptureHandler(resp);
            
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var db = new ListenArrDbContext(options);
            var indexer = new Indexer
            {
                Name = "Test Newznab",
                Protocol = DownloadProtocol.Usenet,
                Implementation = Implementation.Newznab,
                Url = "https://api.example.com/api",
                ApiKey = "good_key",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Indexers.Add(indexer);
            await db.SaveChangesAsync();

            using var client = new HttpClient(handler);
            var controller = new Listenarr.Api.Controllers.IndexersController(
                db,
                NullLogger<Listenarr.Api.Controllers.IndexersController>.Instance,
                client,
                new TestConfigurationService());

            // Act
            _ = await controller.Test(indexer.Id);

            // Assert
            var dbIndexer = await db.Indexers.FindAsync(indexer.Id);
            Assert.NotNull(dbIndexer);
            Assert.True(dbIndexer!.LastTestSuccessful);
            Assert.Null(dbIndexer.LastTestError);
            Assert.NotNull(dbIndexer.LastTestedAt);
        }
    }
}
