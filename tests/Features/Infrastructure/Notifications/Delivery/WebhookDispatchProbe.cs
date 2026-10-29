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
    /// <summary>
    /// A <see cref="NotificationService"/> wired to a captured transport, so a test can count the
    /// outbound requests an event actually produced and decide how each target answers.
    /// </summary>
    internal sealed class WebhookDispatchProbe : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly CapturingHandler _handler;

        /// <param name="settings">The settings the dispatcher reads its targets from.</param>
        /// <param name="respond">
        /// Answers each outbound request. Defaults to 200 OK for every target, so a test only supplies
        /// one when a target has to be slow, fail or be cancelled.
        /// </param>
        /// <param name="timeout">The client timeout, as the production typed client would apply it.</param>
        public WebhookDispatchProbe(
            ApplicationSettings settings,
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? respond = null,
            TimeSpan? timeout = null)
        {
            _handler = new CapturingHandler(respond ?? ((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));

            var configurationService = new Mock<IConfigurationService>();
            configurationService
                .Setup(service => service.GetApplicationSettingsAsync())
                .ReturnsAsync(settings);
            configurationService
                .Setup(service => service.GetWebhookConfigurationsAsync())
                .ReturnsAsync(settings.Webhooks ?? []);
            configurationService
                .Setup(service => service.GetStartupConfigAsync())
                .ReturnsAsync(new StartupConfig());

            _httpClient = new HttpClient(_handler);
            if (timeout.HasValue)
            {
                _httpClient.Timeout = timeout.Value;
            }

            Service = new NotificationService(
                _httpClient,
                Mock.Of<ILogger<NotificationService>>(),
                configurationService.Object,
                new NotificationPayloadBuilderAdapter(),
                Mock.Of<IRequestContextAccessor>());
        }

        public NotificationService Service { get; }

        /// <summary>The client the service sends through, for tests that cancel it deliberately.</summary>
        public HttpClient HttpClient => _httpClient;

        /// <summary>Every URL an outbound POST was attempted against, in delivery order.</summary>
        public IReadOnlyList<string> PostedUrls
            => [.. _handler.Requests.Select(request => request.RequestUri!.ToString())];

        /// <summary>Every outbound POST, in delivery order, for tests that inspect headers.</summary>
        public IReadOnlyList<HttpRequestMessage> Requests => _handler.Requests;

        public static WebhookDispatchProbe ForWebhook(string url, string storedTrigger, bool isEnabled = true)
            => new(new ApplicationSettings
            {
                WebhookUrl = string.Empty,
                EnabledNotificationTriggers = [],
                Webhooks = [NewWebhook(url, isEnabled, storedTrigger)]
            });

        public static WebhookConfiguration NewWebhook(string url, bool isEnabled, params string[] triggers)
            => new()
            {
                Name = "Test target",
                Url = url,
                Type = "Zapier",
                Triggers = [.. triggers],
                IsEnabled = isEnabled
            };

        public void Dispose() => _httpClient.Dispose();

        private sealed class CapturingHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
        {
            private readonly List<HttpRequestMessage> _requests = [];

            public IReadOnlyList<HttpRequestMessage> Requests
            {
                get
                {
                    lock (_requests)
                    {
                        return [.. _requests];
                    }
                }
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.Method == HttpMethod.Post && request.RequestUri != null)
                {
                    lock (_requests)
                    {
                        _requests.Add(request);
                    }
                }

                return respond(request, cancellationToken);
            }
        }
    }
}
