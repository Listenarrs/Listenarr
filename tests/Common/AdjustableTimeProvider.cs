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

namespace Listenarr.Tests.Common
{
    /// <summary>
    /// A clock the test moves by hand.
    ///
    /// Needed for anything that waits a configured interval. Sleeping for real would make the
    /// suite slow and flaky, and asserting only that a wait happened leaves the release path
    /// untested, which is the half that matters when the wait is a new one.
    /// </summary>
    public sealed class AdjustableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public AdjustableTimeProvider(DateTimeOffset? start = null)
        {
            _now = start ?? DateTimeOffset.UtcNow;
        }

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
