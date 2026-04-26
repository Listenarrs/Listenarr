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
using System.Text.Json;

namespace Listenarr.Domain.Models
{
    public class ApiConfiguration
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string BaseUrl { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty; // "torrent" or "nzb"
        public bool IsEnabled { get; set; } = true;
        public int Priority { get; set; } = 1;

        // Store as JSON string in database
        public string HeadersJson { get; set; } = "{}";
        public string ParametersJson { get; set; } = "{}";

        public string? RateLimitPerMinute { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? LastUsed { get; set; }

        // Not mapped - for JSON serialization in API responses
        public Dictionary<string, string> Headers
        {
            get => string.IsNullOrWhiteSpace(HeadersJson)
                ? new Dictionary<string, string>()
                : JsonSerializer.Deserialize<Dictionary<string, string>>(HeadersJson) ?? new Dictionary<string, string>();
            set => HeadersJson = JsonSerializer.Serialize(value);
        }

        public Dictionary<string, string> Parameters
        {
            get => string.IsNullOrWhiteSpace(ParametersJson)
                ? new Dictionary<string, string>()
                : JsonSerializer.Deserialize<Dictionary<string, string>>(ParametersJson) ?? new Dictionary<string, string>();
            set => ParametersJson = JsonSerializer.Serialize(value);
        }
    }

    public class WebhookConfiguration
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string Type { get; set; } = "Zapier"; // Pushbullet, Telegram, Slack, Discord, Pushover, NTFY, Zapier
        public List<string> Triggers { get; set; } = new();
        public bool IsEnabled { get; set; } = true;
    }
}
