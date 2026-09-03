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
using System.Text.RegularExpressions;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Notifications.Delivery
{
    /// <summary>
    /// Covers the subscription check that decides whether a configured webhook hears about an event.
    /// </summary>
    /// <remarks>
    /// The settings screen writes trigger names onto a webhook, and the dispatch code compares those
    /// names against the ones it emits. Those two ends were written independently and shared nothing,
    /// so every webhook added through the settings screen was silently inert. The tests here assert
    /// against the shipped vocabulary rather than against a name the test itself supplies, because a
    /// test that feeds a trigger name in and reads the same name back cannot observe that drift.
    /// </remarks>
    [Trait("Name", "WebhookTriggerDispatchTests")]
    [Trait("Category", "Notifications")]
    public sealed class WebhookTriggerDispatchTests : BaseTests
    {
        private const string WebhookUrl = "https://hooks.notification-target.example/inbound";
        private const string SecondWebhookUrl = "https://hooks.other-target.example/inbound";

        public static TheoryData<string> UserSelectableTriggers()
        {
            var data = new TheoryData<string>();
            foreach (var trigger in NotificationTriggers.UserSelectable)
            {
                data.Add(trigger);
            }

            return data;
        }

        [Theory]
        [MemberData(nameof(UserSelectableTriggers))]
        public async Task WebhookSubscribedThroughTheSettingsScreen_ReceivesTheMatchingEvent(string trigger)
        {
            using var probe = DispatchProbe.ForWebhook(trigger);

            await probe.Service.SendNotificationAsync(trigger, new { title = "A Synthetic Audiobook" });

            Assert.Equal(
                [WebhookUrl],
                probe.PostedUrls);
        }

        [Fact]
        public async Task ImportedDownload_ReachesAWebhookSubscribedToProcessingComplete()
        {
            using var probe = DispatchProbe.ForWebhook(NotificationTriggers.BookCompleted);

            await probe.Service.OnDownloadImportedAsync(ImportedDownload());

            Assert.Equal(
                [WebhookUrl],
                probe.PostedUrls);
        }

        [Fact]
        public async Task ImportedDownload_StillReachesAWebhookSavedAgainstTheOlderInternalName()
        {
            using var probe = DispatchProbe.ForWebhook("Imported");

            await probe.Service.OnDownloadImportedAsync(ImportedDownload());

            Assert.Equal(
                [WebhookUrl],
                probe.PostedUrls);
        }

        [Fact]
        public async Task FailedDownload_ReachesAWebhookSubscribedToTheFailureTrigger()
        {
            using var probe = DispatchProbe.ForWebhook(NotificationTriggers.DownloadFailed);

            await probe.Service.OnDownloadFailedAsync(FailedDownload());

            Assert.Equal(
                [WebhookUrl],
                probe.PostedUrls);
        }

        [Fact]
        public async Task WebhookSubscribedToADifferentTrigger_HearsNothing()
        {
            using var probe = DispatchProbe.ForWebhook(NotificationTriggers.BookAdded);

            await probe.Service.SendNotificationAsync(
                NotificationTriggers.BookDownloading,
                new { title = "A Synthetic Audiobook" });

            Assert.Empty(probe.PostedUrls);
        }

        [Fact]
        public async Task DisabledWebhook_HearsNothing()
        {
            using var probe = DispatchProbe.ForWebhook(NotificationTriggers.BookAdded, isEnabled: false);

            await probe.Service.SendNotificationAsync(
                NotificationTriggers.BookAdded,
                new { title = "A Synthetic Audiobook" });

            Assert.Empty(probe.PostedUrls);
        }

        [Fact]
        public async Task StoredTriggerCasing_DoesNotDecideWhetherAWebhookFires()
        {
            using var probe = DispatchProbe.ForWebhook("Book-Added");

            await probe.Service.SendNotificationAsync(
                NotificationTriggers.BookAdded,
                new { title = "A Synthetic Audiobook" });

            Assert.Equal(
                [WebhookUrl],
                probe.PostedUrls);
        }

        [Fact]
        public async Task EveryMatchingWebhookIsSent_AndTheLegacyTargetIsNotSentTwice()
        {
            var settings = new ApplicationSettings
            {
                WebhookUrl = WebhookUrl,
                EnabledNotificationTriggers = [.. NotificationTriggers.UserSelectable],
                Webhooks =
                [
                    // Same URL as the legacy single-webhook setting: one event, one delivery.
                    NewWebhook(WebhookUrl, NotificationTriggers.BookAdded),
                    NewWebhook(SecondWebhookUrl, NotificationTriggers.BookAdded)
                ]
            };

            using var probe = new DispatchProbe(settings);

            await probe.Service.SendNotificationAsync(
                NotificationTriggers.BookAdded,
                new { title = "A Synthetic Audiobook" });

            Assert.Equal(
                [WebhookUrl, SecondWebhookUrl],
                probe.PostedUrls);
        }

        [Fact]
        public void DefaultEnabledTriggers_AreExactlyTheUserSelectableVocabulary()
        {
            Assert.Equal(
                NotificationTriggers.UserSelectable,
                new ApplicationSettings().EnabledNotificationTriggers);
        }

        [Fact]
        public void SettingsScreenOffersExactlyTheTriggersTheBackendCanDispatch()
        {
            var source = File.ReadAllText(Path.Join(
                TestUtils.FindRepositoryRoot(),
                "fe",
                "src",
                "views",
                "settings",
                "NotificationsTab.vue"));

            var checkboxLoop = Regex.Match(
                source,
                @"v-for=""t in \[(?<triggers>[^\]]*)\]""",
                RegexOptions.None,
                TimeSpan.FromSeconds(5));
            Assert.True(
                checkboxLoop.Success,
                "The trigger checkbox list could not be found in NotificationsTab.vue. "
                + "If the markup moved, point this test at its new home rather than deleting it: "
                + "it is the only thing tying the names the screen offers to the names the backend dispatches.");

            var offered = Regex
                .Matches(
                    checkboxLoop.Groups["triggers"].Value,
                    @"'(?<trigger>[^']+)'",
                    RegexOptions.None,
                    TimeSpan.FromSeconds(5))
                .Select(match => match.Groups["trigger"].Value)
                .ToArray();

            Assert.Equal(NotificationTriggers.UserSelectable, offered);
        }

        [Fact]
        public void EveryUserSelectableTrigger_IsEmittedBySomeProductionCallSite()
        {
            var repositoryRoot = TestUtils.FindRepositoryRoot();
            var vocabularyDefinition = Path.Join(
                repositoryRoot,
                "listenarr.domain",
                "Configuration",
                "NotificationTriggers.cs");
            var productionSources = new[]
                {
                    "listenarr.domain",
                    "listenarr.application",
                    "listenarr.infrastructure",
                    "listenarr.api"
                }
                .SelectMany(project => Directory.EnumerateFiles(
                    Path.Join(repositoryRoot, project),
                    "*.cs",
                    SearchOption.AllDirectories))
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Where(file => !string.Equals(file, vocabularyDefinition, StringComparison.Ordinal))
                .Select(File.ReadAllText)
                .ToArray();

            var memberNames = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [NotificationTriggers.BookAdded] = nameof(NotificationTriggers.BookAdded),
                [NotificationTriggers.BookDownloading] = nameof(NotificationTriggers.BookDownloading),
                [NotificationTriggers.BookAvailable] = nameof(NotificationTriggers.BookAvailable),
                [NotificationTriggers.BookCompleted] = nameof(NotificationTriggers.BookCompleted)
            };

            var unemitted = NotificationTriggers.UserSelectable
                .Where(trigger => !productionSources.Any(source =>
                    source.Contains($"{nameof(NotificationTriggers)}.{memberNames[trigger]}", StringComparison.Ordinal)))
                .ToArray();

            Assert.True(
                unemitted.Length == 0,
                "The settings screen lets a user subscribe to these triggers, but no production code emits them, "
                + "so the checkbox can never do anything: "
                + string.Join(", ", unemitted));
        }

        private static Download ImportedDownload()
            => new()
            {
                Id = "download-imported",
                Title = "A Synthetic Audiobook"
            };

        private static Download FailedDownload()
            => new()
            {
                Id = "download-failed",
                Title = "A Synthetic Audiobook",
                ErrorMessage = "The synthetic download client refused the release"
            };

        private static WebhookConfiguration NewWebhook(string url, params string[] triggers)
            => new()
            {
                Name = "Test target",
                Url = url,
                Type = "Zapier",
                Triggers = [.. triggers],
                IsEnabled = true
            };

        /// <summary>
        /// A <see cref="NotificationService"/> wired to a captured transport, so a test can count the
        /// outbound requests an event actually produced.
        /// </summary>
        private sealed class DispatchProbe : IDisposable
        {
            private readonly HttpClient _httpClient;
            private readonly List<Uri> _postedUrls = [];

            public DispatchProbe(ApplicationSettings settings)
            {
                var handler = new Mock<HttpMessageHandler>();
                handler
                    .Protected()
                    .Setup<Task<HttpResponseMessage>>(
                        "SendAsync",
                        ItExpr.IsAny<HttpRequestMessage>(),
                        ItExpr.IsAny<CancellationToken>())
                    .Callback<HttpRequestMessage, CancellationToken>((request, _) =>
                    {
                        if (request.Method == HttpMethod.Post && request.RequestUri != null)
                        {
                            _postedUrls.Add(request.RequestUri);
                        }
                    })
                    .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK));

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

                _httpClient = new HttpClient(handler.Object);
                Service = new NotificationService(
                    _httpClient,
                    Mock.Of<ILogger<NotificationService>>(),
                    configurationService.Object,
                    new NotificationPayloadBuilderAdapter(),
                    Mock.Of<IRequestContextAccessor>());
            }

            public NotificationService Service { get; }

            /// <summary>Every URL that received an outbound POST, in delivery order.</summary>
            public IReadOnlyList<string> PostedUrls
                => [.. _postedUrls.Select(uri => uri.ToString())];

            public static DispatchProbe ForWebhook(string storedTrigger, bool isEnabled = true)
                => new(new ApplicationSettings
                {
                    WebhookUrl = string.Empty,
                    EnabledNotificationTriggers = [],
                    Webhooks =
                    [
                        new WebhookConfiguration
                        {
                            Name = "Test target",
                            Url = WebhookUrl,
                            Type = "Zapier",
                            Triggers = [storedTrigger],
                            IsEnabled = isEnabled
                        }
                    ]
                });

            public void Dispose() => _httpClient.Dispose();
        }
    }
}
