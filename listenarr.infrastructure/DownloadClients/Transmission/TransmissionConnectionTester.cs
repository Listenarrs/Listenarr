/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Transmission
{
    internal sealed class TransmissionConnectionTester(
        TransmissionRpcClient rpcClient,
        ILogger<TransmissionAdapter> logger)
    {
        public async Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            try
            {
                var payload = new
                {
                    method = "session-get",
                    arguments = new { },
                    tag = 1
                };
                var response = await rpcClient.InvokeAsync(client, payload, ct);

                if (!response.TryGetProperty("result", out var resultProp) ||
                    !string.Equals(resultProp.GetString(), "success", StringComparison.OrdinalIgnoreCase))
                {
                    var hint = resultProp.ValueKind == JsonValueKind.String ? resultProp.GetString() : "unexpected response";
                    return (false, $"Transmission: RPC endpoint did not return a valid session response ({hint})");
                }

                return (true, "Transmission: connected");
            }
            catch (HttpRequestException httpEx) when (httpEx.StatusCode == HttpStatusCode.Unauthorized || httpEx.StatusCode == HttpStatusCode.Forbidden)
            {
                logger.LogDebug(httpEx, "Transmission authentication failed for client {ClientId}", LogRedaction.SanitizeText(client?.Id ?? client?.Name ?? client?.Type));
                return (false, "Transmission: authentication failed");
            }
            catch (HttpRequestException httpEx)
            {
                logger.LogDebug(httpEx, "Transmission test failed for client {ClientId}", LogRedaction.SanitizeText(client?.Id ?? client?.Name ?? client?.Type));
                return (false, $"Transmission: network error ({httpEx.StatusCode?.ToString() ?? "unavailable"})");
            }
            catch (TaskCanceledException tce)
            {
                logger.LogDebug(tce, "Transmission test timed out for client {ClientId}", LogRedaction.SanitizeText(client?.Id ?? client?.Name ?? client?.Type));
                return (false, "Transmission: connection timed out");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogDebug(ex, "Transmission test failed for client {ClientId}", LogRedaction.SanitizeText(client?.Id ?? client?.Name ?? client?.Type));
                return (false, "Transmission: connection failed");
            }
        }
    }
}
