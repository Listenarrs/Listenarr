using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Listenarr.Domain.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Listenarr.Api.Tests
{
    public class IndexersAuthTests
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

        private Listenarr.Api.Controllers.IndexersController CreateController(HttpMessageHandler handler)
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var db = new ListenArrDbContext(options);
            var logger = NullLogger<Listenarr.Api.Controllers.IndexersController>.Instance;
            var client = new HttpClient(handler);

            return new Listenarr.Api.Controllers.IndexersController(db, logger, client);
        }

        [Fact]
        public async Task TestDraft_Newznab_InvalidApiKey_ReturnsBadRequestAndMarksFailed()
        {
            // Arrange - server responds with 403 and message indicating invalid API key
            var resp = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("Invalid API key")
            };
            var handler = new CaptureHandler(resp);
            var controller = CreateController(handler);

            var indexer = new Indexer
            {
                Name = "althub",
                Type = "Usenet",
                Implementation = "Newznab",
                Url = "https://api.althub.co.za",
                ApiKey = "BAD_KEY",
            };

            // Act
            var result = await controller.TestDraft(indexer);

            // Assert: the handler was used and the request contained apikey param
            Assert.NotNull(handler.LastRequest);
            var uri = handler.LastRequest!.RequestUri!.ToString();
            Assert.Contains("apikey=BAD_KEY", uri, StringComparison.OrdinalIgnoreCase);
            // Result should be 400 BadRequest because we treat auth errors as failures
            Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(result);
            // The in-memory provided indexer should have been updated to reflect failure (persist=false still updates instance)
            Assert.False(indexer.LastTestSuccessful);
            Assert.NotNull(indexer.LastTestError);
        }

        [Fact]
        public async Task TestDraft_Newznab_ValidApiKey_ReturnsOkAndMarksSuccess()
        {
            // Arrange - server responds with 200 OK and some valid payload
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{ \"result\": true }")
            };
            var handler = new CaptureHandler(resp);
            var controller = CreateController(handler);

            var indexer = new Indexer
            {
                Name = "althub",
                Type = "Usenet",
                Implementation = "Newznab",
                Url = "https://api.althub.co.za",
                ApiKey = "GOOD_KEY",
            };

            // Act
            var result = await controller.TestDraft(indexer);

            // Assert
            Assert.NotNull(handler.LastRequest);
            var uri = handler.LastRequest!.RequestUri!.ToString();
            Assert.Contains("apikey=GOOD_KEY", uri, StringComparison.OrdinalIgnoreCase);
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
            Assert.True(indexer.LastTestSuccessful);
            Assert.Null(indexer.LastTestError);
        }

        [Fact]
        public async Task TestDraft_GenericIndexer_RemoteCaller_AllowsPrivateHost()
        {
            // Arrange - simulate a remote caller testing a private-network indexer URL.
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}")
            };
            var handler = new CaptureHandler(resp);
            var controller = CreateController(handler);

            var httpContext = new DefaultHttpContext();
            httpContext.Connection.RemoteIpAddress = IPAddress.Parse("8.8.8.8");
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

            var indexer = new Indexer
            {
                Name = "private-indexer",
                Type = "Usenet",
                Implementation = "Generic",
                Url = "http://192.168.1.25"
            };

            // Act
            var result = await controller.TestDraft(indexer);

            // Assert
            Assert.NotNull(handler.LastRequest);
            Assert.Equal("192.168.1.25", handler.LastRequest!.RequestUri!.Host);
            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.NotNull(ok.Value);
            var payload = ok.Value!;
            var successProp = payload.GetType().GetProperty("success");
            Assert.NotNull(successProp);
            Assert.True((bool)successProp!.GetValue(payload)!);
            Assert.True(indexer.LastTestSuccessful);
        }

        [Fact]
        public async Task TestDraft_GenericIndexer_PrivateNetworkCaller_AllowsPrivateHost()
        {
            // Arrange - trusted private-network caller can test private-network indexer URL.
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}")
            };
            var handler = new CaptureHandler(resp);
            var controller = CreateController(handler);

            var httpContext = new DefaultHttpContext();
            httpContext.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.20");
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

            var indexer = new Indexer
            {
                Name = "private-indexer",
                Type = "Usenet",
                Implementation = "Generic",
                Url = "http://192.168.1.25"
            };

            // Act
            var result = await controller.TestDraft(indexer);

            // Assert
            Assert.NotNull(handler.LastRequest);
            Assert.Equal("192.168.1.25", handler.LastRequest!.RequestUri!.Host);
            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.NotNull(ok.Value);
            var payload = ok.Value!;
            var successProp = payload.GetType().GetProperty("success");
            Assert.NotNull(successProp);
            Assert.True((bool)successProp!.GetValue(payload)!);
            Assert.True(indexer.LastTestSuccessful);
        }
    }
}
