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

namespace Listenarr.Infrastructure.DownloadClients.Transmission
{
    /// <summary>
    /// Evaluates Transmission's per-torrent and inherited seed limit settings.
    /// </summary>
    public static class TransmissionSeedLimitEvaluator
    {
        /// <summary>
        /// Mirrors Sonarr's Transmission seed-limit behavior.
        /// </summary>
        public static bool HasReachedSeedLimit(
            bool isStopped,
            bool isSeeding,
            double ratio,
            int seedRatioMode,
            double seedRatioLimit,
            int seedIdleMode,
            int seedIdleLimit,
            long secondsSeeding,
            bool sessionSeedRatioLimited,
            double sessionSeedRatioLimit,
            bool sessionIdleSeedingLimitEnabled,
            int sessionIdleSeedingLimit)
        {
            var hasEffectiveRatioLimit =
                (seedRatioMode == 1 && seedRatioLimit > 0) ||
                (seedRatioMode == 0 && sessionSeedRatioLimited && sessionSeedRatioLimit > 0);
            var hasEffectiveIdleLimit =
                (seedIdleMode == 1 && seedIdleLimit > 0) ||
                (seedIdleMode == 0 && sessionIdleSeedingLimitEnabled && sessionIdleSeedingLimit > 0);

            if (!hasEffectiveRatioLimit && !hasEffectiveIdleLimit)
            {
                return true;
            }

            if (seedRatioMode == 1 && isStopped && ratio >= seedRatioLimit)
            {
                return true;
            }

            if (seedRatioMode == 0 && isStopped && sessionSeedRatioLimited && ratio >= sessionSeedRatioLimit)
            {
                return true;
            }

            if (seedIdleMode == 1 && (isStopped || isSeeding) && secondsSeeding > seedIdleLimit * 60)
            {
                return true;
            }

            if (seedIdleMode == 0 && isStopped && sessionIdleSeedingLimitEnabled)
            {
                return true;
            }

            return false;
        }
    }
}
