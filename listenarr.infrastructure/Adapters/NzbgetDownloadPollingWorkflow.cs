/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Text;
using System.Text.Json;
using Listenarr.Domain.Common;
using Listenarr.Domain.Models;
using Listenarr.Domain.Models.Exceptions;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Adapters
{
    internal sealed class NzbgetDownloadPollingWorkflow
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger _logger;
        private readonly string _clientType;

        public NzbgetDownloadPollingWorkflow(
            IHttpClientFactory httpClientFactory,
            ILogger logger,
            string clientType)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _clientType = clientType;
        }

        public async Task<List<Download>> FetchDownloadsAsync(
            DownloadClientConfiguration client,
            List<Download> downloads,
            CancellationToken cancellationToken)
        {
            _logger.LogDebug("Polling NZBGet client {ClientName}", client.Name);
            try
            {
                var baseUrl = DownloadClientUriBuilder.BuildUri(client, "/jsonrpc");

                using var http = _httpClientFactory.CreateClient(_clientType);

                var authHeader = NzbgetAuthentication.BuildAuthHeader(client);
                if (authHeader != null)
                {
                    http.DefaultRequestHeaders.Authorization = authHeader;
                }

                var statusRequest = new
                {
                    method = "status",
                    id = 2
                };

                var statusJsonContent = JsonSerializer.Serialize(statusRequest);
                using var statusHttpContent = new StringContent(statusJsonContent, Encoding.UTF8, "application/json");

                using var statusResponse = await http.PostAsync(baseUrl, statusHttpContent, cancellationToken);

                if (statusResponse.IsSuccessStatusCode)
                {
                    var statusJson = await statusResponse.Content.ReadAsStringAsync(cancellationToken);
                    var statusDoc = JsonDocument.Parse(statusJson);

                    if (statusDoc.RootElement.TryGetProperty("result", out _))
                    {
                        var queueRequest = new
                        {
                            method = "listgroups",
                            id = 3
                        };

                        var queueJsonContent = JsonSerializer.Serialize(queueRequest);
                        using var queueHttpContent = new StringContent(queueJsonContent, Encoding.UTF8, "application/json");

                        using var queueResponse = await http.PostAsync(baseUrl, queueHttpContent, cancellationToken);

                        if (queueResponse.IsSuccessStatusCode)
                        {
                            var queueJson = await queueResponse.Content.ReadAsStringAsync(cancellationToken);
                            var queueDoc = JsonDocument.Parse(queueJson);

                            if (queueDoc.RootElement.TryGetProperty("result", out var queueResult) && queueResult.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var group in queueResult.EnumerateArray())
                                {
                                    try
                                    {
                                        var nzbId = group.TryGetProperty("NZBID", out var nzbIdProp) ? nzbIdProp.GetInt32() : 0;
                                        var nzbName = group.TryGetProperty("NZBName", out var nameProp) ? nameProp.GetString() ?? "" : "";
                                        var status = group.TryGetProperty("Status", out var statusProp) ? statusProp.GetString() ?? "" : "";
                                        var fileSizeMB = group.TryGetProperty("FileSizeMB", out var sizeProp) ? sizeProp.GetString() ?? "" : "";
                                        var remainingSizeMB = group.TryGetProperty("RemainingSizeMB", out var remainingSizeProp) ? remainingSizeProp.GetString() ?? "" : "";
                                        var matchingDownload = downloads.FirstOrDefault(dl =>
                                        {
                                            var clientItemId = dl.GetExternalId();
                                            return !string.IsNullOrEmpty(clientItemId) &&
                                                    clientItemId.Equals(nzbId.ToString(), StringComparison.OrdinalIgnoreCase);
                                        });

                                        if (matchingDownload == null && !string.IsNullOrEmpty(nzbName))
                                        {
                                            matchingDownload = downloads.FirstOrDefault(dl => TitleUtils.AreTitlesSimilar(dl.Title, nzbName));
                                        }

                                        if (matchingDownload != null &&
                                            double.TryParse(fileSizeMB, out var totalMB) &&
                                            double.TryParse(remainingSizeMB, out var remainingMB))
                                        {
                                            var progress = totalMB > 0 ? (totalMB - remainingMB) / totalMB : 0.0;
                                            var amountLeft = (long)(remainingMB * 1024 * 1024);

                                            AdapterUtils.MapDownloadProgress(matchingDownload, progress, amountLeft, status);
                                        }
                                    }
                                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                    {
                                        _logger.LogWarning(ex, "Error updating NZBGet queue progress for group");
                                    }
                                }
                            }
                        }
                    }
                }

                return downloads;
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                throw new DownloadClientAdapterPollingException($"Error polling NZBGet client {client.Id}", exception);
            }
        }
    }
}
