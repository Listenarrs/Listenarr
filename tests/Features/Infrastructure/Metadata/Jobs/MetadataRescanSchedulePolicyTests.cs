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

namespace Listenarr.Tests.Features.Infrastructure.Metadata.Jobs
{
    [Trait("Area", "Metadata")]
    [Trait("Name", "MetadataRescanSchedulePolicyTests")]
    [Trait("Category", "Infrastructure")]
    public sealed class MetadataRescanSchedulePolicyTests : BaseTests
    {
        private static readonly DateTime Now = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        private static readonly MetadataRescanCounters Existing = new(2, 3, 4);

        [Fact]
        public void Outcome_EnumHasTheElevenRescanOutcomesInOrder()
        {
            Assert.Equal(
                new[]
                {
                    "Completed", "Incomplete", "NoMetadata", "ExtractionFailed", "MoveRecoveryBlocked",
                    "GuardRejected", "SelectionFailed", "FileMissing", "NotOnThisHost", "RowGone", "Cancelled"
                },
                Enum.GetNames<MetadataRescanOutcome>());
        }

        // Existing counters are (failure 2, unavailable 3, blocked 4); the due time is now + the ladder delay of the incremented counter.
        [Theory]
        [InlineData(MetadataRescanOutcome.Completed, 0, 0, 0, 0)]
        [InlineData(MetadataRescanOutcome.Incomplete, 3, 0, 0, 120)]
        [InlineData(MetadataRescanOutcome.NoMetadata, 3, 0, 0, 120)]
        [InlineData(MetadataRescanOutcome.ExtractionFailed, 3, 0, 0, 120)]
        [InlineData(MetadataRescanOutcome.MoveRecoveryBlocked, 2, 0, 5, 120)]
        [InlineData(MetadataRescanOutcome.GuardRejected, 2, 0, 5, 120)]
        [InlineData(MetadataRescanOutcome.SelectionFailed, 2, 0, 5, 120)]
        [InlineData(MetadataRescanOutcome.FileMissing, 2, 4, 0, 240)]
        [InlineData(MetadataRescanOutcome.NotOnThisHost, 2, 4, 0, 240)]
        public void Apply_OutcomeChangesOnlyItsOwnCounterAndSchedulesFromItsLadder(
            MetadataRescanOutcome outcome, int failure, int unavailable, int blocked, int delayMinutes)
        {
            var change = MetadataRescanSchedulePolicy.Apply(outcome, Existing, Now);

            Assert.NotNull(change);
            Assert.Equal(new MetadataRescanCounters(failure, unavailable, blocked), change.Value.Counters);
            if (outcome == MetadataRescanOutcome.Completed)
            {
                Assert.Null(change.Value.DueUtc);
            }
            else
            {
                Assert.Equal(Now.AddMinutes(delayMinutes), change.Value.DueUtc);
            }
        }

        [Theory]
        [InlineData(MetadataRescanOutcome.RowGone)]
        [InlineData(MetadataRescanOutcome.Cancelled)]
        public void Apply_RowGoneAndCancelledWriteNothing(MetadataRescanOutcome outcome)
        {
            Assert.Null(MetadataRescanSchedulePolicy.Apply(outcome, Existing, Now));
        }

        [Fact]
        public void Apply_EveryOutcomeIsCoveredByTheTables()
        {
            foreach (var outcome in Enum.GetValues<MetadataRescanOutcome>())
            {
                var exception = Record.Exception(() => MetadataRescanSchedulePolicy.Apply(outcome, Existing, Now));
                Assert.Null(exception);
            }
        }

        [Theory]
        [InlineData(1, 30)]
        [InlineData(2, 60)]
        [InlineData(3, 120)]
        [InlineData(4, 240)]
        [InlineData(5, 480)]
        [InlineData(6, 960)]
        [InlineData(7, 1440)]
        [InlineData(8, 1440)]
        [InlineData(int.MaxValue, 1440)]
        public void FailureDelay_DoublesFromThirtyMinutesAndCapsAtOneDay(int count, int minutes)
        {
            Assert.Equal(TimeSpan.FromMinutes(minutes), MetadataRescanSchedulePolicy.FailureDelay(count));
        }

        [Theory]
        [InlineData(1, 30)]
        [InlineData(2, 60)]
        [InlineData(3, 120)]
        [InlineData(4, 240)]
        [InlineData(5, 360)]
        [InlineData(int.MaxValue, 360)]
        public void UnavailableDelay_DoublesFromThirtyMinutesAndCapsAtSixHours(int count, int minutes)
        {
            Assert.Equal(TimeSpan.FromMinutes(minutes), MetadataRescanSchedulePolicy.UnavailableDelay(count));
        }

        [Theory]
        [InlineData(1, 10)]
        [InlineData(2, 20)]
        [InlineData(3, 40)]
        [InlineData(4, 80)]
        [InlineData(5, 120)]
        [InlineData(int.MaxValue, 120)]
        public void BlockedDelay_DoublesFromTenMinutesAndCapsAtTwoHours(int count, int minutes)
        {
            Assert.Equal(TimeSpan.FromMinutes(minutes), MetadataRescanSchedulePolicy.BlockedDelay(count));
        }

        [Fact]
        public void Apply_CountersAtTheirCapsKeepTheCappedDelay()
        {
            var atCaps = new MetadataRescanCounters(int.MaxValue - 1, int.MaxValue - 1, int.MaxValue - 1);

            Assert.Equal(Now.AddDays(1), MetadataRescanSchedulePolicy.Apply(MetadataRescanOutcome.NoMetadata, atCaps, Now)!.Value.DueUtc);
            Assert.Equal(Now.AddHours(6), MetadataRescanSchedulePolicy.Apply(MetadataRescanOutcome.FileMissing, atCaps, Now)!.Value.DueUtc);
            Assert.Equal(Now.AddHours(2), MetadataRescanSchedulePolicy.Apply(MetadataRescanOutcome.GuardRejected, atCaps, Now)!.Value.DueUtc);
        }

        [Fact]
        public void Bounds_HaveTheStatedValues()
        {
            Assert.Equal(20, MetadataRescanSchedulePolicy.BatchSize);
            Assert.Equal(128, MetadataRescanSchedulePolicy.PageSize);
            Assert.Equal(8, MetadataRescanSchedulePolicy.MaxPages);
            Assert.Equal(100, MetadataRescanSchedulePolicy.MaxProbes);
            Assert.Equal(256, MetadataRescanSchedulePolicy.MaxForeignRows);
        }
    }
}
