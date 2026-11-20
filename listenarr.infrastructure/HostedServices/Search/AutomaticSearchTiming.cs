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

namespace Listenarr.Infrastructure.HostedServices.Search
{
    /// <summary>
    /// The automatic-search timing knobs resolved from <see cref="ApplicationSettings"/> with
    /// their clamps applied. The three are independent: the sweep interval is how often the
    /// worker looks for work, the cooldown is how long a book stays ineligible after a search,
    /// and the book delay is the pause between searches inside one sweep. A short sweep with a
    /// long cooldown picks books up promptly once they become eligible without searching any
    /// of them more than once per cooldown.
    /// </summary>
    public sealed record AutomaticSearchTiming(
        TimeSpan SweepInterval,
        TimeSpan BookCooldown,
        TimeSpan BookDelay)
    {
        public const int MinSweepIntervalHours = 1;
        public const int MaxSweepIntervalHours = 168;
        public const int MinCooldownHours = 1;
        public const int MaxCooldownHours = 720;
        public const int MinBookDelaySeconds = 0;
        public const int MaxBookDelaySeconds = 300;

        /// <summary>
        /// The timing a fresh install gets: hourly sweep, 24-hour cooldown, 5 seconds between books.
        /// </summary>
        public static AutomaticSearchTiming Default { get; } = FromSettings(new ApplicationSettings());

        public static AutomaticSearchTiming FromSettings(ApplicationSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            return new AutomaticSearchTiming(
                TimeSpan.FromHours(Math.Clamp(
                    settings.AutomaticSearchIntervalHours,
                    MinSweepIntervalHours,
                    MaxSweepIntervalHours)),
                TimeSpan.FromHours(Math.Clamp(
                    settings.AutomaticSearchCooldownHours,
                    MinCooldownHours,
                    MaxCooldownHours)),
                TimeSpan.FromSeconds(Math.Clamp(
                    settings.AutomaticSearchBookDelaySeconds,
                    MinBookDelaySeconds,
                    MaxBookDelaySeconds)));
        }

        /// <summary>
        /// A book whose last search is older than this instant is eligible for the sweep.
        /// </summary>
        public DateTime EligibilityCutoff(DateTime nowUtc) => nowUtc - BookCooldown;
    }
}
