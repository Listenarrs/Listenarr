/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Notifications.Payloads
{
    /// <summary>
    /// Pins the human-friendly Discord "content" line the payload builder produces for each
    /// lifecycle trigger, so the expanded trigger set no longer falls back to a raw "[trigger]".
    /// </summary>
    [Trait("Name", nameof(NotificationPayloadContentTests))]
    [Trait("Category", "Notifications")]
    public class NotificationPayloadContentTests : BaseTests
    {
        private static INotificationPayloadBuilder CreateBuilder()
        {
            var services = new ServiceCollection();
            services.AddSingleton<INotificationPayloadBuilder, NotificationPayloadBuilderAdapter>();
            return services.BuildServiceProvider().GetRequiredService<INotificationPayloadBuilder>();
        }

        private static string Content(string trigger, object data) =>
            CreateBuilder()
                .CreateDiscordPayload(trigger, data, "https://listenarr.example.com")!
                .AsObject()["content"]!
                .ToString();

        [Theory]
        [InlineData("book-wanted", "The Book by The Author is wanted")]
        [InlineData("book-grabbed", "The Book by The Author was grabbed")]
        [InlineData("book-download-completed", "The Book by The Author finished downloading")]
        [InlineData("book-imported", "The Book by The Author was imported")]
        [InlineData("book-completed", "The Book by The Author is complete")]
        [InlineData("book-download-failed", "The Book by The Author failed to download")]
        [InlineData("book-import-failed", "The Book by The Author failed to import")]
        [InlineData("book-renamed", "The Book by The Author was renamed")]
        [InlineData("book-deleted", "The Book by The Author was deleted")]
        public void Content_WithTitleAndAuthor_UsesFriendlyPhrase(string trigger, string expected)
        {
            var data = new { title = "The Book", authors = new[] { "The Author" } };

            Assert.Equal(expected, Content(trigger, data));
        }

        [Fact]
        public void Content_WithTitleOnly_OmitsAuthorClause()
        {
            var data = new { title = "Solo" };

            Assert.Equal("Solo was grabbed", Content("book-grabbed", data));
        }

        [Fact]
        public void Content_WithNoTitle_FallsBackToGenericSentence()
        {
            var data = new { asin = "B000NOPE" };

            Assert.Equal("An audiobook is wanted", Content("book-wanted", data));
        }

        [Fact]
        public void Content_UnknownTrigger_StillUsesBracketFallback()
        {
            var data = new { title = "Mystery" };

            Assert.Equal("[book-unknown] Mystery", Content("book-unknown", data));
        }
    }
}
