
namespace Listenarr.Application.Notifications.Contracts
{
    /// <summary>
    /// Sends notifications via configured channels (webhooks, email, etc.)
    /// </summary>
    public interface INotificationService
    {
        /// <summary>
        /// Sends a notification to a specific webhook
        /// </summary>
        /// <param name="trigger">The event trigger name</param>
        /// <param name="data">The notification data payload</param>
        /// <param name="webhookUrl">The webhook URL to send to</param>
        /// <param name="enabledTriggers">List of enabled triggers for this webhook</param>
        Task SendNotificationAsync(string trigger, object data, string webhookUrl, List<string> enabledTriggers);
    }
}
