using Microsoft.AspNetCore.SignalR;
using Listenarr.Api.Hubs;

namespace Listenarr.Api.Services
{
    public class ToastService : IToastService
    {
        private readonly IHubContext<DownloadHub> _hub;
        private readonly ILogger<ToastService> _logger;

        public ToastService(IHubContext<DownloadHub> hub, ILogger<ToastService> logger)
        {
            _hub = hub;
            _logger = logger;
        }

        public async Task PublishToastAsync(string level, string title, string message, int? timeoutMs = null)
        {
            try
            {
                var payload = new
                {
                    level = level ?? "info",
                    title = title ?? string.Empty,
                    message = message ?? string.Empty,
                    timeoutMs = timeoutMs
                };

                // Send toast message (appears as popup)
                await _hub.Clients.All.SendAsync("ToastMessage", payload);
                
                // Also send as notification (appears in dropdown/bell icon)
                var notification = new
                {
                    id = Guid.NewGuid().ToString(),
                    title = title ?? string.Empty,
                    message = message ?? string.Empty,
                    icon = GetIconForLevel(level ?? "info"),
                    timestamp = DateTime.UtcNow.ToString("o"),
                    dismissed = false
                };
                await _hub.Clients.All.SendAsync("Notification", notification);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogWarning(ex, "Failed to publish toast message: {Title}", title);
            }
        }

        public async Task PublishNotificationAsync(string title, string message, string? icon = null, int? timeoutMs = null)
        {
            try
            {
                var notification = new
                {
                    id = Guid.NewGuid().ToString(),
                    title = title ?? string.Empty,
                    message = message ?? string.Empty,
                    icon = icon ?? GetIconForLevel("info"),
                    timestamp = DateTime.UtcNow.ToString("o"),
                    dismissed = false
                };

                await _hub.Clients.All.SendAsync("Notification", notification);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogWarning(ex, "Failed to publish notification: {Title}", title);
            }
        }

        private static string GetIconForLevel(string level)
        {
            return level?.ToLower() switch
            {
                "success" => "M173.66,98.34a8,8,0,0,1,0,11.32l-56,56a8,8,0,0,1-11.32,0l-24-24a8,8,0,0,1,11.32-11.32L112,148.69l50.34-50.35A8,8,0,0,1,173.66,98.34ZM232,128A104,104,0,1,1,128,24,104.11,104.11,0,0,1,232,128Zm-16,0a88,88,0,1,0-88,88A88.1,88.1,0,0,0,216,128Z",
                "error" => "XCircle",
                "warning" => "Warning",
                _ => "Info"
            };
        }
    }
}

