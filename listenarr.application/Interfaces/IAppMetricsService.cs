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
namespace Listenarr.Application.Interfaces
{
    /// <summary>
    /// Minimal metrics API for Listenarr to allow unit-tests to assert telemetry points.
    /// Implementations should be lightweight and thread-safe.
    /// </summary>
    public interface IAppMetricsService
    {
        void Increment(string metricName, double value = 1);
        void Gauge(string metricName, double value);
        void Timing(string metricName, TimeSpan duration);
    }
}
