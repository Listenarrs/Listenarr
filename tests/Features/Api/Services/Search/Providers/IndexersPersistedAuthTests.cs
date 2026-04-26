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
using Listenarr.Domain.Models;
using Listenarr.Tests.Common;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services.Search.Providers
{
    public class IndexersPersistedAuthTests : BaseTests
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
        public async Task TestPersisted_Newznab_InvalidApiKey_PersistsFailure()
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("Invalid API key")
            };

            var persisted = new Indexer
            {
                Name = "althub",
                Type = "Usenet",
                Implementation = "Newznab",
                Url = "https://api.althub.co.za",
                ApiKey = "BAD_KEY",
            };
            await _indexerRepository.AddAsync(persisted);

            var handler = new CaptureHandler(resp);
            var controller = MockUtils.CreateIndexersController(_provider, handler);

            var actionResult = await controller.Test(persisted.Id);

            // DB indexer should be updated
            var updated = await _indexerRepository.GetByIdAsync(persisted.Id);
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
                Type = "Usenet",
                Implementation = "Newznab",
                Url = "https://api.althub.co.za",
                ApiKey = "GOOD_KEY",
            };
            await _indexerRepository.AddAsync(persisted);

            var handler = new CaptureHandler(resp);
            var controller = MockUtils.CreateIndexersController(_provider, handler);

            var actionResult = await controller.Test(persisted.Id);

            var updated = await _indexerRepository.GetByIdAsync(persisted.Id);
            Assert.NotNull(handler.LastRequest);
            Assert.Contains("apikey=GOOD_KEY", handler.LastRequest!.RequestUri!.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.True(updated!.LastTestSuccessful);
            Assert.Null(updated.LastTestError);
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(actionResult);
        }
    }
}
