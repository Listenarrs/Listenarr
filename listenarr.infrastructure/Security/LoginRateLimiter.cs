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
using System.Collections.Concurrent;
using Listenarr.Application.Interfaces;

namespace Listenarr.Infrastructure.Security
{

    public class LoginRateLimiter : ILoginRateLimiter
    {
        private class Entry
        {
            // Failures is incremented atomically via Interlocked; BlockUntil is
            // written inside a lock on the Entry instance to avoid TOCTOU races.
            public int Failures;
            public DateTime? BlockUntil;
        }

        private readonly ConcurrentDictionary<string, Entry> _map = new();

        // Configurable thresholds
        private readonly int _maxFailures = 5;
        private readonly TimeSpan _blockDuration = TimeSpan.FromMinutes(10);

        public bool IsBlocked(string key)
        {
            if (_map.TryGetValue(key, out var e))
            {
                lock (e)
                {
                    if (e.BlockUntil.HasValue && e.BlockUntil.Value > DateTime.UtcNow) return true;
                }
            }
            return false;
        }

        public int GetSecondsUntilUnblock(string key)
        {
            if (_map.TryGetValue(key, out var e) && e.BlockUntil.HasValue)
            {
                lock (e)
                {
                    if (e.BlockUntil.HasValue)
                    {
                        var ts = e.BlockUntil.Value - DateTime.UtcNow;
                        return ts.Ticks > 0 ? (int)Math.Ceiling(ts.TotalSeconds) : 0;
                    }
                }
            }
            return 0;
        }

        public void RecordFailure(string key)
        {
            var entry = _map.GetOrAdd(key, _ => new Entry());
            // Atomically increment and then decide whether to set BlockUntil under the same lock
            // so that concurrent callers cannot both observe Failures < _maxFailures and skip blocking.
            lock (entry)
            {
                entry.Failures++;
                if (entry.Failures >= _maxFailures)
                {
                    entry.BlockUntil = DateTime.UtcNow.Add(_blockDuration);
                }
            }
        }

        public void RecordSuccess(string key)
        {
            _map.TryRemove(key, out _);
        }
    }
}
