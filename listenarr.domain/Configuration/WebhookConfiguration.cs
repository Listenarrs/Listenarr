namespace Listenarr.Domain.Configuration
{
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
