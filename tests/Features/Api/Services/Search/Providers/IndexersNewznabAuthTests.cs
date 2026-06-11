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
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Services.Search.Providers
{
    /// <summary>
    /// Tests for Newznab/Torznab authentication validation including XML error parsing
    /// </summary>
    public class IndexersNewznabAuthTests : BaseTests
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
        public async Task TestDraft_Newznab_EmptyApiKey_ReturnsBadRequest()
        {
            // Arrange - Server would return OK, but we should reject before sending request
            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            var handler = new CaptureHandler(resp);
            var controller = MockUtils.CreateIndexersController(_provider, handler);

            var indexer = new Indexer
            {
                Name = "Test Newznab",
                Type = "Usenet",
                Implementation = "Newznab",
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
            var controller = MockUtils.CreateIndexersController(_provider, handler);

            var indexer = new Indexer
            {
                Name = "Test Newznab",
                Type = "Usenet",
                Implementation = "Newznab",
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
            var controller = MockUtils.CreateIndexersController(_provider, handler);

            var indexer = new Indexer
            {
                Name = "Test Newznab",
                Type = "Usenet",
                Implementation = "Newznab",
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
            var controller = MockUtils.CreateIndexersController(_provider, handler);

            var indexer = new Indexer
            {
                Name = "Test Torznab",
                Type = "Torrent",
                Implementation = "Torznab",
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
            var controller = MockUtils.CreateIndexersController(_provider, handler);

            var indexer = new Indexer
            {
                Name = "Test Newznab",
                Type = "Usenet",
                Implementation = "Newznab",
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
            var controller = MockUtils.CreateIndexersController(_provider, handler);

            var indexer = new Indexer
            {
                Name = "Test Newznab",
                Type = "Usenet",
                Implementation = "Newznab",
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
            var controller = MockUtils.CreateIndexersController(_provider, handler);

            var indexer = new Indexer
            {
                Name = "Test Newznab",
                Type = "Usenet",
                Implementation = "Newznab",
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
            var controller = MockUtils.CreateIndexersController(_provider, handler);

            var indexer = new Indexer
            {
                Name = "Test Newznab",
                Type = "Usenet",
                Implementation = "Newznab",
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
            var controller = MockUtils.CreateIndexersController(_provider, handler);

            var indexer = new Indexer
            {
                Name = "Test Newznab",
                Type = "Usenet",
                Implementation = "Newznab",
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
            var controller = MockUtils.CreateIndexersController(_provider, handler);

            var indexer = new Indexer
            {
                Name = "Test Torznab",
                Type = "Torrent",
                Implementation = "Torznab",
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

            var indexer = new Indexer
            {
                Name = "Test Newznab",
                Type = "Usenet",
                Implementation = "Newznab",
                Url = "https://api.example.com/api",
                ApiKey = "bad_key",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            await _indexerRepository.AddAsync(indexer);

            var controller = MockUtils.CreateIndexersController(_provider, handler);

            // Act - Test persisted indexer
            _ = await controller.Test(indexer.Id);

            // Assert - Check failure was persisted to database
            var dbIndexer = await _indexerRepository.GetByIdAsync(indexer.Id);
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

            var indexer = new Indexer
            {
                Name = "Test Newznab",
                Type = "Usenet",
                Implementation = "Newznab",
                Url = "https://api.example.com/api",
                ApiKey = "good_key",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            await _indexerRepository.AddAsync(indexer);

            var controller = MockUtils.CreateIndexersController(_provider, handler);

            // Act
            _ = await controller.Test(indexer.Id);

            // Assert
            var dbIndexer = await _indexerRepository.GetByIdAsync(indexer.Id);
            Assert.NotNull(dbIndexer);
            Assert.True(dbIndexer!.LastTestSuccessful);
            Assert.Null(dbIndexer.LastTestError);
            Assert.NotNull(dbIndexer.LastTestedAt);
        }
    }
}
