using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Listenarr.Api.Models;
using Listenarr.Domain.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Listenarr.Api.Tests
{
    public class IndexersControllerProwlarrImportTests
    {
        private sealed class CaptureHandler : HttpMessageHandler
        {
            private readonly HttpResponseMessage _response = new(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            };

            public HttpRequestMessage? LastRequest { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                LastRequest = request;
                return Task.FromResult(_response);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _response.Dispose();
                }

                base.Dispose(disposing);
            }
        }

        private sealed class ControllerHarness : IDisposable
        {
            private readonly LoggerFactory _loggerFactory;
            private readonly ListenArrDbContext _db;
            private readonly HttpClient _client;

            public ControllerHarness(CaptureHandler handler)
            {
                Handler = handler;
                var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                    .UseInMemoryDatabase($"prowlarr-import-{Guid.NewGuid()}")
                    .Options;

                _db = new ListenArrDbContext(options);
                _loggerFactory = new LoggerFactory();
                _client = new HttpClient(handler);
                Controller = new Listenarr.Api.Controllers.IndexersController(
                    _db,
                    _loggerFactory.CreateLogger<Listenarr.Api.Controllers.IndexersController>(),
                    _client);
            }

            public CaptureHandler Handler { get; }
            public Listenarr.Api.Controllers.IndexersController Controller { get; }

            public void Dispose()
            {
                _client.Dispose();
                Handler.Dispose();
                _db.Dispose();
                _loggerFactory.Dispose();
            }
        }

        [Fact]
        public async Task ImportFromProwlarr_AcceptsEmbeddedPortInHostField_WhenSchemeOmitted()
        {
            using var harness = new ControllerHarness(new CaptureHandler());

            var result = await harness.Controller.ImportFromProwlarr(new ProwlarrImportRequest
            {
                Url = "192.168.1.10:4545",
                ApiKey = "test-key"
            });

            Assert.IsType<OkObjectResult>(result);
            Assert.NotNull(harness.Handler.LastRequest);
            Assert.Equal("http://192.168.1.10:4545/api/v1/indexer", harness.Handler.LastRequest!.RequestUri!.ToString());
        }

        [Fact]
        public async Task ImportFromProwlarr_BuildsFromHostAndSeparatePortField()
        {
            using var harness = new ControllerHarness(new CaptureHandler());

            var result = await harness.Controller.ImportFromProwlarr(new ProwlarrImportRequest
            {
                Url = "192.168.1.10",
                Port = 4545,
                ApiKey = "test-key"
            });

            Assert.IsType<OkObjectResult>(result);
            Assert.NotNull(harness.Handler.LastRequest);
            Assert.Equal("http://192.168.1.10:4545/api/v1/indexer", harness.Handler.LastRequest!.RequestUri!.ToString());
        }

        [Fact]
        public async Task ImportFromProwlarr_HonorsExplicitHttpsScheme_WhenProvided()
        {
            using var harness = new ControllerHarness(new CaptureHandler());

            var result = await harness.Controller.ImportFromProwlarr(new ProwlarrImportRequest
            {
                Url = "https://192.168.1.10",
                Port = 4545,
                ApiKey = "test-key"
            });

            Assert.IsType<OkObjectResult>(result);
            Assert.NotNull(harness.Handler.LastRequest);
            Assert.Equal("https://192.168.1.10:4545/api/v1/indexer", harness.Handler.LastRequest!.RequestUri!.ToString());
        }
    }
}
