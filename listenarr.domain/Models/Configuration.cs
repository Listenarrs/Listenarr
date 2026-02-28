using System.Text.Json;
using System.ComponentModel.DataAnnotations.Schema;

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
                : JsonSerializer.Deserialize<Dictionary<string, object>>(SettingsJson) ?? new Dictionary<string, object>();
            set => SettingsJson = JsonSerializer.Serialize(value);
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

    public class ApplicationSettings
    {
        public int Id { get; set; } = 1; // Singleton pattern - only one settings record
        public string OutputPath { get; set; } = string.Empty;
        // Folder naming pattern (base directory structure)
        // Available variables:
        // {Author} - Audiobook author/narrator
        // {Series} - Series name (if applicable)
        // {SeriesNumber} - Position in series (e.g., "1", "2")
        // {Title} - Book/audiobook title
        // {Year} - Publication year
        public string FolderNamingPattern { get; set; } = "{Author}/{Series}/{Title}";

        // File naming pattern for SINGLE-FILE imports (one audio file per audiobook)
        // Available variables:
        // {Author} - Audiobook author/narrator
        // {Series} - Series name (if applicable)
        // {SeriesNumber} - Position in series (e.g., "1", "2")
        // {Title} - Book/audiobook title
        // {Year} - Publication year
        // {Quality} - Audio quality (e.g., "64kbps mp3")
        public string FileNamingPattern { get; set; } = "{Title}";

        // File naming pattern for MULTI-FILE imports (multiple audio files per audiobook)
        // Use {DiskNumber} or {DiskNumber:00}, {ChapterNumber} or {ChapterNumber:00} to differentiate files
        // Available variables:
        // {Author} - Audiobook author/narrator
        // {Series} - Series name (if applicable)
        // {SeriesNumber} - Position in series (e.g., "1", "2")
        // {Title} - Book/audiobook title
        // {DiskNumber} or {DiskNumber:00} - Disk/part number (00 = zero-padded)
        // {ChapterNumber} or {ChapterNumber:00} - Chapter number (00 = zero-padded)
        // {Year} - Publication year
        // {Quality} - Audio quality (e.g., "64kbps mp3")
        public string MultiFileNamingPattern { get; set; } = "{Title}-{DiskNumber:00}";

        public bool EnableMetadataProcessing { get; set; } = true;
        public bool EnableCoverArtDownload { get; set; } = true;
        public string AudnexusApiUrl { get; set; } = "https://api.audnex.us";
        public int MaxConcurrentDownloads { get; set; } = 3;
        public int PollingIntervalSeconds { get; set; } = 30;
        public bool EnableNotifications { get; set; } = false;
        public List<string> AllowedFileExtensions { get; set; } = new() { ".mp3", ".flac", ".m4a", ".m4b", ".ogg" };

        // Number of seconds a download must be observed in the client as "complete" before
        // the system will finalize it (stability window). Keeping a short default (10s)
        // avoids accidental long delays while still allowing this to be tuned by admins.
        public int DownloadCompletionStabilitySeconds { get; set; } = 10;

        // Retry/backoff settings for when a finalized download has no discoverable source file
        // at the time of finalization. These control how the monitor schedules retries when
        // files are still being extracted/moved by the client.
        public int MissingSourceRetryInitialDelaySeconds { get; set; } = 30;
        public int MissingSourceMaxRetries { get; set; } = 3;

        // Action to take when a download completes: "Move" or "Copy"
        public string CompletedFileAction { get; set; } = "Move";

        // Whether to extract archive files (zip/rar/7z) when discovered in a completed download
        public bool ExtractArchives { get; set; } = true;

        // Whether to show completed downloads from external clients in the Activity view
        public bool ShowCompletedExternalDownloads { get; set; } = false;

        // Failed download handling settings
        public bool FailedDownloadHandlingEnabled { get; set; } = true;
        public bool FailedDownloadAutoSearch { get; set; } = false;

        /// <summary>
        /// Webhook URL for sending notifications (legacy single webhook).
        /// </summary>
        public string WebhookUrl { get; set; } = string.Empty;

        /// <summary>
        /// List of enabled notification triggers (legacy).
        /// </summary>
        public List<string> EnabledNotificationTriggers { get; set; } = new() { "book-added", "book-downloading", "book-available", "book-completed" };

        /// <summary>
        /// Multiple webhooks configuration (new format).
        /// </summary>
        public List<WebhookConfiguration>? Webhooks { get; set; }

        // Optional admin credentials submitted from the UI when saving settings.
        // These are NOT mapped to the ApplicationSettings table; they are used to create/update
        // a User record in the Users table via the ConfigurationService.
        /// <summary>
        /// Admin username submitted from the UI (not persisted to the settings table).
        /// </summary>
        [NotMapped]
        public string? AdminUsername { get; set; }

        [NotMapped]
        public string? AdminPassword { get; set; }

        // Discord bot integration settings (used by external Discord bot or interactions)
        /// <summary>
        /// Enable (persisted) Discord bot integration settings. The bot process may read these settings to
        /// automatically login / register commands.
        /// </summary>
        public bool DiscordBotEnabled { get; set; } = false;

        /// <summary>
        /// Discord Application (Client) ID for registering application commands.
        /// </summary>
        public string? DiscordApplicationId { get; set; }

        /// <summary>
        /// Optional Guild ID to register commands in a single guild for faster deployment during testing.
        /// </summary>
        public string? DiscordGuildId { get; set; }

        /// <summary>
        /// Optional Channel ID to restrict bot interactions to a single channel. If set, the bot
        /// will ignore interactions from other channels unless the bot configuration allows it.
        /// </summary>
        public string? DiscordChannelId { get; set; }

        /// <summary>
        /// Bot token used by an external bot process to authenticate to Discord.
        /// NOTE: Storing tokens in the database has security implications. Consider using a secrets manager
        /// for production deployments.
        /// </summary>
        public string? DiscordBotToken { get; set; }

        /// <summary>
        /// Primary command group name (e.g. "request"). We'll create a slash command with this group and
        /// a subcommand for specific request types (e.g. "audiobook").
        /// </summary>
        public string? DiscordCommandGroupName { get; set; } = "request";

        /// <summary>
        /// Subcommand name for audiobooks (e.g. "audiobook"). Combined with the group this yields "/request audiobook".
        /// </summary>
        public string? DiscordCommandSubcommandName { get; set; } = "audiobook";

        /// <summary>
        /// Optional custom username for the Discord bot. If set, the bot will attempt to change its username.
        /// </summary>
        public string? DiscordBotUsername { get; set; }

        /// <summary>
        /// Optional avatar URL for the Discord bot. If set, the bot will attempt to change its avatar.
        /// </summary>
        public string? DiscordBotAvatar { get; set; }

        // Search settings
        /// <summary>
        /// Enable searching Amazon as part of intelligent searches.
        /// </summary>
        public bool EnableAmazonSearch { get; set; } = true;

        /// <summary>
        /// Enable searching Audible as part of intelligent searches.
        /// </summary>
        public bool EnableAudibleSearch { get; set; } = true;

        /// <summary>
        /// Enable using OpenLibrary augmentation during intelligent searches.
        /// </summary>
        public bool EnableOpenLibrarySearch { get; set; } = true;

        
    }
}
