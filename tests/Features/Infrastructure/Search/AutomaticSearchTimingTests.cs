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
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Search;

[Trait("Name", "AutomaticSearchTimingTests")]
[Trait("Category", "Infrastructure")]
public class AutomaticSearchTimingTests : BaseTests
{
    [Fact]
    public void Defaults_SweepHourly_CooldownDaily_FiveSecondsBetweenBooks()
    {
        var timing = AutomaticSearchTiming.FromSettings(new ApplicationSettings());

        Assert.Equal(TimeSpan.FromHours(1), timing.SweepInterval);
        Assert.Equal(TimeSpan.FromHours(24), timing.BookCooldown);
        Assert.Equal(TimeSpan.FromSeconds(5), timing.BookDelay);
        Assert.Equal(timing, AutomaticSearchTiming.Default);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(6, 6)]
    [InlineData(168, 168)]
    [InlineData(5000, 168)]
    public void SweepInterval_IsClampedToOneHourThroughOneWeek(int configuredHours, int expectedHours)
    {
        var timing = AutomaticSearchTiming.FromSettings(
            new ApplicationSettings { AutomaticSearchIntervalHours = configuredHours });

        Assert.Equal(TimeSpan.FromHours(expectedHours), timing.SweepInterval);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(48, 48)]
    [InlineData(720, 720)]
    [InlineData(99999, 720)]
    public void Cooldown_IsClampedToOneHourThroughThirtyDays(int configuredHours, int expectedHours)
    {
        var timing = AutomaticSearchTiming.FromSettings(
            new ApplicationSettings { AutomaticSearchCooldownHours = configuredHours });

        Assert.Equal(TimeSpan.FromHours(expectedHours), timing.BookCooldown);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(30, 30)]
    [InlineData(301, 300)]
    public void BookDelay_IsClampedToZeroThroughFiveMinutes(int configuredSeconds, int expectedSeconds)
    {
        var timing = AutomaticSearchTiming.FromSettings(
            new ApplicationSettings { AutomaticSearchBookDelaySeconds = configuredSeconds });

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), timing.BookDelay);
    }

    [Fact]
    public void EligibilityCutoff_FollowsTheCooldown_NotTheSweepInterval()
    {
        var now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        var timing = AutomaticSearchTiming.FromSettings(new ApplicationSettings
        {
            AutomaticSearchIntervalHours = 1,
            AutomaticSearchCooldownHours = 24
        });

        var cutoff = timing.EligibilityCutoff(now);

        Assert.Equal(now.AddHours(-24), cutoff);
        Assert.Equal(DateTimeKind.Utc, cutoff.Kind);
    }

    [Fact]
    public void FromSettings_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => AutomaticSearchTiming.FromSettings(null!));
    }
}
