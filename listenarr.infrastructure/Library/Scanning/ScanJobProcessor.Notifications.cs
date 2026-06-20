/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Scanning;

public partial class ScanJobProcessor
{
    private async Task NotifyAvailableAsync(Audiobook audiobook, int createdFiles)
    {
        if (!audiobook.Monitored || createdFiles <= 0)
        {
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var notificationService = scope.ServiceProvider.GetService<NotificationService>();
            var configurationService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
            var settings = await configurationService.GetApplicationSettingsAsync();
            if (notificationService == null)
            {
                return;
            }

            await notificationService.SendNotificationAsync(
                "book-available",
                new
                {
                    id = audiobook.Id,
                    title = audiobook.Title ?? "Unknown Title",
                    authors = audiobook.Authors,
                    asin = audiobook.Asin,
                    imageUrl = audiobook.ImageUrl,
                    description = audiobook.Description,
                    monitored = audiobook.Monitored,
                    qualityProfileId = audiobook.QualityProfileId,
                    filesImported = createdFiles,
                    totalFiles = 0
                },
                settings.WebhookUrl,
                settings.EnabledNotificationTriggers);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            _logger.LogWarning(
                exception,
                "Failed to send book-available notification for audiobook {AudiobookId} in background scan",
                audiobook.Id);
        }
    }
}
