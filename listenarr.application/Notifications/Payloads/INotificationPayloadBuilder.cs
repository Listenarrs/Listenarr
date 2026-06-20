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
using System.Text.Json.Nodes;

namespace Listenarr.Application.Notifications.Payloads
{
    /// <summary>
    /// Abstraction for building notification payloads and preparing attachments.
    /// Implementations should contain only logic for payload construction and attachment retrieval.
    /// </summary>
    public interface INotificationPayloadBuilder
    {
        JsonNode CreateDiscordPayload(string trigger, object data, string? startupBaseUrl, string? apiVersion = null);

        Task<(JsonObject payload, NotificationAttachmentInfo? attachment)> CreateDiscordPayloadWithAttachmentAsync(
            string trigger,
            object data,
            string? startupBaseUrl,
            HttpClient httpClient,
            IRequestContextAccessor? requestContextAccessor = null,
            Action<string>? logInfo = null,
            Action<Exception, string>? logDebug = null,
            string? apiVersion = null);
    }
}
