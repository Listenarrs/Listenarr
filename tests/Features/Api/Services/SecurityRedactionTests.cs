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

namespace Listenarr.Tests.Features.Api.Services
{
    public class SecurityRedactionTests
    {
        [Fact]
        public async Task NotificationService_LogsAreRedacted_WhenResponseContainsSensitiveValue()
        {
            // Arrange
            var secret = "MY_SUPER_SECRET_KEY";
            Environment.SetEnvironmentVariable("LISTENARR_API_KEY", secret);

            var webhookUrl = "https://discord.com/api/webhooks/test";
            var trigger = "book-added";
            var data = new { id = 1, title = "Redaction Test" };
            var enabledTriggers = new List<string> { trigger };

            string? capturedLog = null;

            var mockHandler = new Mock<HttpMessageHandler>();
            using var errorResponse = new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent($"Error body includes secret: {secret}")
            };
            mockHandler
                .Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(errorResponse);

            using var httpClient = new HttpClient(mockHandler.Object);

            var mockConfigService = new Mock<IConfigurationService>();
            mockConfigService.Setup(x => x.GetStartupConfigAsync()).ReturnsAsync(new StartupConfig { UrlBase = "https://listenarr.example.com" });

            var mockLogger = new Mock<ILogger<NotificationService>>();
            mockLogger
                .Setup(l => l.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(), (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()))
                .Callback(new InvocationAction(invocation =>
                {
                    var formatter = invocation.Arguments[4] as Func<object, Exception?, string>;
                    var state = invocation.Arguments[2];
                    if (formatter != null)
                        capturedLog = formatter.Invoke(state!, null);
                    else
                    {
                        // Try to extract the structured 'Body' value from the state object
                        if (state is System.Collections.IEnumerable enm)
                        {
                            foreach (var kv in enm)
                            {
                                if (kv is System.Collections.Generic.KeyValuePair<string, object> pair && string.Equals(pair.Key, "Body", StringComparison.OrdinalIgnoreCase))
                                {
                                    capturedLog = pair.Value?.ToString();
                                    break;
                                }
                            }
                        }
                        // Last-resort: fallback to ToString()
                        if (capturedLog == null) capturedLog = state?.ToString();
                    }
                }));

            var services = new ServiceCollection();
            services.AddSingleton<INotificationPayloadBuilder, NotificationPayloadBuilderAdapter>();
            var provider = services.BuildServiceProvider();
            var payloadBuilder = provider.GetRequiredService<INotificationPayloadBuilder>();
            var service = new NotificationService(httpClient, mockLogger.Object, mockConfigService.Object, payloadBuilder, Mock.Of<IRequestContextAccessor>());

            // Act
            await service.SendNotificationAsync(trigger, data, webhookUrl, enabledTriggers);

            // Assert
            Assert.NotNull(capturedLog);
            Assert.Contains("<redacted>", capturedLog);
        }

        [Fact]
        public async Task DiscordController_LogsAreRedacted_WhenTokenValidationFails()
        {
            // Arrange
            var secret = "BOT_TOKEN_123";
            Environment.SetEnvironmentVariable("DISCORD_TOKEN", secret);

            var mockConfig = new Mock<IConfigurationService>();
            var appSettings = new ApplicationSettings
            {
                DiscordBotToken = secret,
                DiscordApplicationId = "app123",
                DiscordGuildId = null
            };
            mockConfig.Setup(x => x.GetApplicationSettingsAsync()).ReturnsAsync(appSettings);

            var handler = new Mock<HttpMessageHandler>();
            int calls = 0;
            handler
                .Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync((HttpRequestMessage req, CancellationToken ct) =>
                {
                    // First call returns 200 OK (pre-check) - second call returns Unauthorized so
                    // the controller will proceed to the branch that logs the redacted body.
                    if (req.RequestUri!.ToString().Contains("users/@me"))
                    {
                        var current = System.Threading.Interlocked.Increment(ref calls);
                        if (current == 1)
                        {
                            return new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StringContent("{}")
                            };
                        }

                        return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                        {
                            Content = new StringContent($"Invalid token: {secret}")
                        };
                    }

                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                });

            using var httpClient = new HttpClient(handler.Object);
            var mockFactory = new Mock<IHttpClientFactory>();
            mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

            string? capturedLog = null;
            var mockLogger = new Mock<ILogger<DiscordController>>();
            mockLogger
                .Setup(l => l.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(), (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()))
                .Callback(new InvocationAction(invocation =>
                {
                    var formatter = invocation.Arguments[4] as Func<object, Exception?, string>;
                    var state = invocation.Arguments[2];
                    if (formatter != null)
                        capturedLog = formatter.Invoke(state!, null);
                    else
                    {
                        if (state is System.Collections.IEnumerable enm)
                        {
                            foreach (var kv in enm)
                            {
                                if (kv is System.Collections.Generic.KeyValuePair<string, object> pair && string.Equals(pair.Key, "Body", StringComparison.OrdinalIgnoreCase))
                                {
                                    capturedLog = pair.Value?.ToString();
                                    break;
                                }
                            }
                        }
                        if (capturedLog == null) capturedLog = state?.ToString();
                    }
                }));

            var controller = new DiscordController(mockConfig.Object, mockFactory.Object, mockLogger.Object, Mock.Of<IDiscordBotService>(), Mock.Of<IProcessRunner>(), Mock.Of<IApplicationPathService>());

            // Act
            await controller.GetStatus();

            // Assert
            Assert.NotNull(capturedLog);
            Assert.Contains("<redacted>", capturedLog);
        }
    }
}
