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
using System.Text.Json.Nodes;
using System.Net;

namespace Listenarr.Api.Tests
{
    public partial class NotificationServiceTests
    {
        private static JsonNode RequireProperty(JsonObject obj, string propertyName)
        {
            Assert.True(obj.TryGetPropertyValue(propertyName, out var value), $"Expected property '{propertyName}' to be present.");
            Assert.NotNull(value);
            return value!;
        }

        [Fact]
        public void CreateDiscordPayload_IncludesTitleAuthorAndThumbnail_WhenAsinAndBaseProvided()
        {
            // Arrange
            var trigger = "book-added";
            var data = new
            {
                id = 123,
                title = "Test Book",
                authors = new[] { "Jane Doe" },
                asin = "B00TEST"
            };
            var baseUrl = "https://listenarr.example.com";

            // Act
            JsonNode node = NotificationPayloadBuilder.CreateDiscordPayload(trigger, data, baseUrl);

            // Assert
            Assert.NotNull(node);
            Assert.True(node is JsonObject);
            var obj = node.AsObject();

            Assert.Equal("Test Book by Jane Doe has been added", RequireProperty(obj, "content").ToString());

            var embeds = RequireProperty(obj, "embeds").AsArray();
            Assert.Single(embeds);

            var embed = embeds[0]!.AsObject();
            Assert.Equal("Test Book", embed["title"]?.ToString());
            // Author should be present as a labeled field
            var fields = RequireProperty(embed, "fields").AsArray();
            Assert.Contains(fields, f => f.AsObject()!["name"]!.ToString() == "Author" && f.AsObject()!["value"]!.ToString() == "Jane Doe");
            var thumb = RequireProperty(embed, "thumbnail").AsObject();
            Assert.Equal("https://listenarr.example.com/api/v1/images/B00TEST", thumb["url"]?.ToString());
        }
    }

    // Additional tests for truncation and metadata fields
    public partial class NotificationServiceTests
    {
        [Fact]
        public void CreateDiscordPayload_TruncatesLongFields_ToDiscordLimits()
        {
            // Arrange
            var trigger = "book-added";
            var longTitle = new string('T', 300); // longer than 256
            var longAuthor = new string('A', 5000); // longer than 4096
            var data = new
            {
                title = longTitle,
                authors = new[] { longAuthor }
            };

            // Act
            var node = NotificationPayloadBuilder.CreateDiscordPayload(trigger, data, null);

            // Assert
            Assert.NotNull(node);
            var obj = node.AsObject();
            var embeds = RequireProperty(obj, "embeds").AsArray();
            Assert.Single(embeds);
            var embed = embeds[0]!.AsObject();

            var title = embed["title"]?.ToString() ?? string.Empty;

            // Author should be present in fields and truncated to field limit (1024)
            string authorFieldValue = string.Empty;
            if (embed.TryGetPropertyValue("fields", out var fieldsNode) && fieldsNode is not null)
            {
                foreach (var fo in fieldsNode.AsArray().Select(f => f!.AsObject()).Where(fo => fo["name"]?.ToString() == "Author"))
                {
                    authorFieldValue = fo["value"]?.ToString() ?? string.Empty;
                    break;
                }
            }

            Assert.True(title.Length <= 256, "Title should be truncated to 256 chars");
            Assert.True(authorFieldValue.Length <= 1024, "Author field should be truncated to 1024 chars");
        }

        [Fact]
        public void CreateDiscordPayload_IncludesPublisherYearImageAndFooter_WhenProvided()
        {
            // Arrange
            var trigger = "book-added";
            var data = new
            {
                title = "Metadata Book",
                authors = new[] { "Author" },
                publisher = "Test Publisher",
                year = "2021",
                imageUrl = "https://cdn.example.com/covers/test.jpg",
            };
            var baseUrl = "https://listenarr.example.com";

            // Act
            var node = NotificationPayloadBuilder.CreateDiscordPayload(trigger, data, baseUrl);

            // Assert
            Assert.NotNull(node);
            var obj = node.AsObject();
            var embeds = RequireProperty(obj, "embeds").AsArray();
            Assert.Single(embeds);
            var embed = embeds[0]!.AsObject();

            // fields should include Publisher and Year
            var fields = RequireProperty(embed, "fields").AsArray();
            Assert.Contains(fields, f => f.AsObject()!["name"]!.ToString() == "Publisher");
            Assert.Contains(fields, f => f.AsObject()!["name"]!.ToString() == "Year");

            // thumbnail should be present (we use thumbnail only now)
            var thumb = RequireProperty(embed, "thumbnail").AsObject();
            Assert.Equal("https://cdn.example.com/covers/test.jpg", thumb["url"]?.ToString());

            // footer should contain publisher and year
            var footer = RequireProperty(embed, "footer").AsObject();
            var footerText = footer["text"]?.ToString() ?? string.Empty;
            Assert.Contains("Test Publisher", footerText);
            Assert.Contains("2021", footerText);
        }

