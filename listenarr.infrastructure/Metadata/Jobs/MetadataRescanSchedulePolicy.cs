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
    public readonly record struct MetadataRescanCounters(int Failure, int Unavailable, int Blocked);

    // DueUtc is null when the due time stays as stored (Completed: the row leaves the partial index).
    public readonly record struct MetadataRescanScheduleChange(MetadataRescanCounters Counters, DateTime? DueUtc);

    // Pure schedule rules for the metadata rescan worker: three bounded exponential ladders and the counter rules per outcome.
    public static class MetadataRescanSchedulePolicy
    {
        public const int BatchSize = 20;
        public const int PageSize = 128;
        public const int MaxPages = 8;
        public const int MaxProbes = 100;
        public const int MaxForeignRows = 256;

        public static readonly TimeSpan FailureBaseDelay = TimeSpan.FromMinutes(30);
        public static readonly TimeSpan FailureMaxDelay = TimeSpan.FromDays(1);
        public static readonly TimeSpan UnavailableBaseDelay = TimeSpan.FromMinutes(30);
        public static readonly TimeSpan UnavailableMaxDelay = TimeSpan.FromHours(6);
        public static readonly TimeSpan BlockedBaseDelay = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan BlockedMaxDelay = TimeSpan.FromHours(2);

        public static TimeSpan FailureDelay(int failureCount) =>
            Ladder(failureCount, FailureBaseDelay, FailureMaxDelay);

        public static TimeSpan UnavailableDelay(int unavailableCount) =>
            Ladder(unavailableCount, UnavailableBaseDelay, UnavailableMaxDelay);

        public static TimeSpan BlockedDelay(int blockedCount) =>
            Ladder(blockedCount, BlockedBaseDelay, BlockedMaxDelay);

        // Returns null when the outcome writes nothing: RowGone (no row to write) and Cancelled (the row stays due).
        public static MetadataRescanScheduleChange? Apply(
            MetadataRescanOutcome outcome,
            MetadataRescanCounters current,
            DateTime nowUtc)
        {
            switch (outcome)
            {
                case MetadataRescanOutcome.Completed:
                    return new(new MetadataRescanCounters(0, 0, 0), null);
                case MetadataRescanOutcome.Incomplete:
                case MetadataRescanOutcome.NoMetadata:
                case MetadataRescanOutcome.ExtractionFailed:
                    var failure = current.Failure + 1;
                    return new(new MetadataRescanCounters(failure, 0, 0), nowUtc + FailureDelay(failure));
                case MetadataRescanOutcome.MoveRecoveryBlocked:
                case MetadataRescanOutcome.GuardRejected:
                case MetadataRescanOutcome.SelectionFailed:
                    var blocked = current.Blocked + 1;
                    return new(new MetadataRescanCounters(current.Failure, 0, blocked), nowUtc + BlockedDelay(blocked));
                case MetadataRescanOutcome.FileMissing:
                case MetadataRescanOutcome.NotOnThisHost:
                    var unavailable = current.Unavailable + 1;
                    return new(new MetadataRescanCounters(current.Failure, unavailable, 0), nowUtc + UnavailableDelay(unavailable));
                case MetadataRescanOutcome.RowGone:
                case MetadataRescanOutcome.Cancelled:
                    return null;
                default:
                    throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null);
            }
        }

        // min(baseDelay * 2^(count - 1), maxDelay); the exponent stops growing once the cap is certain, so no count overflows.
        private static TimeSpan Ladder(int count, TimeSpan baseDelay, TimeSpan maxDelay)
        {
            var delay = baseDelay;
            for (var step = 1; step < count && delay < maxDelay; step++)
            {
                delay += delay;
            }

            return delay < maxDelay ? delay : maxDelay;
        }
    }
}
