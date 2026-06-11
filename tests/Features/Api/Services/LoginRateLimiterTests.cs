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
using Listenarr.Infrastructure.Security;

namespace Listenarr.Tests.Features.Api.Services
{
    public class LoginRateLimiterTests
    {
        [Fact]
        public void RecordsFailuresAndBlocks()
        {
            var limiter = new LoginRateLimiter();
            var key = "1.2.3.4:alice";

            // Default configured max is 5; record 5 failures
            for (int i = 0; i < 5; i++) limiter.RecordFailure(key);

            Assert.True(limiter.IsBlocked(key));
            var secs = limiter.GetSecondsUntilUnblock(key);
            Assert.True(secs > 0, "Expected remaining block seconds to be > 0");

            // Record success should clear the block
            limiter.RecordSuccess(key);
            Assert.False(limiter.IsBlocked(key));
            Assert.Equal(0, limiter.GetSecondsUntilUnblock(key));
        }
    }
}