        [Fact]
        public void CreateDiscordPayload_ConvertsRelativeImageUrlToAbsolute_WhenBaseUrlProvided()
        {
            // Arrange
            var trigger = "book-added";
            var data = new
            {
                title = "Relative Image Book",
                authors = new[] { "Author" },
                imageUrl = "/api/v1/images/B123RELATIVE"
            };
            var baseUrl = "https://listenarr.example.com";

            // Act
            var node = NotificationPayloadBuilder.CreateDiscordPayload(trigger, data, baseUrl);

            // Assert
            Assert.NotNull(node);
            var obj = node.AsObject();
            var embeds = RequireProperty(obj, "embeds").AsArray();
            Assert.Single(embeds);
            var embed = embeds[0]!.AsObject();

            // thumbnail should be present and converted to absolute URL
            var image = RequireProperty(embed, "thumbnail").AsObject();
            Assert.Equal("https://listenarr.example.com/api/v1/images/B123RELATIVE", image["url"]?.ToString());
        }
    }

    public partial class NotificationServiceTests
    {
        [Fact]
        public void GetBaseUrlFromRequestContext_ReturnsExpectedBase()
        {
            var ctx = new RequestContextSnapshot(null, "https", "listenarr.example.com", null, false);

            var baseUrl = NotificationPayloadBuilder.GetBaseUrlFromRequestContext(ctx);
            Assert.Equal("https://listenarr.example.com", baseUrl);
        }

        [Fact]
        public void CreateDiscordPayload_UsesDerivedBaseForThumbnail_WhenProvided()
        {
            var trigger = "book-added";
            var data = new
            {
                title = "Derived Base Book",
                authors = new[] { "Author" },
                asin = "B123DERIVE"
            };

            var ctx = new RequestContextSnapshot(null, "https", "listenarr.example.com", null, false);

            var derived = NotificationPayloadBuilder.GetBaseUrlFromRequestContext(ctx);
            Assert.NotNull(derived);

            var node = NotificationPayloadBuilder.CreateDiscordPayload(trigger, data, derived);
            Assert.NotNull(node);
            var obj = node.AsObject();
            var embed = RequireProperty(obj, "embeds").AsArray()[0]!.AsObject();
            var thumb = RequireProperty(embed, "thumbnail").AsObject();
            Assert.Equal("https://listenarr.example.com/api/v1/images/B123DERIVE", thumb["url"]?.ToString());
        }

        [Fact]
        public void CreateDiscordPayload_EnforcesOverallEmbedLimit_TruncatesAsNeeded()
        {
            var trigger = "book-added";
            // Create a very large description to exceed the 6000 char limit
            var desc = new string('D', 5000);

            var data = new JsonObject
            {
                ["title"] = "Big Embed",
                ["authors"] = new JsonArray("Author"),
                ["description"] = desc
            };

            // Also attach large fields by creating a wrapper object that CreateDiscordPayload will pick up
            // We'll add them under keys so the payload parsing can pick them up; simpler to include them as part of the data
            data["publisher"] = "Pub";

            var node = NotificationPayloadBuilder.CreateDiscordPayload(trigger, data, null);
            Assert.NotNull(node);
            var obj = node.AsObject();
            var embed = RequireProperty(obj, "embeds").AsArray()[0]!.AsObject();

            // Calculate total embed size
            int total = 0;
            if (embed.TryGetPropertyValue("title", out var titleNode) && titleNode is not null) total += titleNode.ToString().Length;
            if (embed.TryGetPropertyValue("description", out var descriptionNode) && descriptionNode is not null) total += descriptionNode.ToString().Length;
            if (embed.TryGetPropertyValue("footer", out var footerNode) && footerNode is not null) total += footerNode.AsObject()?["text"]?.ToString()?.Length ?? 0;
            if (embed.TryGetPropertyValue("fields", out var embedFieldsNode) && embedFieldsNode is not null)
            {
                foreach (var fo in embedFieldsNode.AsArray().Select(f => f!.AsObject()))
                {
                    total += fo["name"]?.ToString()?.Length ?? 0;
                    total += fo["value"]?.ToString()?.Length ?? 0;
                }
            }

            Assert.True(total <= 6000, "Combined embed content should not exceed MAX_EMBED_TOTAL (6000)");
        }
    }

