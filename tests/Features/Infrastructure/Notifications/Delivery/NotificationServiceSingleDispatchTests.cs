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

namespace Listenarr.Tests.Features.Infrastructure.Notifications.Delivery
{
    // Regression tests: the Discord and NTFY handlers must not fall through to the
    // generic webhook sender after handling the notification. The fall-through posted a
    // second copy of every Discord/NTFY notification; for Discord the duplicate used a
    // payload Discord rejects with 400 {"embeds": ["0"]} whenever the embed contained a
    // relative thumbnail URL, making delivery look broken even though the first send
    // had succeeded.
    public partial class NotificationServiceTests
    {
        private static (NotificationService Service, Func<int> GetRequestCount) CreateServiceWithCountingHandler()
        {
            var requestCount = 0;
            var mockHandler = new Mock<HttpMessageHandler>();
            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .Callback<HttpRequestMessage, CancellationToken>((_, _) => Interlocked.Increment(ref requestCount))
                .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK));

            var httpClient = new HttpClient(mockHandler.Object);

            var mockConfigService = new Mock<IConfigurationService>();
            mockConfigService
                .Setup(x => x.GetStartupConfigAsync())
                .ReturnsAsync(new StartupConfig { UrlBase = "https://listenarr.example.com" });

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

            return (service, () => requestCount);
        }

        [Fact]
        public async Task SendNotificationAsync_DiscordWebhook_PostsExactlyOnce()
        {
            var (service, getRequestCount) = CreateServiceWithCountingHandler();
            var trigger = "book-added";
            var data = new
            {
                id = 1,
                title = "Test Book",
                authors = new[] { "Jane Doe" },
                asin = "B00TEST"
            };

            await service.SendNotificationAsync(
                trigger,
                data,
                "https://discord.com/api/webhooks/123456/token",
                new List<string> { trigger });

            Assert.Equal(1, getRequestCount());
        }

        [Fact]
        public async Task SendNotificationAsync_NtfyWebhook_PostsExactlyOnce()
        {
            var (service, getRequestCount) = CreateServiceWithCountingHandler();
            var trigger = "book-added";
            var data = new
            {
                id = 1,
                title = "Test Book",
                authors = new[] { "Jane Doe" },
                asin = "B00TEST"
            };

            await service.SendNotificationAsync(
                trigger,
                data,
                "https://ntfy.sh/listenarr-test",
                new List<string> { trigger });

            Assert.Equal(1, getRequestCount());
        }
    }
}
