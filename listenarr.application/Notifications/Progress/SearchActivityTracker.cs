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

namespace Listenarr.Application.Notifications.Progress
{
    /// <summary>
    /// One automatic-search activity event. <see cref="Stage"/> is a small
    /// machine-readable vocabulary the UI styles by:
    /// <c>searching</c> (a book is being queried), <c>grabbed</c> (a release was
    /// queued), <c>no_results</c> (nothing found), <c>idle</c> (the sweep is not
    /// running). <see cref="Timestamp"/> is UTC.
    /// </summary>
    public sealed record SearchActivityEvent(
        string Message,
        string Stage,
        int? AudiobookId,
        string? Asin,
        DateTime Timestamp);

    /// <summary>
    /// In-memory, process-wide record of what the automatic-search sweep is
    /// doing right now plus a short rolling history of recent outcomes. Lets a
    /// freshly-loaded page hydrate the live indicator immediately instead of
    /// waiting for the next SignalR event (which during an idle period could be
    /// hours away). Volatile by design — this is ambient status, not audit data;
    /// History already persists grabs.
    /// </summary>
    public interface ISearchActivityTracker
    {
        /// <summary>The most recent event of any stage (including idle).</summary>
        SearchActivityEvent? Current { get; }

        /// <summary>
        /// Record an event. When <paramref name="addToFeed"/> is true the event
        /// also enters the rolling recent-history buffer; transient stages
        /// (<c>searching</c>, <c>idle</c>) pass false so the feed reads as a list
        /// of outcomes rather than a doubled play-by-play.
        /// </summary>
        void Record(SearchActivityEvent evt, bool addToFeed = true);

        /// <summary>Current event plus the recent-outcome buffer (newest first).</summary>
        (SearchActivityEvent? Current, IReadOnlyList<SearchActivityEvent> Recent) Snapshot();
    }

    /// <inheritdoc />
    public sealed class SearchActivityTracker : ISearchActivityTracker
    {
        // Enough history to fill an activity panel without unbounded growth.
        private const int MaxRecent = 30;

        private readonly object _gate = new();
        private readonly LinkedList<SearchActivityEvent> _recent = new();
        private SearchActivityEvent? _current;

        public SearchActivityEvent? Current
        {
            get { lock (_gate) { return _current; } }
        }

        public void Record(SearchActivityEvent evt, bool addToFeed = true)
        {
            if (evt == null) return;
            lock (_gate)
            {
                _current = evt;
                if (addToFeed)
                {
                    _recent.AddFirst(evt);
                    while (_recent.Count > MaxRecent)
                    {
                        _recent.RemoveLast();
                    }
                }
            }
        }

        public (SearchActivityEvent? Current, IReadOnlyList<SearchActivityEvent> Recent) Snapshot()
        {
            lock (_gate)
            {
                return (_current, _recent.ToList());
            }
        }
    }
}