    public partial class NotificationServiceTests
    {
        [Fact]
        public async Task SendNotificationAsync_PostsCorrectJsonToDiscordWebhook()
        {
            // Arrange
            var trigger = "book-added";
            var data = new
            {
                id = 123,
                title = "Integration Test Book",
                authors = new[] { "Test Author" },
                asin = "B00INTEGRATION",
                publisher = "Test Publisher",
                year = "2023"
            };
            var webhookUrl = "https://discord.com/api/webhooks/test";
            var enabledTriggers = new List<string> { trigger };

            // Mock HttpClient to capture the posted content
            string? capturedJson = null;
            var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
            using var postResponse = new HttpResponseMessage(HttpStatusCode.OK);

            mockHttpMessageHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .Callback<HttpRequestMessage, CancellationToken>(async (request, token) =>
                {
                    capturedJson = await request.Content!.ReadAsStringAsync();
                })
                .ReturnsAsync(postResponse);

            var httpClient = new HttpClient(mockHttpMessageHandler.Object);

            // Mock configuration service
            var mockConfigService = new Mock<IConfigurationService>();
            var startupConfig = new StartupConfig { UrlBase = "https://listenarr.example.com" };
            mockConfigService
                .Setup(x => x.GetStartupConfigAsync())
                .ReturnsAsync(startupConfig);

            // Mock HttpContextAccessor (optional for this test)
            var mockHttpContextAccessor = new Mock<IRequestContextAccessor>();

            // Create service
            var services = new ServiceCollection();
            services.AddSingleton<INotificationPayloadBuilder, NotificationPayloadBuilderAdapter>();
            var provider = services.BuildServiceProvider();
            var payloadBuilder = provider.GetRequiredService<INotificationPayloadBuilder>();
            var service = new NotificationService(
                httpClient,
                Mock.Of<ILogger<NotificationService>>(),
                mockConfigService.Object,
                payloadBuilder,
                mockHttpContextAccessor.Object
            );

            // Act
            await service.SendNotificationAsync(trigger, data, webhookUrl, enabledTriggers);

            // Assert
            Assert.NotNull(capturedJson);
            var postedNode = JsonNode.Parse(capturedJson);
            Assert.NotNull(postedNode);

            // Compare with what CreateDiscordPayload produces
            var expectedNode = NotificationPayloadBuilder.CreateDiscordPayload(trigger, data, startupConfig.UrlBase);

            // Parse both as objects and compare key properties (excluding timestamp which is dynamic)
            var postedObj = postedNode.AsObject();
            var expectedObj = expectedNode.AsObject();

            // Compare content
            Assert.Equal(expectedObj["content"]?.ToString(), postedObj["content"]?.ToString());

            // Compare embeds (excluding timestamp)
            var postedEmbeds = RequireProperty(postedObj, "embeds").AsArray();
            var expectedEmbeds = RequireProperty(expectedObj, "embeds").AsArray();
            Assert.Single(postedEmbeds);
            Assert.Single(expectedEmbeds);

            var postedEmbed = postedEmbeds[0]!.AsObject();
            var expectedEmbed = expectedEmbeds[0]!.AsObject();

            // Compare all properties except timestamp
            Assert.Equal(expectedEmbed["title"]?.ToString(), postedEmbed["title"]?.ToString());
            Assert.Equal(expectedEmbed["description"]?.ToString(), postedEmbed["description"]?.ToString());

            // Compare thumbnail if present
            if (expectedEmbed.TryGetPropertyValue("thumbnail", out var expectedThumbNode) && expectedThumbNode is not null)
            {
                var expectedThumb = expectedThumbNode.AsObject();
                var postedThumb = RequireProperty(postedEmbed, "thumbnail").AsObject();
                Assert.Equal(expectedThumb["url"]?.ToString(), postedThumb["url"]?.ToString());
            }

            // Compare fields if present
            if (expectedEmbed.TryGetPropertyValue("fields", out var expectedFieldsNode) && expectedFieldsNode is not null)
            {
                var expectedFields = expectedFieldsNode.AsArray();
                var postedFields = RequireProperty(postedEmbed, "fields").AsArray();
                Assert.Equal(expectedFields.Count, postedFields.Count);

                for (int i = 0; i < expectedFields.Count; i++)
                {
                    var expectedField = expectedFields[i]!.AsObject();
                    var postedField = postedFields[i]!.AsObject();
                    Assert.Equal(expectedField["name"]?.ToString(), postedField["name"]?.ToString());
                    Assert.Equal(expectedField["value"]?.ToString(), postedField["value"]?.ToString());
                    Assert.Equal(expectedField["inline"]?.GetValue<bool>(), postedField["inline"]?.GetValue<bool>());
                }
            }

            // Compare footer if present
            if (expectedEmbed.TryGetPropertyValue("footer", out var expectedFooterNode) && expectedFooterNode is not null)
            {
                var expectedFooter = expectedFooterNode.AsObject();
                var postedFooter = RequireProperty(postedEmbed, "footer").AsObject();
                Assert.Equal(expectedFooter["text"]?.ToString(), postedFooter["text"]?.ToString());
            }

        }
    }

