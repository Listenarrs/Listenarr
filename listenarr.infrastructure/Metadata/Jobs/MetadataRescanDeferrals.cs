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
namespace Listenarr.Infrastructure.Metadata.Jobs
{
    // Per-file retry schedule for the periodic metadata rescan. Candidates are
    // selected lowest id first, so a file that is attempted and still needs
    // metadata afterwards would otherwise be selected again on every cycle and
    // keep later files out of the batch. Every attempt pushes that file's next
    // turn back, doubling up to a day. A successful refresh counts as an attempt
    // too, because metadata the file does not carry (a sample rate, say) leaves
    // the row a candidate after it was refreshed.
    internal sealed class MetadataRescanDeferrals(TimeProvider timeProvider)
    {
        internal static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(30);
        internal static readonly TimeSpan MaximumDelay = TimeSpan.FromDays(1);

        private readonly Dictionary<int, Deferral> _deferrals = [];

        public bool IsDeferred(int fileId)
        {
            lock (_deferrals)
            {
                return _deferrals.TryGetValue(fileId, out var deferral)
                    && deferral.RetryAfter > timeProvider.GetUtcNow();
            }
        }

        public void RecordAttempt(int fileId, bool refreshed)
        {
            lock (_deferrals)
            {
                _deferrals.TryGetValue(fileId, out var previous);
                var attempts = previous.Attempts + 1;
                _deferrals[fileId] = new Deferral(
                    attempts,
                    timeProvider.GetUtcNow() + DelayAfter(attempts),
                    FailureReported: !refreshed && previous.FailureReported);
            }
        }

        // Returns true only for the first failure since the file was last
        // refreshed, so each failing file is reported once and not every cycle.
        public bool ClaimFailureReport(int fileId)
        {
            lock (_deferrals)
            {
                _deferrals.TryGetValue(fileId, out var previous);
                if (previous.FailureReported)
                {
                    return false;
                }

                _deferrals[fileId] = previous with { FailureReported = true };
                return true;
            }
        }

        // Entries whose retry time passed more than a full maximum delay ago
        // belong to rows that left the candidate set or are no longer reached;
        // dropping them bounds the map and only resets that file's schedule.
        public void PruneSettled()
        {
            var cutoff = timeProvider.GetUtcNow() - MaximumDelay;
            lock (_deferrals)
            {
                foreach (var fileId in _deferrals
                    .Where(entry => entry.Value.RetryAfter <= cutoff)
                    .Select(entry => entry.Key)
                    .ToList())
                {
                    _deferrals.Remove(fileId);
                }
            }
        }

        private static TimeSpan DelayAfter(int attempts)
        {
            var doublings = Math.Min(attempts - 1, 16);
            var delay = InitialDelay * Math.Pow(2, doublings);
            return delay < MaximumDelay ? delay : MaximumDelay;
        }

        private readonly record struct Deferral(
            int Attempts,
            DateTimeOffset RetryAfter,
            bool FailureReported);
    }
}
