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
using System.Net;
using Listenarr.Api.Controllers;
using Listenarr.Infrastructure.Persistence;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Api.Services.Search.Providers
{
    public class IndexersControllerTests : BaseTests
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

        [Fact]
        public async Task TestDraft_Newznab_AppendsApiKeyQueryParam()
        {
            // Arrange - in-memory db for controller ctor (persist=false path won't call DB saves)
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using var db = new ListenArrDbContext(options);
            using var loggerFactory = new LoggerFactory();
            var logger = loggerFactory.CreateLogger<IndexersController>();

            // Prepare a handler that captures outgoing requests and returns OK
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            };
            var handler = new CaptureHandler(resp);

            var controller = MockUtils.CreateIndexersController(_provider, handler);

            var indexer = new Indexer
            {
                Name = "althub",
                Type = "Usenet",
                Implementation = "Newznab",
                Url = "https://api.althub.co.za",
                ApiKey = "MY_SUPER_KEY",
                Categories = "3030",
                EnableRss = true,
                EnableAutomaticSearch = true,
                EnableInteractiveSearch = true,
                EnableAnimeStandardSearch = false,
                IsEnabled = true,
                Priority = 25,
                MinimumAge = 0,
                Retention = 0,
                MaximumSize = 0,
                AdditionalSettings = string.Empty
            };

            // Act
            await controller.TestDraft(indexer);

            // Assert
            Assert.NotNull(handler.LastRequest);
            var uri = handler.LastRequest!.RequestUri!.ToString();
            Assert.Contains("apikey=MY_SUPER_KEY", uri, StringComparison.OrdinalIgnoreCase);
            // also ensure HTTP method is GET
            Assert.Equal(HttpMethod.Get, handler.LastRequest.Method);
        }
    }
}
