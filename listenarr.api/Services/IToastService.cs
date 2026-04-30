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
namespace Listenarr.Api.Services
{
    public interface IToastService
    {
        Task PublishToastAsync(string level, string title, string message, int? timeoutMs = null);

        /// <summary>
        /// Publish a notification to the activity dropdown without triggering a popup toast message.
        /// This is useful for server-driven events where clients already display context-specific toasts
        /// (eg. when broadcasting an IndexersUpdated event the client will show a toast, so the server
        /// should only create a notification to populate the activity bell).
        /// </summary>
        Task PublishNotificationAsync(string title, string message, string? icon = null, int? timeoutMs = null);
    }
}
