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
using Listenarr.Api.Controllers;
using Listenarr.Api.Models;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;
using Listenarr.Infrastructure.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services.Search.Providers
{
    public class IndexersControllerProwlarrImportTests
    {
        private sealed class CaptureHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

            public HttpRequestMessage? LastRequest { get; private set; }

            public CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage>? responseFactory = null)
            {
                _responseFactory = responseFactory ?? (_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json")
                });
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                LastRequest = request;
                return Task.FromResult(_responseFactory(request));
            }
        }

        private sealed class ControllerHarness : IDisposable
        {
            private readonly LoggerFactory _loggerFactory;
            private readonly ListenArrDbContext _db;
            private readonly HttpClient _client;
            private readonly ConfigurationService _configurationService;

            public ControllerHarness(CaptureHandler handler)
            {
                Handler = handler;
                var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                    .UseInMemoryDatabase($"prowlarr-import-{Guid.NewGuid()}")
                    .Options;

                _db = new ListenArrDbContext(options);
                _loggerFactory = new LoggerFactory();
                _client = new HttpClient(handler);
                _configurationService = new ConfigurationService(
                    new EfApplicationSettingsRepository(_db),
                    new EfApiConfigurationRepository(_db),
                    new EfDownloadClientConfigurationRepository(_db),
                    _loggerFactory.CreateLogger<ConfigurationService>(),
                    new Mock<IUserService>().Object,
                    new Mock<IStartupConfigService>().Object);
                Controller = new IndexersController(
                    new EfIndexerRepository(_db),
                    _loggerFactory.CreateLogger<IndexersController>(),
                    _client,
                    _configurationService);
            }

            public CaptureHandler Handler { get; }
            public ListenArrDbContext Db => _db;
            public ConfigurationService ConfigurationService => _configurationService;
            public IndexersController Controller { get; }

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

        [Fact]
        public async Task ImportFromProwlarr_UsesSavedApiKey_WhenRequestOmitsApiKey()
        {
            using var harness = new ControllerHarness(new CaptureHandler());

            await harness.ConfigurationService.SaveProwlarrImportSettingsAsync(new ProwlarrImportConnectionSettings
            {
                Url = "192.168.1.10",
                Port = 4545,
                ApiKey = "saved-test-key"
            });

            var result = await harness.Controller.ImportFromProwlarr(new ProwlarrImportRequest
            {
                Url = "192.168.1.10",
                Port = 4545,
                ApiKey = string.Empty
            });

            Assert.IsType<OkObjectResult>(result);
            Assert.NotNull(harness.Handler.LastRequest);
            Assert.True(harness.Handler.LastRequest!.Headers.TryGetValues("X-Api-Key", out var apiKeyValues));
            Assert.Contains("saved-test-key", apiKeyValues!);
        }

        [Fact]
        public async Task ImportFromProwlarr_WhenRequestClearsSavedPort_DoesNotReuseStoredPort()
        {
            using var harness = new ControllerHarness(new CaptureHandler());

            await harness.ConfigurationService.SaveProwlarrImportSettingsAsync(new ProwlarrImportConnectionSettings
            {
                Url = "http://localhost",
                Port = 9696,
                ApiKey = "saved-test-key"
            });

            var result = await harness.Controller.ImportFromProwlarr(new ProwlarrImportRequest
            {
                Url = "http://localhost:7878",
                ClearPort = true,
                ApiKey = string.Empty
            });

            Assert.IsType<OkObjectResult>(result);
            Assert.NotNull(harness.Handler.LastRequest);
            Assert.Equal("http://localhost:7878/api/v1/indexer", harness.Handler.LastRequest!.RequestUri!.ToString());

            var saved = await harness.ConfigurationService.GetProwlarrImportSettingsAsync(includeSecret: true);
            Assert.Equal("http://localhost:7878", saved.Url);
            Assert.Null(saved.Port);
            Assert.Equal("saved-test-key", saved.ApiKey);
        }

        [Fact]
        public async Task ImportFromProwlarr_WhenTagFilterIsSet_ImportsMatchingTaggedIndexers_WithoutAudiobookCategories()
        {
            var indexerPayload = """
                [
                  {
                    "id": 12,
                    "name": "Tagged Indexer",
                    "protocol": "torrent",
                    "tags": [7],
                    "categories": [5000],
                    "enable": true
                  }
                ]
                """;
            var tagPayload = """
                [
                  { "id": 7, "label": "audiobooks" }
                ]
                """;

            using var harness = new ControllerHarness(new CaptureHandler(request =>
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.EndsWith("/api/v1/tag", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(tagPayload, Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(indexerPayload, Encoding.UTF8, "application/json")
                };
            }));

            var result = await harness.Controller.ImportFromProwlarr(new ProwlarrImportRequest
            {
                Url = "http://localhost",
                Port = 9696,
                ApiKey = "tag-key",
                TagFilter = "audiobooks"
            });

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.NotNull(ok.Value);

            var imported = await harness.Db.Indexers.AsNoTracking().SingleAsync();
            Assert.Equal("Tagged Indexer (Prowlarr)", imported.Name);
            Assert.Equal("Torznab", imported.Implementation);
            Assert.Equal("tag-key", imported.ApiKey);
            Assert.Equal(string.Empty, imported.Categories);
        }

        [Fact]
        public async Task ImportFromProwlarr_WhenSavedTagFilterExists_UsesItWhenRequestOmitsTagFilter()
        {
            var indexerPayload = """
                [
                  {
                    "id": 13,
                    "name": "Saved Tag Indexer",
                    "protocol": "torrent",
                    "tags": [7],
                    "categories": [5000],
                    "enable": true
                  }
                ]
                """;
            var tagPayload = """
                [
                  { "id": 7, "label": "audiobooks" }
                ]
                """;

            using var harness = new ControllerHarness(new CaptureHandler(request =>
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.EndsWith("/api/v1/tag", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(tagPayload, Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(indexerPayload, Encoding.UTF8, "application/json")
                };
            }));

            await harness.ConfigurationService.SaveProwlarrImportSettingsAsync(new ProwlarrImportConnectionSettings
            {
                Url = "http://localhost",
                Port = 9696,
                ApiKey = "saved-tag-key",
                TagFilter = "audiobooks"
            });

            var result = await harness.Controller.ImportFromProwlarr(new ProwlarrImportRequest
            {
                Url = "http://localhost",
                Port = 9696,
                ApiKey = string.Empty
            });

            Assert.IsType<OkObjectResult>(result);

            var imported = await harness.Db.Indexers.AsNoTracking().SingleAsync();
            Assert.Equal("Saved Tag Indexer (Prowlarr)", imported.Name);
            Assert.Equal("saved-tag-key", imported.ApiKey);
            Assert.Equal(string.Empty, imported.Categories);
        }

        [Fact]
        public async Task ImportFromProwlarr_WhenTagFilterNeedsTagMap_AndLookupFails_ReturnsBadGateway()
        {
            var indexerPayload = """
                [
                  {
                    "id": 15,
                    "name": "Numeric Tag Indexer",
                    "protocol": "torrent",
                    "tags": [7],
                    "categories": [5000],
                    "enable": true
                  }
                ]
                """;

            using var harness = new ControllerHarness(new CaptureHandler(request =>
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.EndsWith("/api/v1/tag", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        Content = new StringContent("Forbidden", Encoding.UTF8, "text/plain")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(indexerPayload, Encoding.UTF8, "application/json")
                };
            }));

            var result = await harness.Controller.ImportFromProwlarr(new ProwlarrImportRequest
            {
                Url = "http://localhost",
                Port = 9696,
                ApiKey = "tag-key",
                TagFilter = "audiobooks"
            });

            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal((int)HttpStatusCode.BadGateway, objectResult.StatusCode);
            Assert.Empty(await harness.Db.Indexers.AsNoTracking().ToListAsync());
        }

        [Fact]
        public async Task ImportFromProwlarr_WhenTagNamesArePresent_AndLookupFails_StillImportsMatchingIndexer()
        {
            var indexerPayload = """
                [
                  {
                    "id": 16,
                    "name": "Named Tag Indexer",
                    "protocol": "torrent",
                    "tagNames": ["audiobooks"],
                    "categories": [5000],
                    "enable": true
                  }
                ]
                """;

            using var harness = new ControllerHarness(new CaptureHandler(request =>
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.EndsWith("/api/v1/tag", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        Content = new StringContent("Forbidden", Encoding.UTF8, "text/plain")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(indexerPayload, Encoding.UTF8, "application/json")
                };
            }));

            var result = await harness.Controller.ImportFromProwlarr(new ProwlarrImportRequest
            {
                Url = "http://localhost",
                Port = 9696,
                ApiKey = "tag-key",
                TagFilter = "audiobooks"
            });

            Assert.IsType<OkObjectResult>(result);

            var imported = await harness.Db.Indexers.AsNoTracking().SingleAsync();
            Assert.Equal("Named Tag Indexer (Prowlarr)", imported.Name);
            Assert.Equal("Torznab", imported.Implementation);
        }

        [Fact]
        public async Task ImportFromProwlarr_WhenRequestClearsSavedTagFilter_FallsBackToAudiobookCategories()
        {
            var indexerPayload = """
                [
                  {
                    "id": 14,
                    "name": "Category Indexer",
                    "protocol": "usenet",
                    "tags": [7],
                    "categories": [3030],
                    "enable": true
                  }
                ]
                """;
            var tagPayload = """
                [
                  { "id": 7, "label": "audiobooks" }
                ]
                """;

            using var harness = new ControllerHarness(new CaptureHandler(request =>
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path.EndsWith("/api/v1/tag", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(tagPayload, Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(indexerPayload, Encoding.UTF8, "application/json")
                };
            }));

            await harness.ConfigurationService.SaveProwlarrImportSettingsAsync(new ProwlarrImportConnectionSettings
            {
                Url = "http://localhost",
                Port = 9696,
                ApiKey = "saved-tag-key",
                TagFilter = "audiobooks"
            });

            var result = await harness.Controller.ImportFromProwlarr(new ProwlarrImportRequest
            {
                Url = "http://localhost",
                Port = 9696,
                ApiKey = string.Empty,
                TagFilter = string.Empty
            });

            Assert.IsType<OkObjectResult>(result);

            var imported = await harness.Db.Indexers.AsNoTracking().SingleAsync();
            Assert.Equal("Category Indexer (Prowlarr)", imported.Name);
            Assert.Equal("Newznab", imported.Implementation);
            Assert.Equal("3030", imported.Categories);

            var saved = await harness.ConfigurationService.GetProwlarrImportSettingsAsync();
            Assert.Null(saved.TagFilter);
        }

        [Fact]
        public async Task ImportFromProwlarr_WhenReplacementCredentialsFail_PreservesSavedConnectionSettings()
        {
            using var harness = new ControllerHarness(new CaptureHandler(request =>
            {
                if (request.Headers.TryGetValues("X-Api-Key", out var values)
                    && values.Contains("bad-key"))
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {
                        Content = new StringContent("Unauthorized", Encoding.UTF8, "text/plain")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json")
                };
            }));

            await harness.ConfigurationService.SaveProwlarrImportSettingsAsync(new ProwlarrImportConnectionSettings
            {
                Url = "http://localhost",
                Port = 9696,
                ApiKey = "good-key",
                TagFilter = "audiobooks"
            });

            var result = await harness.Controller.ImportFromProwlarr(new ProwlarrImportRequest
            {
                Url = "http://localhost",
                Port = 9696,
                ApiKey = "bad-key",
                TagFilter = "other-tag"
            });

            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal((int)HttpStatusCode.Unauthorized, objectResult.StatusCode);

            var saved = await harness.ConfigurationService.GetProwlarrImportSettingsAsync(includeSecret: true);
            Assert.Equal("http://localhost", saved.Url);
            Assert.Equal(9696, saved.Port);
            Assert.Equal("good-key", saved.ApiKey);
            Assert.Equal("audiobooks", saved.TagFilter);
        }
    }
}
