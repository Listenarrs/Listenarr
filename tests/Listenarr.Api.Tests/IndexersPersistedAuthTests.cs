using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Listenarr.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Listenarr.Api.Tests
{
    public class IndexersPersistedAuthTests
    {
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

        private (ListenArrDbContext db, Listenarr.Api.Controllers.IndexersController controller, CaptureHandler handler) CreateControllerWithPersistedIndexer(HttpResponseMessage httpResponse, Indexer persisted)
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var db = new ListenArrDbContext(options);
            // persist the indexer first
            db.Indexers.Add(persisted);
            db.SaveChanges();

            using var loggerFactory = new LoggerFactory();
            var logger = loggerFactory.CreateLogger<Listenarr.Api.Controllers.IndexersController>();
            var handler = new CaptureHandler(httpResponse);
            var client = new HttpClient(handler);
            var controller = new Listenarr.Api.Controllers.IndexersController(db, logger, client, new TestConfigurationService());

            return (db, controller, handler);
        }

        [Fact]
        public async Task TestPersisted_Newznab_InvalidApiKey_PersistsFailure()
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("Invalid API key")
            };

            var persisted = new Indexer
            {
                Name = "althub",
                Protocol = DownloadProtocol.Usenet,
                Implementation = Implementation.Newznab,
                Url = "https://api.althub.co.za",
                ApiKey = "BAD_KEY",
            };

            var (db, controller, handler) = CreateControllerWithPersistedIndexer(resp, persisted);

            var actionResult = await controller.Test(persisted.Id);

            // DB indexer should be updated
            var updated = await db.Indexers.FindAsync(persisted.Id);
            Assert.NotNull(handler.LastRequest);
            Assert.Contains("apikey=BAD_KEY", handler.LastRequest!.RequestUri!.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.False(updated!.LastTestSuccessful);
            Assert.NotNull(updated.LastTestError);
            Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(actionResult);
        }

        [Fact]
        public async Task TestPersisted_Newznab_ValidApiKey_PersistsSuccess()
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{ \"result\": true }")
            };

            var persisted = new Indexer
            {
                Name = "althub",
                Protocol = DownloadProtocol.Usenet,
                Implementation = Implementation.Newznab,
                Url = "https://api.althub.co.za",
                ApiKey = "GOOD_KEY",
            };

            var (db, controller, handler) = CreateControllerWithPersistedIndexer(resp, persisted);

            var actionResult = await controller.Test(persisted.Id);

            var updated = await db.Indexers.FindAsync(persisted.Id);
            Assert.NotNull(handler.LastRequest);
            Assert.Contains("apikey=GOOD_KEY", handler.LastRequest!.RequestUri!.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.True(updated!.LastTestSuccessful);
            Assert.Null(updated.LastTestError);
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(actionResult);
        }
    }
}
