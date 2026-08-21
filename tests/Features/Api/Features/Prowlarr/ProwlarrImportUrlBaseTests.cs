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
using System.Text;
using Listenarr.Api.Dtos;
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Tests.Features.Api.Features.Prowlarr
{
    [Trait("Name", "ProwlarrImportUrlBaseTests")]
    [Trait("Category", "Api")]
    public class ProwlarrImportUrlBaseTests : BaseTests
    {
        private const string IndexerPayload = """
            [
              {
                "id": 4,
                "name": "Example Indexer",
                "protocol": "usenet",
                "categories": [3030],
                "enable": true
              }
            ]
            """;

        /// <summary>
        /// Stands in for a Prowlarr instance configured with a URL base: anything requested outside that
        /// base is answered with a redirect onto it, exactly as the reported deployment behaved.
        /// </summary>
        private sealed class UrlBaseRedirectHandler : HttpMessageHandler
        {
            private readonly string _urlBase;

            public UrlBaseRedirectHandler(string urlBase) => _urlBase = urlBase;

            public List<Uri> Requests { get; } = new();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri!;
                Requests.Add(uri);

                if (!uri.AbsolutePath.StartsWith(_urlBase + "/", StringComparison.Ordinal))
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                    redirect.Headers.Location = new Uri(_urlBase + uri.PathAndQuery, UriKind.Relative);
                    return Task.FromResult(redirect);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(IndexerPayload, Encoding.UTF8, "application/json")
                });
            }
        }

        [Fact]
        public async Task ImportFromProwlarr_WhenDiscoveryIsRedirectedOntoAUrlBase_StoresProxyUrlsUnderThatBase()
        {
            var handler = new UrlBaseRedirectHandler("/prowlarr");
            var controller = MockUtils.CreateIndexersController(_provider, handler);

            var result = await controller.ImportFromProwlarr(new ProwlarrImportRequestDto
            {
                Url = "http://prowlarr.example:9696",
                ApiKey = "test-key"
            });

            Assert.IsType<OkObjectResult>(result);
            Assert.Contains(handler.Requests, uri => uri.AbsolutePath == "/prowlarr/api/v1/indexer");

            var imported = Assert.Single(await _indexerRepository.GetAllAsync());
            Assert.Equal("http://prowlarr.example:9696/prowlarr/4/api", imported.Url);
        }

        [Fact]
        public async Task ImportFromProwlarr_WhenDiscoveryIsNotRedirected_KeepsTheSuppliedBase()
        {
            var handler = new UrlBaseRedirectHandler(string.Empty);
            var controller = MockUtils.CreateIndexersController(_provider, handler);

            var result = await controller.ImportFromProwlarr(new ProwlarrImportRequestDto
            {
                Url = "http://prowlarr.example:9696",
                ApiKey = "test-key"
            });

            Assert.IsType<OkObjectResult>(result);

            var imported = Assert.Single(await _indexerRepository.GetAllAsync());
            Assert.Equal("http://prowlarr.example:9696/4/api", imported.Url);
        }
    }
}
