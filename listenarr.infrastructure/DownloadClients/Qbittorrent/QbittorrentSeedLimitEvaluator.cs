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

namespace Listenarr.Infrastructure.DownloadClients.Qbittorrent
{
    /// <summary>
    /// Evaluates qBittorrent's per-torrent and inherited seed limit settings.
    /// </summary>
    public static class QbittorrentSeedLimitEvaluator
    {
        /// <summary>
        /// Mirrors Sonarr's qBittorrent seed-limit behavior.
        /// </summary>
        public static bool HasReachedSeedLimit(
            double ratio,
            float ratioLimit,
            long? seedingTime,
            long seedingTimeLimit,
            bool globalMaxRatioEnabled,
            float globalMaxRatio,
            bool globalMaxSeedingTimeEnabled,
            long globalMaxSeedingTime)
        {
            var hasEffectiveRatioLimit =
                ratioLimit >= 0 ||
                (ratioLimit <= -2 && globalMaxRatioEnabled && globalMaxRatio > 0);
            var hasEffectiveSeedingTimeLimit =
                seedingTimeLimit >= 0 ||
                (seedingTimeLimit <= -2 && globalMaxSeedingTimeEnabled && globalMaxSeedingTime > 0);

            if (!hasEffectiveRatioLimit && !hasEffectiveSeedingTimeLimit)
            {
                return true;
            }

            if (ratioLimit >= 0 && ratioLimit - ratio <= 0.001)
            {
                return true;
            }

            if (ratioLimit <= -2 && globalMaxRatioEnabled && globalMaxRatio - ratio <= 0.001)
            {
                return true;
            }

            if (seedingTimeLimit >= 0 &&
                seedingTime is long currentSeedingTime &&
                currentSeedingTime >= seedingTimeLimit * 60)
            {
                return true;
            }

            if (seedingTimeLimit <= -2 &&
                globalMaxSeedingTimeEnabled &&
                seedingTime is long inheritedSeedingTime &&
                inheritedSeedingTime >= globalMaxSeedingTime * 60)
            {
                return true;
            }

            return false;
        }
    }
}
