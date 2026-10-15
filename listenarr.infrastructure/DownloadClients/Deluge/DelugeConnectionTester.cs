/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Deluge
{
    internal sealed class DelugeConnectionTester(
        DelugeRpcClient rpcClient,
        ILogger logger)
    {
        public async Task<(bool Success, string Message)> TestConnectionAsync(
            DownloadClientConfiguration client,
            CancellationToken ct = default)
        {
            try
            {
                var connected = await rpcClient.InvokeAsync(client, "web.connected", [], ct);
                if (connected.ValueKind == JsonValueKind.True)
                {
                    return (true, "Deluge: connected to Web UI and daemon");
                }
                return (false, "Deluge: unexpected JSON-RPC response");
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogDebug(ex, "Deluge authentication failed for client {ClientId}", LogRedaction.SanitizeText(client?.Id ?? client?.Name ?? client?.Type));
                return (false, "Deluge: authentication failed (check Web UI password)");
            }
            catch (TaskCanceledException)
            {
                return (false, "Deluge: connection timed out");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogDebug(ex, "Deluge test failed for client {ClientId}", LogRedaction.SanitizeText(client?.Id ?? client?.Name ?? client?.Type));
                return (false, $"Deluge: connection failed ({ex.Message})");
            }
        }
    }
}
