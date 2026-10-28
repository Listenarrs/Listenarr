/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */

using System.Net;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Nzbget;

internal sealed class NzbgetConnectionTester(
    NzbgetXmlRpcClient xmlRpcClient,
    ILogger logger)
{
    public async Task<(bool Success, string Message)> TestConnectionAsync(
        DownloadClientConfiguration client,
        CancellationToken ct)
    {
        if (client == null)
        {
            return (false, "NZBGet: Configuration not provided");
        }

        if (!string.IsNullOrWhiteSpace(client.Username) && string.IsNullOrWhiteSpace(client.Password))
        {
            return (false, "NZBGet: Password is required when a username is specified");
        }

        try
        {
            var versionResult = await xmlRpcClient.CallAsync(
                new NzbgetXmlRpcRequest
                {
                    Client = client,
                    MethodName = "version"
                },
                ct);
            var version = versionResult.Element("string")?.Value;

            if (string.IsNullOrWhiteSpace(version))
            {
                return (false, "NZBGet: Unable to retrieve version");
            }

            // NZBGet history is required for reliable import. Active queue rows
            // are only progress telemetry; completed history provides final state
            // and FinalDir/DestDir for import path resolution.
            var configResult = await xmlRpcClient.CallAsync(
                new NzbgetXmlRpcRequest
                {
                    Client = client,
                    MethodName = "config"
                },
                ct);
            var keepHistoryValidation = NzbgetConfigValidator.ValidateKeepHistory(configResult);
            if (!keepHistoryValidation.Success)
            {
                return keepHistoryValidation;
            }

            return (true, "NZBGet: connected");
        }
        catch (HttpRequestException httpEx) when (httpEx.StatusCode == HttpStatusCode.Unauthorized || httpEx.StatusCode == HttpStatusCode.Forbidden)
        {
            logger.LogDebug(httpEx, "NZBGet authentication failed for client {ClientId}", LogRedaction.SanitizeText(client.Id ?? client.Name ?? client.Type));
            return (false, "NZBGet: Authentication failed (check username/password)");
        }
        catch (HttpRequestException httpEx)
        {
            logger.LogDebug(httpEx, "NZBGet network error for client {ClientId}", LogRedaction.SanitizeText(client.Id ?? client.Name ?? client.Type));
            return (false, $"NZBGet: network error ({httpEx.StatusCode?.ToString() ?? "unavailable"})");
        }
        catch (TaskCanceledException tce) when (ct.IsCancellationRequested)
        {
            logger.LogDebug(tce, "NZBGet test canceled for client {ClientId}", LogRedaction.SanitizeText(client.Id ?? client.Name ?? client.Type));
            return (false, "NZBGet: connection canceled");
        }
        catch (TaskCanceledException tce)
        {
            logger.LogDebug(tce, "NZBGet test timed out for client {ClientId}", LogRedaction.SanitizeText(client.Id ?? client.Name ?? client.Type));
            return (false, "NZBGet: connection timed out");
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            logger.LogDebug(ex, "NZBGet test failed for client {ClientId}", LogRedaction.SanitizeText(client.Id ?? client.Name ?? client.Type));
            return (false, "NZBGet: connection failed");
        }
    }
}