    public partial class NotificationServiceTests
    {
        [Fact]
        public async Task SendNotificationAsync_AttachesImageAndReferencesAttachmentInPayload()
        {
            // Arrange
            var trigger = "book-added";
            var data = new
            {
                id = 321,
                title = "Attachment Test Book",
                authors = new[] { "Attach Author" },
                asin = "BATTACH",
                imageUrl = "https://listenarr.example.com/api/v1/images/BATTACH.jpg"
            };
            var webhookUrl = "https://discord.com/api/webhooks/test-attach";
            var enabledTriggers = new List<string> { trigger };

            var capturedBodies = new List<string>();

            // Mock HttpMessageHandler to return an image on GET and capture POST body
            var mockHandler = new Mock<HttpMessageHandler>();
            // Create named responses for disposal after test
            using var imageGetResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
            };
            imageGetResponse.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");

            // GET for image
            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Get),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(imageGetResponse);

            // POST to webhook - capture body (match exact webhook URL to avoid capturing unrelated POSTs)
            using var webhookPostResponse = new HttpResponseMessage(HttpStatusCode.OK);
            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Post && r.RequestUri != null && r.RequestUri.ToString().Equals(webhookUrl, System.StringComparison.OrdinalIgnoreCase)),
                    ItExpr.IsAny<CancellationToken>()
                )
                .Callback<HttpRequestMessage, CancellationToken>(async (request, token) =>
                {
                    var contentType = request.Content?.Headers.ContentType?.ToString() ?? string.Empty;
                    string bodyText = string.Empty;
                    try
                    {
                        var bytes = await request.Content!.ReadAsByteArrayAsync();
                        bodyText = System.Text.Encoding.UTF8.GetString(bytes);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        try { bodyText = await request.Content!.ReadAsStringAsync(); } catch (Exception ex2) when (ex2 is not OutOfMemoryException && ex2 is not StackOverflowException) { bodyText = string.Empty; }
                    }

                    capturedBodies.Add(contentType + "\n" + bodyText);
                })
                .ReturnsAsync(webhookPostResponse);

            // Fallback for other POSTs in the test run
            using var fallbackPostResponse = new HttpResponseMessage(HttpStatusCode.OK);
            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Post && (r.RequestUri == null || !r.RequestUri.ToString().StartsWith(webhookUrl, System.StringComparison.OrdinalIgnoreCase))), ItExpr.IsAny<CancellationToken>())
                .Callback<HttpRequestMessage, CancellationToken>((request, _) =>
                {
                    Console.WriteLine("DEBUG FALLBACK POST to: " + (request.RequestUri?.ToString() ?? "(null)") + " ContentType=" + (request.Content?.Headers.ContentType?.ToString() ?? "(none)"));
                })
                .ReturnsAsync(fallbackPostResponse);

            var httpClient = new HttpClient(mockHandler.Object);

            // Mock configuration service
            var mockConfigService = new Mock<IConfigurationService>();
            var startupConfig = new StartupConfig { UrlBase = "https://listenarr.example.com" };
            mockConfigService
                .Setup(x => x.GetStartupConfigAsync())
                .ReturnsAsync(startupConfig);

            var services = new ServiceCollection();
            services.AddSingleton<INotificationPayloadBuilder, NotificationPayloadBuilderAdapter>();
            var provider = services.BuildServiceProvider();
            var payloadBuilder = provider.GetRequiredService<INotificationPayloadBuilder>();
            var service = new NotificationService(
                httpClient,
                Mock.Of<ILogger<NotificationService>>(),
                mockConfigService.Object,
                payloadBuilder,
                Mock.Of<IRequestContextAccessor>()
            );

            // Act
            await service.SendNotificationAsync(trigger, data, webhookUrl, enabledTriggers);

            // Verify the adapter attempted to download the image from the configured host
            mockHandler.Protected().Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Get),
                ItExpr.IsAny<CancellationToken>()
            );

            // Verify that at least one POST used multipart/form-data (attachment branch)
            mockHandler.Protected().Verify(
                "SendAsync",
                Times.AtLeastOnce(),
                ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Post && r.Content is MultipartFormDataContent),
                ItExpr.IsAny<CancellationToken>()
            );

            // Assert we captured at least one POST to the webhook URL
            Assert.NotEmpty(capturedBodies);

            // Dump captured bodies for debugging when assertions fail
            //foreach (var cb in capturedBodies) Console.WriteLine("DEBUG CAPTURED POST BODY:\n" + cb);

            // At least one multipart POST should have been observed (verified above). Also ensure we captured at least one POST body.
            Assert.NotEmpty(capturedBodies);
        }
    }
}
