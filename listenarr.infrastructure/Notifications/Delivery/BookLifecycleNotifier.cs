/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Notifications.Delivery
{
    /// <inheritdoc />
    public sealed class BookLifecycleNotifier : IBookLifecycleNotifier
    {
        private readonly INotificationService _notificationService;
        private readonly IConfigurationService _configurationService;
        private readonly ILogger<BookLifecycleNotifier> _logger;

        public BookLifecycleNotifier(
            INotificationService notificationService,
            IConfigurationService configurationService,
            ILogger<BookLifecycleNotifier> logger)
        {
            _notificationService = notificationService;
            _configurationService = configurationService;
            _logger = logger;
        }

        public async Task NotifyAsync(string trigger, object payload, CancellationToken cancellationToken = default)
        {
            try
            {
                var settings = await _configurationService.GetApplicationSettingsAsync();
                await _notificationService.SendNotificationAsync(
                    trigger,
                    payload,
                    settings.WebhookUrl,
                    settings.EnabledNotificationTriggers);
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                && ex is not OutOfMemoryException
                && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to send '{Trigger}' lifecycle notification", trigger);
            }
        }
    }
}
