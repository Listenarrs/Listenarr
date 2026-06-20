
namespace Listenarr.Application.Notifications.Contracts
{
    /// <summary>
    /// Sends notifications via configured channels (webhooks, email, etc.)
    /// </summary>
    public interface INotificationService
    {
        /// <summary>
        /// Sends a notification when a download completes successfully
        /// </summary>
        /// <param name="download">The completed download</param>
        Task OnDownloadImportedAsync(Download download);

        /// <summary>
        /// Sends a notification when a download fails
        /// </summary>
        /// <param name="download">The failed download</param>
        Task OnDownloadFailedAsync(Download download);

        /// <summary>
        /// Sends a general system notification
        /// </summary>
        /// <param name="title">Notification title</param>
        /// <param name="message">Notification message</param>
        Task SendSystemNotificationAsync(string title, string message);

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
