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

using Listenarr.Application.Notifications.Progress;

namespace Listenarr.Tests.Features.Application.Notifications
{
    [Trait("Category", "SearchActivityTracker")]
    public class SearchActivityTrackerTests
    {
        private static SearchActivityEvent Evt(string stage, string message = "msg")
            => new(message, stage, null, null, DateTime.UtcNow);

        [Fact(DisplayName = "Records set Current; outcomes enter the feed, transient stages do not")]
        public void Record_FeedSemantics()
        {
            var tracker = new SearchActivityTracker();

            // Transient: updates Current but not the feed.
            tracker.Record(Evt("searching", "Searching 13 indexers for X"), addToFeed: false);
            var (current1, recent1) = tracker.Snapshot();
            Assert.Equal("searching", current1!.Stage);
            Assert.Empty(recent1);

            // Outcome: updates Current AND the feed.
            tracker.Record(Evt("grabbed", "Grabbed 1 for X"));
            var (current2, recent2) = tracker.Snapshot();
            Assert.Equal("grabbed", current2!.Stage);
            Assert.Single(recent2);

            // Idle: transient, current-only.
            tracker.Record(Evt("idle", "Search idle"), addToFeed: false);
            var (current3, recent3) = tracker.Snapshot();
            Assert.Equal("idle", current3!.Stage);
            Assert.Single(recent3); // unchanged
        }

        [Fact(DisplayName = "Feed is newest-first")]
        public void Feed_NewestFirst()
        {
            var tracker = new SearchActivityTracker();
            tracker.Record(Evt("grabbed", "first"));
            tracker.Record(Evt("no_results", "second"));

            var (_, recent) = tracker.Snapshot();
            Assert.Equal(new[] { "second", "first" }, recent.Select(e => e.Message).ToArray());
        }

        [Fact(DisplayName = "Feed is capped at 30 (oldest dropped)")]
        public void Feed_Capped()
        {
            var tracker = new SearchActivityTracker();
            for (int i = 0; i < 45; i++)
            {
                tracker.Record(Evt("no_results", $"book {i}"));
            }

            var (_, recent) = tracker.Snapshot();
            Assert.Equal(30, recent.Count);
            // Newest first → book 44 at the head, book 15 at the tail (0..14 dropped).
            Assert.Equal("book 44", recent.First().Message);
            Assert.Equal("book 15", recent.Last().Message);
        }

        [Fact(DisplayName = "Empty tracker snapshots to null current and empty feed")]
        public void Empty_Snapshot()
        {
            var tracker = new SearchActivityTracker();
            var (current, recent) = tracker.Snapshot();
            Assert.Null(current);
            Assert.Empty(recent);
        }
    }
}
