/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
namespace Listenarr.Application.Notifications.Contracts
{
    /// <summary>
    /// Fires a book-lifecycle notification for a given trigger (see
    /// <see cref="Domain.Notifications.NotificationTriggers"/>). Resolves the current notification
    /// settings itself and dispatches through <see cref="INotificationService"/>, so callers at
    /// lifecycle transition points only supply the trigger and payload. Best-effort: it never
    /// throws into the calling flow.
    /// </summary>
    public interface IBookLifecycleNotifier
    {
        Task NotifyAsync(string trigger, object payload, CancellationToken cancellationToken = default);
    }
}
