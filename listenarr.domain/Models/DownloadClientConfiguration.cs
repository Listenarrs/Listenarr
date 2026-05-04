using System.Text.Json;

namespace Listenarr.Domain.Models
{
    public class DownloadClientConfiguration
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty; // "qbittorrent", "transmission", "sabnzbd", "nzbget"
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string DownloadPath { get; set; } = string.Empty;
        public bool UseSSL { get; set; } = false;
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// Cleanup behavior after successful import: "none", "remove", "remove_and_delete"
        /// </summary>
        public string RemoveCompletedDownloads { get; set; } = "none";

        // Store as JSON string in database
        public string SettingsJson { get; set; } = "{}";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Not mapped - for JSON serialization in API responses
        public Dictionary<string, object> Settings
        {
            get => string.IsNullOrWhiteSpace(SettingsJson)
                ? new Dictionary<string, object>()
                : JsonSerializer.Deserialize<Dictionary<string, object>>(SettingsJson) ?? [];
            set => SettingsJson = JsonSerializer.Serialize(value);
        }

        public int GetPollingInterval(int defaultInterval = 30)
        {
            if (Settings != null)
            {
                bool hasSetting = Settings.TryGetValue("PollingIntervalSeconds", out var interval);
                if (hasSetting && int.TryParse(interval?.ToString() ?? string.Empty, out var custom) && custom >= 15)
                    return custom;
            }

            return defaultInterval;
        }
    }
}
