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
namespace Listenarr.Application.Integrations.Audiobookshelf.Contracts
{
    /// <summary>
    /// Debounced post-import Audiobookshelf scan requests. Signalling is cheap and
    /// never throws; whether a scan is actually sent is decided by the scheduler
    /// (integration configured, notify-on-import enabled) when the debounce window closes.
    /// </summary>
    public interface IAudiobookshelfScanScheduler
    {
        void RequestScan(string reason);
    }
}
