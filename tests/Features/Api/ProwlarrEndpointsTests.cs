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
using System.Text.Json;
using Xunit;
using Listenarr.Tests.Mocks;

namespace Listenarr.Tests.Features.Api
{
    public class ProwlarrEndpointsTests : IClassFixture<ListenarrWebApplicationFactory>
    {
        private readonly ListenarrWebApplicationFactory _factory;

        public ProwlarrEndpointsTests(ListenarrWebApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task SystemStatus_ReturnsJsonWithVersion()
        {
            using var client = _factory.CreateClient();
            var resp = await client.GetAsync("/api/v1/prowlarr/system/status");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

            using var stream = await resp.Content.ReadAsStreamAsync();
            var doc = await JsonDocument.ParseAsync(stream);
            Assert.True(doc.RootElement.TryGetProperty("version", out var versionProp));
            Assert.False(string.IsNullOrEmpty(versionProp.GetString()));
        }

        [Fact]
        public async Task IndexerTest_ReturnsHeaderAndJson()
        {
            using var client = _factory.CreateClient();
            // Prefer GET for indexer test in CI to avoid antiforgery middleware interactions during tests
            var resp = await client.GetAsync("/api/v1/prowlarr/indexer/test");

            // Debug POST to ensure POSTs are routed correctly
            using var debugContent = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            var debug = await client.PostAsync("/api/v1/prowlarr/debug/test", debugContent);
            Assert.True(debug.IsSuccessStatusCode, $"Debug POST failed: {(int)debug.StatusCode} {debug.StatusCode}: {await debug.Content.ReadAsStringAsync()}");

            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                throw new System.Exception($"POST /api/v1/indexer/test returned {(int)resp.StatusCode} {resp.StatusCode}: {body}");
            }

            Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

            // Header present
            Assert.True(resp.Headers.Contains("X-Application-Version"));
            var header = resp.Headers.GetValues("X-Application-Version").FirstOrDefault();
            Assert.False(string.IsNullOrEmpty(header));

            // JSON body contains success and version
            using var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(body ?? ""));
            var doc = await JsonDocument.ParseAsync(stream);
            Assert.True(doc.RootElement.TryGetProperty("success", out var successProp));
            Assert.True(successProp.GetBoolean());
            Assert.True(doc.RootElement.TryGetProperty("version", out var v2));
            Assert.False(string.IsNullOrEmpty(v2.GetString()));
        }

        [Fact]
        public async Task IndexerSchema_ReturnsFieldsArray()
        {
            using var client = _factory.CreateClient();
            var resp = await client.GetAsync("/api/v1/prowlarr/indexer/schema");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

            using var stream = await resp.Content.ReadAsStreamAsync();
            var doc = await JsonDocument.ParseAsync(stream);

            JsonElement schemaObject;

            // Support both object and array shapes: prefer array (one entry per implementation),
            // otherwise fall back to object with 'fields' and 'implementations'.
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                Assert.True(doc.RootElement.GetArrayLength() >= 1);
                schemaObject = doc.RootElement[0];
            }
            else
            {
                schemaObject = doc.RootElement;
            }

            Assert.True(schemaObject.TryGetProperty("fields", out var fieldsProp));
            Assert.True(fieldsProp.ValueKind == JsonValueKind.Array);
            Assert.True(fieldsProp.GetArrayLength() >= 1);

            // Ensure schema contains required fields for Prowlarr compatibility
            var fieldNames = fieldsProp.EnumerateArray().Select(f => f.GetProperty("name").GetString() ?? string.Empty).ToList();
            Assert.Contains("baseUrl", fieldNames);
            Assert.Contains("apiPath", fieldNames);
            Assert.Contains("apiKey", fieldNames);
            Assert.Contains("categories", fieldNames);

            // Schema must advertise supported implementations (Prowlarr expects at least Newznab or Torznab)
            bool hasImpl = false;

