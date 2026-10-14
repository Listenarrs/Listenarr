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

namespace Listenarr.Tests.Features.Domain.Notifications
{
    [Trait("Name", nameof(NotificationTriggersTests))]
    [Trait("Category", "Notifications")]
    public class NotificationTriggersTests : BaseTests
    {
        [Fact]
        public void Catalog_HasUniqueIds()
        {
            var ids = NotificationTriggers.Catalog.Select(t => t.Id).ToList();

            Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
            Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));
        }

        [Fact]
        public void Catalog_OrdersAreStrictlyAscending()
        {
            var orders = NotificationTriggers.Catalog.Select(t => t.Order).ToList();
            var sorted = orders.OrderBy(o => o).ToList();

            Assert.Equal(sorted, orders);
            Assert.Equal(orders.Count, orders.Distinct().Count());
        }

        [Fact]
        public void DefaultEnabled_IsSubsetOfCatalog_AndMatchesHistoricalSet()
        {
            var catalogIds = NotificationTriggers.Catalog.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);

            Assert.All(NotificationTriggers.DefaultEnabled, id => Assert.Contains(id, catalogIds));

            // Back-compat: the default-enabled set must stay the four historical triggers so an
            // upgrade does not start emitting new notification types unexpectedly.
            Assert.Equal(
                new[]
                {
                    NotificationTriggers.BookAdded,
                    NotificationTriggers.BookDownloading,
                    NotificationTriggers.BookAvailable,
                    NotificationTriggers.BookCompleted,
                },
                NotificationTriggers.DefaultEnabled);
        }

        [Fact]
        public void IsKnown_TrueForCatalogIds_FalseForOthers()
        {
            Assert.All(
                NotificationTriggers.Catalog.Select(t => t.Id),
                id => Assert.True(NotificationTriggers.IsKnown(id)));

            Assert.False(NotificationTriggers.IsKnown("book-nonexistent"));
            Assert.False(NotificationTriggers.IsKnown(""));
        }

        [Fact]
        public void UpgradedTrigger_IsCataloguedAndKnown()
        {
            // book-upgraded now fires (an import that replaced an existing book's files), so it is
            // catalogued and recognized like any other trigger.
            Assert.Equal("book-upgraded", NotificationTriggers.BookUpgraded);
            Assert.Contains(
                NotificationTriggers.Catalog,
                t => string.Equals(t.Id, NotificationTriggers.BookUpgraded, StringComparison.Ordinal));
            Assert.True(NotificationTriggers.IsKnown(NotificationTriggers.BookUpgraded));
        }
    }
}
