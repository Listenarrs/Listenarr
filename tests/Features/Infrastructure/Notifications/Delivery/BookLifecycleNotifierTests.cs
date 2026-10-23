/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Domain.Notifications;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Notifications.Delivery
{
    [Trait("Name", nameof(BookLifecycleNotifierTests))]
    [Trait("Category", "Notifications")]
    public class BookLifecycleNotifierTests : BaseTests
    {
        private static BookLifecycleNotifier CreateSut(
            Mock<INotificationService> notificationService,
            Mock<IConfigurationService> configurationService) =>
            new(
                notificationService.Object,
                configurationService.Object,
                Mock.Of<ILogger<BookLifecycleNotifier>>());

        [Fact]
        public async Task NotifyAsync_ForwardsTriggerAndPayloadWithConfiguredWebhookAndEnabledTriggers()
        {
            var settings = new ApplicationSettings
            {
                WebhookUrl = "https://hooks.example.com/abc",
                EnabledNotificationTriggers = new List<string> { NotificationTriggers.BookWanted },
            };
            var configurationService = new Mock<IConfigurationService>();
            configurationService
                .Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(settings);
            var notificationService = new Mock<INotificationService>();
            var sut = CreateSut(notificationService, configurationService);
            var payload = new { id = 42, title = "A Book" };

            await sut.NotifyAsync(NotificationTriggers.BookWanted, payload);

            notificationService.Verify(
                n => n.SendNotificationAsync(
                    NotificationTriggers.BookWanted,
                    payload,
                    "https://hooks.example.com/abc",
                    settings.EnabledNotificationTriggers),
                Times.Once);
        }

        [Fact]
        public async Task NotifyAsync_SwallowsExceptions_AndDoesNotThrow()
        {
            var configurationService = new Mock<IConfigurationService>();
            configurationService
                .Setup(c => c.GetApplicationSettingsAsync())
                .ThrowsAsync(new InvalidOperationException("config unavailable"));
            var notificationService = new Mock<INotificationService>();
            var sut = CreateSut(notificationService, configurationService);

            var exception = await Record.ExceptionAsync(
                () => sut.NotifyAsync(NotificationTriggers.BookGrabbed, new { id = 1 }));

            Assert.Null(exception);
            notificationService.Verify(
                n => n.SendNotificationAsync(
                    It.IsAny<string>(),
                    It.IsAny<object>(),
                    It.IsAny<string>(),
                    It.IsAny<List<string>>()),
                Times.Never);
        }

        [Fact]
        public async Task NotifyAsync_HonorsCancellation_WhichIsNotSwallowed()
        {
            var configurationService = new Mock<IConfigurationService>();
            configurationService
                .Setup(c => c.GetApplicationSettingsAsync())
                .ThrowsAsync(new OperationCanceledException());
            var notificationService = new Mock<INotificationService>();
            var sut = CreateSut(notificationService, configurationService);

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => sut.NotifyAsync(NotificationTriggers.BookImported, new { id = 1 }));
        }
    }
}
