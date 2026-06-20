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

namespace Listenarr.Tests.Features.Application.Notifications.Payloads
{
    public class NotificationPayloadBuilderAdapterTests
    {
        [Fact]
        public void CreateDiscordPayload_ReturnsExpectedContent()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddSingleton<INotificationPayloadBuilder, NotificationPayloadBuilderAdapter>();
            var provider = services.BuildServiceProvider();
            var adapter = provider.GetRequiredService<INotificationPayloadBuilder>();
            var data = new
            {
                title = "Adapter Title",
                authors = new[] { "Adapter Author" },
                asin = "BADAPTER"
            };
            var baseUrl = "https://listenarr.example.com";

            // Act
            var node = adapter.CreateDiscordPayload("book-added", data, baseUrl);

            // Assert
            Assert.NotNull(node);
            var obj = node.AsObject();
            Assert.Equal("Adapter Title by Adapter Author has been added", obj["content"]?.ToString());
        }

        [Fact]
        public async Task CreateDiscordPayloadWithAttachmentAsync_DownloadsImageAndReturnsAttachment()
        {
            // Arrange
            var expectedBytes = new byte[] { 1, 2, 3, 4 };
            using var mockResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(expectedBytes)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg") }
                }
            };
            var handler = new Mock<HttpMessageHandler>();
            handler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(r => r.Method == HttpMethod.Get),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync(mockResponse);

            using var httpClient = new HttpClient(handler.Object);
            var services = new ServiceCollection();
            services.AddSingleton<INotificationPayloadBuilder, NotificationPayloadBuilderAdapter>();
            var provider = services.BuildServiceProvider();
            var adapter = provider.GetRequiredService<INotificationPayloadBuilder>();

            var data = new
            {
                title = "Attachment Title",
                authors = new[] { "Attachment Author" },
                asin = "BATTACH",
                imageUrl = "https://cdn.example.com/covers/BATTACH.jpg"
            };

            // Act
            var (payload, attachment) = await adapter.CreateDiscordPayloadWithAttachmentAsync("book-added", data, "https://listenarr.example.com", httpClient, Mock.Of<IRequestContextAccessor>());

            // Assert
            Assert.NotNull(payload);
            Assert.NotNull(attachment);
            Assert.Equal(expectedBytes.Length, attachment.ImageData.Length);
            Assert.Equal("image/jpeg", attachment.ContentType);
            Assert.Contains("attachment://", payload["embeds"]!.AsArray()[0]!.AsObject()["thumbnail"]!.AsObject()["url"]!.ToString());
        }
    }
}