            // If root returned 'implementations' array, use that
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("implementations", out var implProp)
                && implProp.ValueKind == JsonValueKind.Array)
            {
                hasImpl = implProp.EnumerateArray().Any(e => (e.GetString() ?? string.Empty) == "Newznab" || (e.GetString() ?? string.Empty) == "Torznab");
            }

            // Otherwise, if an array of schema entries was returned, check entries for implementation names
            if (!hasImpl && doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                hasImpl = doc.RootElement.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("implementation", out var impl) && (impl.GetString() == "Newznab" || impl.GetString() == "Torznab"));
            }

            Assert.True(hasImpl, "Schema implementations must include Newznab or Torznab");
        }

        [Fact]
        public async Task IndexerRoot_ReturnsJsonWithImplementations()
        {
            using var client = _factory.CreateClient();
            var resp = await client.GetAsync("/api/v1/prowlarr/indexer/info");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

            using var stream = await resp.Content.ReadAsStreamAsync();
            var doc = await JsonDocument.ParseAsync(stream);
            Assert.True(doc.RootElement.TryGetProperty("implementations", out var implProp));
            Assert.True(implProp.ValueKind == JsonValueKind.Array);
            bool hasImpl = implProp.EnumerateArray().Any(e => (e.GetString() ?? string.Empty) == "Newznab" || (e.GetString() ?? string.Empty) == "Torznab");
            Assert.True(hasImpl, "Indexers root must include Newznab or Torznab implementations");
        }

        [Fact]
        public async Task IndexersList_Get_ReturnsArray()
        {
            using var client = _factory.CreateClient();
            var resp = await client.GetAsync("/api/v1/prowlarr/indexers");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

            using var stream = await resp.Content.ReadAsStreamAsync();
            var doc = await JsonDocument.ParseAsync(stream);
            Assert.True(doc.RootElement.ValueKind == JsonValueKind.Array);
        }

        [Fact]
        public async Task IndexersList_Post_AcceptsArray()
        {
            using var client = _factory.CreateClient();
            var payload = "[]";
            using var arrayContent = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var resp = await client.PostAsync("/api/v1/prowlarr/indexers", arrayContent);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var stream = await resp.Content.ReadAsStreamAsync();
            var doc = await JsonDocument.ParseAsync(stream);
            Assert.True(doc.RootElement.TryGetProperty("accepted", out var acc));
            Assert.True(acc.GetBoolean());
        }

        [Fact]
        public async Task IndexersList_Post_AcceptsSingleObject()
        {
            using var client = _factory.CreateClient();

            var payload = JsonSerializer.Serialize(new
            {
                name = "Single Object via Indexers",
                implementation = "Newznab",
                baseUrl = "http://localhost:18090",
                apiPath = "api",
                apiKey = "OBJECTKEY",
                categories = new[] { 3030 }
            });

            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var resp = await client.PostAsync(
                "/api/v1/prowlarr/indexers",
                content);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            using var stream = await resp.Content.ReadAsStreamAsync();
            var doc = await JsonDocument.ParseAsync(stream);
            Assert.True(doc.RootElement.TryGetProperty("accepted", out var accepted));
            Assert.True(accepted.GetBoolean());
            Assert.True(doc.RootElement.TryGetProperty("created", out var created));
            Assert.True(created.GetInt32() >= 1);
        }

        [Fact]
        public async Task Indexers_Post_PersistsToDatabaseAndVisibleViaApi()
        {
            using var client = _factory.CreateClient();

            var newIndexer = new
            {
                name = "Prowlarr Test Indexer",
                implementation = "Newznab",
                baseUrl = "http://localhost:8080",
                apiPath = "api",
                apiKey = "TESTKEY",
                categories = new[] { 1000 }
            };

            var arr = "[" + System.Text.Json.JsonSerializer.Serialize(newIndexer) + "]";
            using var batchContent = new StringContent(arr, System.Text.Encoding.UTF8, "application/json");
            var resp = await client.PostAsync("/api/v1/prowlarr/indexers", batchContent);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            // Now fetch persisted indexers via the Prowlarr-compatible endpoint
            var resp2 = await client.GetAsync("/api/v1/prowlarr/indexer");
            Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);

            using var stream = await resp2.Content.ReadAsStreamAsync();
            var doc = await JsonDocument.ParseAsync(stream);
            Assert.True(doc.RootElement.ValueKind == JsonValueKind.Array);
            // Ensure at least one indexer has the name we posted
            bool found = doc.RootElement.EnumerateArray().Any(elem => elem.TryGetProperty("name", out var p) && (p.GetString() ?? string.Empty) == "Prowlarr Test Indexer");
            Assert.True(found, "Posted indexer should be persisted and visible via /api/indexers");
        }

        [Fact]
        public async Task Indexer_Post_Single_PersistsToDatabaseAndVisibleViaApi()
        {
            using var client = _factory.CreateClient();

            var newIndexer = new
            {
                name = "Prowlarr Single Test Indexer",
                implementation = "Newznab",
                baseUrl = "http://localhost:8081",
                apiPath = "api",
                apiKey = "SINGLEKEY",
                categories = new[] { 2000 }
            };

            var payload = System.Text.Json.JsonSerializer.Serialize(newIndexer);
            using var singleContent = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var resp = await client.PostAsync("/api/v1/prowlarr/indexer", singleContent);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            // Validate response contains created indexer
            var respBody = await resp.Content.ReadAsStringAsync();
            using var respDocStream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(respBody ?? ""));
            var respDoc = await JsonDocument.ParseAsync(respDocStream);
            Assert.True(respDoc.RootElement.TryGetProperty("indexers", out var idxProp));
            Assert.True(idxProp.ValueKind == JsonValueKind.Array);
            bool foundInResp = idxProp.EnumerateArray().Any(elem => elem.TryGetProperty("name", out var p) && (p.GetString() ?? string.Empty) == "Prowlarr Single Test Indexer");

            System.Text.Json.JsonElement createdElem;
            int id;
            if (foundInResp)
            {
                createdElem = idxProp.EnumerateArray().First();
                id = createdElem.GetProperty("id").GetInt32();
            }
            else
            {
                // If the response didn't include the created indexer (dedupe / existing indexer case), search persisted indexers by URL
                var listResp = await client.GetAsync("/api/v1/prowlarr/indexer");
                Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
                using var listStream = await listResp.Content.ReadAsStreamAsync();
                var listDoc = await JsonDocument.ParseAsync(listStream);
                var expectedUrl = "http://localhost:8081/api";
                var match = listDoc.RootElement.EnumerateArray().FirstOrDefault(elem => elem.TryGetProperty("baseUrl", out var p) && (p.GetString() ?? string.Empty) == expectedUrl);
                Assert.True(match.ValueKind != JsonValueKind.Undefined, "Posted single indexer should be present in persisted indexers (by URL)");
                createdElem = match;
                id = createdElem.GetProperty("id").GetInt32();
            }

            var getResp = await client.GetAsync($"/api/v1/prowlarr/indexer/{id}");
            Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);

            using var getStream = await getResp.Content.ReadAsStreamAsync();
            var getDoc = await JsonDocument.ParseAsync(getStream);
            Assert.True(getDoc.RootElement.TryGetProperty("id", out var getIdProp));
            Assert.Equal(id, getIdProp.GetInt32());
            Assert.True(getDoc.RootElement.TryGetProperty("settings", out var settingsProp));
            Assert.True(settingsProp.TryGetProperty("baseUrl", out var sb));
            Assert.Equal("http://localhost:8081/api", sb.GetString());

            // Ensure requesting id 0 returns a compatibility object rather than 404 HTML
            var respZero = await client.GetAsync("/api/v1/prowlarr/indexer/0");
            Assert.Equal(HttpStatusCode.OK, respZero.StatusCode);
            var zeroBody = await respZero.Content.ReadAsStringAsync();
            Assert.Contains("Prowlarr Indexer", zeroBody);


            // Now fetch persisted indexers via the Prowlarr-compatible endpoint
            var resp2 = await client.GetAsync("/api/v1/prowlarr/indexer");
            Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);

            using var stream = await resp2.Content.ReadAsStreamAsync();
            var doc = await JsonDocument.ParseAsync(stream);
            Assert.True(doc.RootElement.ValueKind == JsonValueKind.Array);
            // Ensure at least one indexer has the name we posted
            bool found = doc.RootElement.EnumerateArray().Any(elem => elem.TryGetProperty("name", out var p) && (p.GetString() ?? string.Empty) == "Prowlarr Single Test Indexer");
            Assert.True(found, "Posted single indexer should be persisted and visible via /api/indexers");
        }

        [Fact]
        public async Task Delete_Indexer_WithZeroId_IsNoOp_ReturnsOk()
        {
            using var client = _factory.CreateClient();

            var resp = await client.DeleteAsync("/api/v1/prowlarr/indexer/0");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            var body = await resp.Content.ReadAsStringAsync();
            using var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(body ?? ""));
            var doc = await JsonDocument.ParseAsync(stream);
            Assert.True(doc.RootElement.ValueKind == JsonValueKind.Object);
            Assert.Empty(doc.RootElement.EnumerateObject()); // empty object
        }
    }
}
