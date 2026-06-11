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
using Listenarr.Api.Controllers;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Reflection;
using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Infrastructure.Persistence;

namespace Listenarr.Tests.Features.Api.Controllers
{
    public class ProwlarrCompatControllerTests
    {
        private static ListenArrDbContext CreateInMemoryDb()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new ListenArrDbContext(options);
        }

        private static IApplicationVersionService CreateApplicationVersionService()
        {
            return Mock.Of<IApplicationVersionService>(service => service.Resolve() == "0.4.2");
        }

        private static IRealtimeClientRegistry CreateRealtimeClientRegistry()
        {
            return Mock.Of<IRealtimeClientRegistry>(registry => registry.GetSettingsClientIds() == Array.Empty<string>());
        }

        [Fact]
        public async Task PostIndexers_BroadcastsRealtimeUpdate_WhenNewIndexersCreated()
        {
            var db = CreateInMemoryDb();
            var mockHubBroadcaster = new Mock<IHubBroadcaster>();
            var mockLogger = new Mock<ILogger<ProwlarrCompatController>>();
            var mockToastService = new Mock<IToastService>();
            var mockStartupConfigService = new Mock<IStartupConfigService>();
            mockStartupConfigService.Setup(s => s.GetConfig()).Returns(new StartupConfig { AuthenticationRequired = "false" });
            var controller = new ProwlarrCompatController(mockLogger.Object, new EfIndexerRepository(db), mockHubBroadcaster.Object, CreateRealtimeClientRegistry(), mockToastService.Object, mockStartupConfigService.Object, CreateApplicationVersionService());

            var newIndexer = new { name = "Unit Test Indexer", implementation = "Newznab", baseUrl = "http://localhost", apiPath = "api", apiKey = "KEY" };
            var arr = JsonSerializer.Serialize(new[] { newIndexer });

            // Clear static toast maps to avoid test interdependence
            var fld = typeof(ProwlarrCompatController).GetField("_lastToastTimes", BindingFlags.NonPublic | BindingFlags.Static);
            var dict = (System.Collections.Concurrent.ConcurrentDictionary<int, DateTime>)fld.GetValue(null);
            dict.Clear();
            var msgFld = typeof(ProwlarrCompatController).GetField("_lastToastMessages", BindingFlags.NonPublic | BindingFlags.Static);
            var msgDict = (System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>)msgFld.GetValue(null);
            msgDict.Clear();

            var payload = JsonDocument.Parse(arr).RootElement;
            _ = await controller.PostIndexers(payload);

            mockHubBroadcaster.Verify(
                b => b.BroadcastAsync(RealtimeHubTarget.Settings, "IndexersUpdated", It.IsAny<object>(), It.IsAny<CancellationToken>()),
                Times.Once);

            // Verify a 'Created indexer' log entry exists
            Assert.True(mockLogger.Invocations.Any(inv => inv.ToString().Contains("Created indexer")), "Expected a log entry containing 'Created indexer'");

            // Verify a 'Indexers processed' log entry exists
            Assert.True(mockLogger.Invocations.Any(inv => inv.ToString().Contains("Indexers processed")), "Expected a log entry containing 'Indexers processed'");
            // Verify that raw payload was logged (redacted/truncated text should include the indexer name)
            Assert.True(mockLogger.Invocations.Any(inv => inv.ToString().Contains("Unit Test Indexer")), "Expected a log entry containing the indexer name");

            // Verify that a notification was published (activity dropdown) and a toast was shown once
            mockToastService.Verify(
                s => s.PublishNotificationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>()),
                Times.Once);
        }

        [Fact]
        public async Task PostIndexer_ReturnsCreatedIndex_WhenSingleIndexerPosted()
        {
            var db = CreateInMemoryDb();
            var mockHubBroadcaster = new Mock<IHubBroadcaster>();
            var mockLogger = new Mock<ILogger<ProwlarrCompatController>>();
            var mockToastService = new Mock<IToastService>();
            var mockStartupConfigService = new Mock<IStartupConfigService>();
            mockStartupConfigService.Setup(s => s.GetConfig()).Returns(new StartupConfig { AuthenticationRequired = "false" });
            var controller = new ProwlarrCompatController(mockLogger.Object, new EfIndexerRepository(db), mockHubBroadcaster.Object, CreateRealtimeClientRegistry(), mockToastService.Object, mockStartupConfigService.Object, CreateApplicationVersionService());

            var newIndexer = new { name = "Unit Test Indexer", implementation = "Newznab", baseUrl = "http://localhost", apiPath = "api", apiKey = "KEY" };
            // Clear static toast maps to avoid test interdependence
            var fld = typeof(ProwlarrCompatController).GetField("_lastToastTimes", BindingFlags.NonPublic | BindingFlags.Static);
            var dict = (System.Collections.Concurrent.ConcurrentDictionary<int, DateTime>)fld.GetValue(null);
            dict.Clear();
            var msgFld = typeof(ProwlarrCompatController).GetField("_lastToastMessages", BindingFlags.NonPublic | BindingFlags.Static);
            var msgDict = (System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>)msgFld.GetValue(null);
            msgDict.Clear();

            var payload = JsonDocument.Parse(JsonSerializer.Serialize(newIndexer)).RootElement;

            var result = await controller.PostIndexer(payload);

            int idVal;
            object valueObj = null;
            if (result is Microsoft.AspNetCore.Mvc.CreatedAtActionResult created)
            {
                Assert.Equal(nameof(ProwlarrCompatController.GetIndexerById), created.ActionName);
                valueObj = created.Value;
                var idProp = valueObj.GetType().GetProperty("id");
                Assert.NotNull(idProp);
                idVal = (int)idProp.GetValue(valueObj);
                Assert.True(idVal > 0, "Expected created indexer to have a positive id");
            }
            else
            {
                // Some flows return OK with created/indexers array (accept both shapes)
                var postOk = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
                var createdProp = postOk.Value.GetType().GetProperty("created");
                Assert.NotNull(createdProp);
                Assert.Equal(1, (int)createdProp.GetValue(postOk.Value));
                var idxProp = postOk.Value.GetType().GetProperty("indexers");
                Assert.NotNull(idxProp);
                var arr = Assert.IsAssignableFrom<System.Array>(idxProp.GetValue(postOk.Value));
                Assert.NotNull(arr);
                var first = arr.GetValue(0);
                var idProp = first.GetType().GetProperty("id");
                Assert.NotNull(idProp);
                idVal = (int)idProp.GetValue(first);
                Assert.True(idVal > 0, "Expected created indexer to have a positive id");
                valueObj = first;
            }
            // Handle possible null Created.Value (some flows return Created with null body) by fetching the created indexer from the DB
            if (valueObj == null)
            {
                // Try finding by normalized URL, then by name, then fallback to first indexer if present so tests are resilient to small differences in normalization.
                var idx = db.Indexers.FirstOrDefault(i => NormalizeIndexerUrl(i.Url) == NormalizeIndexerUrl("http://localhost/api"));
                if (idx == null)
                {
                    idx = db.Indexers.FirstOrDefault(i => i.Name == "Unit Test Indexer");
                }
                if (idx == null)
                {
                    idx = db.Indexers.FirstOrDefault();
                }
                Assert.NotNull(idx);
                idVal = idx.Id;
                var getRes = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(controller.GetIndexerById(idVal));
                valueObj = getRes.Value;
                Assert.NotNull(valueObj);
            }

            // If the created/returned indexer object is present, ensure DTO includes fields and tags (compatibility with Prowlarr)
            if (valueObj != null)
            {
                var fieldsProp = valueObj.GetType().GetProperty("fields");
                Assert.NotNull(fieldsProp);
                var fieldsVal = Assert.IsAssignableFrom<System.Array>(fieldsProp.GetValue(valueObj));
                Assert.NotNull(fieldsVal);
                Assert.True(fieldsVal.Length >= 3, "Expected at least baseUrl/apiKey/apiPath fields");

                var tagsProp = valueObj.GetType().GetProperty("tags");
                if (tagsProp != null)
                {
                    var tagsValObj = tagsProp.GetValue(valueObj);
                    if (tagsValObj != null)
                    {
                        var tagsVal = Assert.IsAssignableFrom<System.Array>(tagsValObj);
                        Assert.Empty(tagsVal);
                    }
                }
            }

            // Now update the created indexer via PUT
            var update = new { name = "Unit Test Indexer Updated", baseUrl = "http://example.local", apiPath = "api" };
            var updatePayload = JsonDocument.Parse(JsonSerializer.Serialize(update)).RootElement;
            var putResult = await controller.PutIndexer(idVal, updatePayload);
            var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(putResult);
            var updated = ok.Value;
            var updatedIdProp = updated.GetType().GetProperty("id");
            Assert.NotNull(updatedIdProp);
            Assert.Equal(idVal, (int)updatedIdProp.GetValue(updated));
            var updatedNameProp = updated.GetType().GetProperty("name");
            Assert.Equal("Unit Test Indexer Updated", updatedNameProp.GetValue(updated));

            // Call PUT again with same payload to ensure idempotent upsert does not create duplicates
            var putResult2 = await controller.PutIndexer(idVal, updatePayload);
            var ok2 = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(putResult2);
            var updated2 = ok2.Value;
            var updatedId2 = (int)updated2.GetType().GetProperty("id").GetValue(updated2);
            Assert.Equal(idVal, updatedId2);

            // Verify only one persisted indexer exists with that normalized url
            var dbIndexed = db.Indexers.ToList();
            Assert.True(dbIndexed.Count(i => NormalizeIndexerUrl(i.Url) == NormalizeIndexerUrl("http://example.local/api")) == 1);

            // Verify a broadcast and notification occurred
            mockHubBroadcaster.Verify(
                b => b.BroadcastAsync(RealtimeHubTarget.Settings, "IndexersUpdated", It.IsAny<object>(), It.IsAny<CancellationToken>()),
                Times.AtLeastOnce);
            mockToastService.Verify(
                s => s.PublishNotificationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>()),
                Times.AtLeastOnce);
        }

        [Fact]
        public async Task PutIndexer_SuppressesUpdateToast_IfIndexerRecentlyCreated()
        {
            var db = CreateInMemoryDb();
            var mockHubBroadcaster = new Mock<IHubBroadcaster>();
            var mockLogger = new Mock<ILogger<ProwlarrCompatController>>();
            var mockToastService = new Mock<IToastService>();
            var mockStartupConfigService = new Mock<IStartupConfigService>();
            mockStartupConfigService.Setup(s => s.GetConfig()).Returns(new StartupConfig { AuthenticationRequired = "false" });
            var controller = new ProwlarrCompatController(mockLogger.Object, new EfIndexerRepository(db), mockHubBroadcaster.Object, CreateRealtimeClientRegistry(), mockToastService.Object, mockStartupConfigService.Object, CreateApplicationVersionService());

            // Create indexer via POST (this publishes one notification)
            var newIndexer = new { name = "Recent Import", implementation = "Newznab", baseUrl = "http://localhost:9090", apiPath = "api", apiKey = "KEY" };
            var arr = JsonSerializer.Serialize(new[] { newIndexer });

            // Clear static toast maps to avoid test interdependence
            var fld = typeof(ProwlarrCompatController).GetField("_lastToastTimes", BindingFlags.NonPublic | BindingFlags.Static);
            var dict = (System.Collections.Concurrent.ConcurrentDictionary<int, DateTime>)fld.GetValue(null);
            dict.Clear();
            var msgFld = typeof(ProwlarrCompatController).GetField("_lastToastMessages", BindingFlags.NonPublic | BindingFlags.Static);
            var msgDict = (System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>)msgFld.GetValue(null);
            msgDict.Clear();

            var payload = JsonDocument.Parse(arr).RootElement;
            _ = await controller.PostIndexers(payload);
            // Ensure created indexer exists in DB
            var created = db.Indexers.FirstOrDefault(i => i.Name == "Recent Import");
            Assert.NotNull(created);

            // Immediately send a PUT update for the same indexer - should NOT produce an additional toast due to suppression
            var updatePayload = JsonDocument.Parse(JsonSerializer.Serialize(new { name = "Recent Import Updated", baseUrl = "http://localhost:9090", apiPath = "api" })).RootElement;
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.PutIndexer(created.Id, updatePayload));

            // Verify notifications: only one PublishNotificationAsync call (from initial POST)
            mockToastService.Verify(
                s => s.PublishNotificationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>()),
                Times.Once);
        }

        [Fact]
        public async Task PutIndexer_DeduplicatesUpdateToasts_OnRapidConsecutivePuts()
        {
            var db = CreateInMemoryDb();
            var mockHubBroadcaster = new Mock<IHubBroadcaster>();
            var mockLogger = new Mock<ILogger<ProwlarrCompatController>>();
            var mockToastService = new Mock<IToastService>();
            var mockStartupConfigService = new Mock<IStartupConfigService>();
            mockStartupConfigService.Setup(s => s.GetConfig()).Returns(new StartupConfig { AuthenticationRequired = "false" });
            var controller = new ProwlarrCompatController(mockLogger.Object, new EfIndexerRepository(db), mockHubBroadcaster.Object, CreateRealtimeClientRegistry(), mockToastService.Object, mockStartupConfigService.Object, CreateApplicationVersionService());

            // Seed an existing indexer (older CreatedAt so created-based suppression doesn't interfere)
            var idx = new Indexer { Name = "Rapid Update", Url = "http://rapid", ApiKey = "K", Categories = "", CreatedAt = DateTime.UtcNow.AddMinutes(-10), UpdatedAt = DateTime.UtcNow.AddMinutes(-10), IsEnabled = true };
            db.Indexers.Add(idx);
            db.SaveChanges();

            // Clear static toast maps to avoid test interdependence
            var fld = typeof(ProwlarrCompatController).GetField("_lastToastTimes", BindingFlags.NonPublic | BindingFlags.Static);
            var dict = (System.Collections.Concurrent.ConcurrentDictionary<int, DateTime>)fld.GetValue(null);
            dict.Clear();
            var msgFld = typeof(ProwlarrCompatController).GetField("_lastToastMessages", BindingFlags.NonPublic | BindingFlags.Static);
            var msgDict = (System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>)msgFld.GetValue(null);
            msgDict.Clear();

            // Perform two rapid PUTs for the same indexer
            var updatePayload1 = JsonDocument.Parse(JsonSerializer.Serialize(new { name = "Rapid Update 1", baseUrl = "http://rapid", apiPath = "" })).RootElement;
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.PutIndexer(idx.Id, updatePayload1));

            var updatePayload2 = JsonDocument.Parse(JsonSerializer.Serialize(new { name = "Rapid Update 2", baseUrl = "http://rapid", apiPath = "" })).RootElement;
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.PutIndexer(idx.Id, updatePayload2));

            // Verify that only one toast was published for the two consecutive PUTs
            mockToastService.Verify(
                s => s.PublishNotificationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>()),
                Times.Once);

            // Now perform two rapid identical POST indexer (batch) imports which produce the same batch toast message; ensure deduplication by message suppresses the second toast
            var newIndexer = new { name = "Batch Import", implementation = "Newznab", baseUrl = "http://localhost:9091", apiPath = "api", apiKey = "KEY" };
            var arr = JsonSerializer.Serialize(new[] { newIndexer });
            var payload = JsonDocument.Parse(arr).RootElement;
            _ = await controller.PostIndexers(payload);
            _ = await controller.PostIndexers(payload);

            // Only one additional toast should have been published for the two identical batch imports (message-level dedupe)
            mockToastService.Verify(
                s => s.PublishNotificationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>()),
                Times.Exactly(2)); // 1 from earlier PUTs and 1 from the first batch post (second is suppressed)
        }

        [Fact]
        public async Task GetIndexers_IncludesFieldsAndTags()
        {
            var db = CreateInMemoryDb();
            // Seed an indexer
            db.Indexers.Add(new Indexer { Name = "Seeded", Url = "http://seed", ApiKey = "K", Categories = "1,2" });
            db.SaveChanges();

            var mockHubBroadcaster = new Mock<IHubBroadcaster>();
            var mockLogger = new Mock<ILogger<ProwlarrCompatController>>();
            var mockToastService = new Mock<IToastService>();
            var mockStartupConfigService = new Mock<IStartupConfigService>();
            mockStartupConfigService.Setup(s => s.GetConfig()).Returns(new StartupConfig { AuthenticationRequired = "false" });
            var controller = new ProwlarrCompatController(mockLogger.Object, new EfIndexerRepository(db), mockHubBroadcaster.Object, CreateRealtimeClientRegistry(), mockToastService.Object, mockStartupConfigService.Object, CreateApplicationVersionService());

            var result = await controller.GetIndexers();
            var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
            var arr = Assert.IsAssignableFrom<System.Array>(ok.Value);
            Assert.True(arr.Length > 0);
            var first = arr.GetValue(0);

            var fieldsProp = first.GetType().GetProperty("fields");
            Assert.NotNull(fieldsProp);
            var fieldsVal = fieldsProp.GetValue(first) as System.Array;
            Assert.NotNull(fieldsVal);

            var tagsProp = first.GetType().GetProperty("tags");
            Assert.NotNull(tagsProp);
            var tagsVal = tagsProp.GetValue(first) as System.Array;
            Assert.NotNull(tagsVal);
            Assert.Empty(tagsVal);
        }

        private static string NormalizeIndexerUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return string.Empty;

            try
            {
                var uri = new Uri(url);
                var path = uri.AbsolutePath;
                // Trim trailing slash
                path = path.TrimEnd('/');
                // Remove trailing /api if present
                if (path.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
                {
                    path = path.Substring(0, path.Length - 4);
                }

                var port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port;
                var normalized = $"{uri.Scheme}://{uri.Host}{port}{path}";
                return normalized.TrimEnd('/');
            }
            catch (UriFormatException)
            {
                return url.TrimEnd('/');
            }
        }
    }
}
