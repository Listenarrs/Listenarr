/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Api.Features.Notifications;
using Listenarr.Domain.Notifications;
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Tests.Features.Api.Notifications
{
    [Trait("Name", nameof(NotificationsControllerTests))]
    [Trait("Category", "Notifications")]
    public class NotificationsControllerTests : BaseTests
    {
        private static NotificationsController CreateSut() =>
            new(
                Mock.Of<IConfigurationService>(),
                Mock.Of<ILogger<NotificationsController>>(),
                Mock.Of<INotificationService>());

        [Fact]
        public void GetTriggers_ReturnsWholeCatalog_OrderedByLifecycle()
        {
            var sut = CreateSut();

            var result = sut.GetTriggers();

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var items = Assert.IsAssignableFrom<System.Collections.IEnumerable>(ok.Value)
                .Cast<object>()
                .ToList();

            var expectedIds = NotificationTriggers.Catalog
                .OrderBy(t => t.Order)
                .Select(t => t.Id)
                .ToList();
            var actualIds = items
                .Select(item => item.GetType().GetProperty("id")!.GetValue(item)!.ToString()!)
                .ToList();

            Assert.Equal(expectedIds, actualIds);
        }

        [Fact]
        public void GetTriggers_EveryItem_HasNameAndDescription()
        {
            var sut = CreateSut();

            var result = sut.GetTriggers();

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var items = Assert.IsAssignableFrom<System.Collections.IEnumerable>(ok.Value)
                .Cast<object>()
                .ToList();

            Assert.All(items, item =>
            {
                var name = item.GetType().GetProperty("name")!.GetValue(item)?.ToString();
                var description = item.GetType().GetProperty("description")!.GetValue(item)?.ToString();
                Assert.False(string.IsNullOrWhiteSpace(name));
                Assert.False(string.IsNullOrWhiteSpace(description));
            });
        }
    }
}
