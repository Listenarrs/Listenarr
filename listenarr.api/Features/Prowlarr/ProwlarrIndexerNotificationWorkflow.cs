/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */


namespace Listenarr.Api.Features.Prowlarr
{
    public sealed class ProwlarrIndexerNotificationWorkflow
    {
        private readonly IHubBroadcaster _hubBroadcaster;
        private readonly IToastService _toastService;
        private readonly ILogger<ProwlarrIndexerNotificationWorkflow> _logger;

        public ProwlarrIndexerNotificationWorkflow(
            IHubBroadcaster hubBroadcaster,
            IToastService toastService,
            ILogger<ProwlarrIndexerNotificationWorkflow> logger)
        {
            _hubBroadcaster = hubBroadcaster;
            _toastService = toastService;
            _logger = logger;
        }

        public async Task NotifyDeletedAsync(Indexer indexer)
        {
            try
            {
                var deleteMessage = $"Removed indexer: {indexer.Name}";
                await _hubBroadcaster.BroadcastAsync(
                    RealtimeHubTarget.Settings,
                    "IndexersUpdated",
                    new { created = 0, skipped = 0, indexers = new[] { new { id = indexer.Id, name = indexer.Name, baseUrl = indexer.Url } } });

                if (ProwlarrToastThrottler.ShouldSendForIndexer(indexer.Id) && ProwlarrToastThrottler.ShouldSendForMessage(deleteMessage))
                {
                    await _toastService.PublishNotificationAsync("Indexers", deleteMessage, icon: null, timeoutMs: 8000);
                }
                else
                {
                    _logger.LogDebug("Suppressing delete toast for indexer {Id} due to recent toast or duplicate message", indexer.Id);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to broadcast IndexersUpdated after delete");
            }
        }

        public async Task NotifyPutAsync(Indexer indexer, int createdForBroadcast)
        {
            try
            {
                await _hubBroadcaster.BroadcastAsync(
                    RealtimeHubTarget.Settings,
                    "IndexersUpdated",
                    new { created = createdForBroadcast, skipped = 0, indexers = new[] { new { id = indexer.Id, name = indexer.Name, baseUrl = indexer.Url } } });

                var toastMessage = createdForBroadcast == 1 ? $"Imported indexer from PUT: {indexer.Name}" : $"Updated indexer: {indexer.Name}";
                var publishToast = true;
                try
                {
                    if (createdForBroadcast == 0 && indexer.CreatedAt != default && (DateTime.UtcNow - indexer.CreatedAt).TotalSeconds < ProwlarrToastThrottler.NotificationSuppressionSeconds)
                    {
                        publishToast = false;
                        _logger.LogDebug("Suppressing update toast for indexer {Id} since it was created recently", indexer.Id);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogDebug(ex, "Failed to evaluate recent-create toast suppression for Prowlarr indexer {Id}", indexer.Id);
                }

                if (!publishToast)
                {
                    return;
                }

                bool sendByIndexer;
                bool sendByMessage;
                try
                {
                    sendByIndexer = ProwlarrToastThrottler.ShouldSendForIndexer(indexer.Id);
                }
                catch (Exception caughtEx) when (caughtEx is not OperationCanceledException && caughtEx is not OutOfMemoryException && caughtEx is not StackOverflowException)
                {
                    sendByIndexer = true;
                }

                try
                {
                    sendByMessage = ProwlarrToastThrottler.ShouldSendForMessage(toastMessage);
                }
                catch (Exception caughtEx) when (caughtEx is not OperationCanceledException && caughtEx is not OutOfMemoryException && caughtEx is not StackOverflowException)
                {
                    sendByMessage = true;
                }

                _logger.LogDebug("Toast suppression check for indexer {Id}: byIndexer={ByIndexer}, byMessage={ByMessage}", indexer.Id, sendByIndexer, sendByMessage);

                if (sendByIndexer && sendByMessage)
                {
                    await _toastService.PublishNotificationAsync("Indexers", toastMessage, icon: null, timeoutMs: 8000);
                }
                else
                {
                    _logger.LogDebug("Suppressing toast for indexer {Id} due to recent toast or duplicate message", indexer.Id);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to broadcast IndexersUpdated after update");
            }
        }

        public async Task NotifyImportedAsync(int created, int skipped, IReadOnlyCollection<Indexer> createdIndexers)
        {
            if (created <= 0)
            {
                return;
            }

            try
            {
                var createdInfo = createdIndexers.Select(i => new { id = i.Id, name = i.Name, baseUrl = i.Url }).ToArray();

                _logger.LogInformation("Broadcasting IndexersUpdated to clients: created={Created}, skipped={Skipped}, indexerCount={Count}", created, skipped, createdInfo.Length);

                await _hubBroadcaster.BroadcastAsync(RealtimeHubTarget.Settings, "IndexersUpdated", new { created, skipped, indexers = createdInfo });

                _logger.LogInformation("IndexersUpdated broadcast complete");

                try
                {
                    var names = createdIndexers.Select(i => i.Name).ToArray();
                    var message = names.Length > 0 ? $"Imported {created} indexer(s): {string.Join(", ", names)}" : $"Imported {created} indexer(s) successfully";
                    if (ProwlarrToastThrottler.ShouldSendForMessage(message))
                    {
                        await _toastService.PublishNotificationAsync("Indexers", message, icon: null, timeoutMs: 8000);
                    }
                    else
                    {
                        _logger.LogDebug("Suppressing batch import toast due to recent identical message");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to publish indexer import notification");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to broadcast IndexersUpdated to realtime clients");
            }
        }

        public async Task NotifyDebugIndexersAsync(int created, IEnumerable<object> indexers)
        {
            _logger.LogInformation("DEBUG: Broadcasting IndexersUpdated (manual test): created={Created}", created);
            await _hubBroadcaster.BroadcastAsync(RealtimeHubTarget.Settings, "IndexersUpdated", new { created, skipped = 0, indexers });
            _logger.LogInformation("DEBUG: IndexersUpdated broadcast sent");

            try
            {
                var names = indexers.Select(i => i.GetType().GetProperty("name")?.GetValue(i)?.ToString() ?? string.Empty).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
                var message = names.Length > 0 ? $"Imported {created} indexer(s): {string.Join(", ", names)}" : $"Imported {created} indexer(s) successfully";
                await _toastService.PublishNotificationAsync("Indexers", message, icon: null, timeoutMs: 8000);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to publish debug indexer notification");
            }
        }
    }
}
