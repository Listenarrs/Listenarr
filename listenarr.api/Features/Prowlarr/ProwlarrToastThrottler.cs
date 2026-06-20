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

namespace Listenarr.Api.Features.Prowlarr
{
    internal static class ProwlarrToastThrottler
    {
        public const int NotificationSuppressionSeconds = 5;

        internal static readonly ConcurrentDictionary<int, DateTime> LastToastTimes = new();
        internal static readonly ConcurrentDictionary<string, DateTime> LastToastMessages = new();

        public static bool ShouldSendForIndexer(int indexerId)
        {
            try
            {
                var now = DateTime.UtcNow;
                if (LastToastTimes.TryGetValue(indexerId, out var last) && (now - last).TotalSeconds < NotificationSuppressionSeconds)
                {
                    return false;
                }

                LastToastTimes[indexerId] = now;
                return true;
            }
            catch (Exception caughtEx) when (caughtEx is not OperationCanceledException && caughtEx is not OutOfMemoryException && caughtEx is not StackOverflowException)
            {
                return true;
            }
        }

        public static bool ShouldSendForMessage(string? message)
        {
            try
            {
                var now = DateTime.UtcNow;
                var key = message ?? string.Empty;
                if (LastToastMessages.TryGetValue(key, out var last) && (now - last).TotalSeconds < NotificationSuppressionSeconds)
                {
                    return false;
                }

                LastToastMessages[key] = now;
                return true;
            }
            catch (Exception caughtEx) when (caughtEx is not OperationCanceledException && caughtEx is not OutOfMemoryException && caughtEx is not StackOverflowException)
            {
                return true;
            }
        }
    }
}
