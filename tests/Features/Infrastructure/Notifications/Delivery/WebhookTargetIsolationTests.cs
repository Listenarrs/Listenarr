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

namespace Listenarr.Tests.Features.Infrastructure.Notifications.Delivery
{
    /// <summary>
    /// Covers how one event fans out across several notification targets: one target failing must not
    /// cost the others their delivery.
    /// </summary>
    [Trait("Name", "WebhookTargetIsolationTests")]
    [Trait("Category", "Notifications")]
    public sealed class WebhookTargetIsolationTests : BaseTests
    {
        private const string LegacyUrl = "https://hooks.notification-target.example/TokenABC";
        private const string SubscriberUrl = "https://hooks.other-target.example/inbound";
        private const string NtfyUrl = "https://ntfy.notification-target.example/library-events";

        private static readonly object Payload = new { title = "A Princess of Mars" };

        [Fact]
        public async Task LegacyTargetTimingOut_DoesNotStopConfiguredWebhooksBeingSent()
        {
            using var probe = new WebhookDispatchProbe(
                SettingsWithLegacyAnd(LegacyUrl, SubscriberUrl),
                respond: HangAt(LegacyUrl),
                timeout: TimeSpan.FromMilliseconds(250));

            await probe.Service.SendNotificationAsync(NotificationTriggers.BookAdded, Payload);

            Assert.Equal([LegacyUrl, SubscriberUrl], probe.PostedUrls);
        }

        [Fact]
        public async Task ConfiguredWebhookTimingOut_DoesNotStopTheNextOneBeingSent()
        {
            var settings = new ApplicationSettings
            {
                WebhookUrl = string.Empty,
                EnabledNotificationTriggers = [],
                Webhooks =
                [
                    WebhookDispatchProbe.NewWebhook(LegacyUrl, isEnabled: true, NotificationTriggers.BookAdded),
                    WebhookDispatchProbe.NewWebhook(SubscriberUrl, isEnabled: true, NotificationTriggers.BookAdded)
                ]
            };
            using var probe = new WebhookDispatchProbe(
                settings,
                respond: HangAt(LegacyUrl),
                timeout: TimeSpan.FromMilliseconds(250));

            await probe.Service.SendNotificationAsync(NotificationTriggers.BookAdded, Payload);

            Assert.Equal([LegacyUrl, SubscriberUrl], probe.PostedUrls);
        }

        [Fact]
        public async Task TargetFailingInAWayItsSenderDoesNotHandle_DoesNotStopTheNextOneBeingSent()
        {
            // The NTFY sender only handles the HTTP, JSON and invalid-operation failures it expects;
            // anything else used to leave the dispatch loop and take every later target with it.
            using var probe = new WebhookDispatchProbe(
                SettingsWithLegacyAnd(NtfyUrl, SubscriberUrl),
                respond: (request, _) => request.RequestUri!.ToString() == NtfyUrl
                    ? throw new IOException("The connection was reset mid-response")
                    : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

            await probe.Service.SendNotificationAsync(NotificationTriggers.BookAdded, Payload);

            Assert.Equal([NtfyUrl, SubscriberUrl], probe.PostedUrls);
        }

        [Fact]
        public async Task DeliberateCancellation_StillStopsTheDispatch()
        {
            var legacyRequestArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var probe = new WebhookDispatchProbe(
                SettingsWithLegacyAnd(LegacyUrl, SubscriberUrl),
                respond: async (request, cancellationToken) =>
                {
                    if (request.RequestUri!.ToString() == LegacyUrl)
                    {
                        legacyRequestArrived.TrySetResult();
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    }

                    return new HttpResponseMessage(HttpStatusCode.OK);
                },
                timeout: Timeout.InfiniteTimeSpan);

            var dispatch = probe.Service.SendNotificationAsync(NotificationTriggers.BookAdded, Payload);
            await legacyRequestArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            probe.HttpClient.CancelPendingRequests();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal([LegacyUrl], probe.PostedUrls);
        }

        private static ApplicationSettings SettingsWithLegacyAnd(string legacyUrl, string configuredUrl)
            => new()
            {
                WebhookUrl = legacyUrl,
                EnabledNotificationTriggers = [.. NotificationTriggers.UserSelectable],
                Webhooks = [WebhookDispatchProbe.NewWebhook(configuredUrl, isEnabled: true, NotificationTriggers.BookAdded)]
            };

        /// <summary>Answers every target at once except <paramref name="slowUrl"/>, which never answers.</summary>
        private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> HangAt(string slowUrl)
            => async (request, cancellationToken) =>
            {
                if (request.RequestUri!.ToString() == slowUrl)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }

                return new HttpResponseMessage(HttpStatusCode.OK);
            };
    }
}
