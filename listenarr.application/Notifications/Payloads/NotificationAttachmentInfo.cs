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
namespace Listenarr.Application.Notifications.Payloads
{
    /// <summary>
    /// Public DTO used by DI-friendly payload builders to describe an attachment prepared for notifications.
    /// Kept minimal and immutable for easy testing.
    /// </summary>
    public sealed class NotificationAttachmentInfo
    {
        public required byte[] ImageData { get; init; }
        public required string Filename { get; init; }
        public required string ContentType { get; init; }
    }
}
