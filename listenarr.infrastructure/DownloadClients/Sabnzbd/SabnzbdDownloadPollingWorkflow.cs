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
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.DownloadClients.Sabnzbd
{
    internal sealed class SabnzbdDownloadPollingWorkflow
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly SabnzbdRequestBuilder _requestBuilder;
        private readonly IAppMetricsService _appMetricsService;
        private readonly ILogger _logger;
        private readonly string _clientType;

        public SabnzbdDownloadPollingWorkflow(
            IHttpClientFactory httpFactory,
            SabnzbdRequestBuilder requestBuilder,
            IAppMetricsService appMetricsService,
            ILogger logger,
            string clientType)
        {
            _httpFactory = httpFactory;
            _requestBuilder = requestBuilder;
            _appMetricsService = appMetricsService;
            _logger = logger;
            _clientType = clientType;
        }

        public async Task<List<Download>> FetchDownloadsAsync(
            DownloadClientConfiguration client,
            List<Download> downloads,
            CancellationToken cancellationToken)
        {
            _logger.LogDebug("Polling SABnzbd client {ClientName}", client.Name);
            try
            {
                using var http = _httpFactory.CreateClient(_clientType);

                var requestContext = _requestBuilder.CreateContext(client);
                if (!requestContext.HasApiKey)
                {
                    throw new DownloadClientAdapterPollingException($"SABnzbd API key not configured for client {client.Id}");
                }

                var queueUrl = _requestBuilder.BuildUrl(requestContext, new Dictionary<string, string>
                {
                    ["mode"] = "queue",
                    ["output"] = "json"
                });
                _logger.LogDebug("SABnzbd poll queue URL (redacted): {Url}", LogRedaction.RedactText(queueUrl, _requestBuilder.BuildSensitiveValues(requestContext)));
                using var queueResponse = await http.GetAsync(queueUrl, cancellationToken);

                if (queueResponse.IsSuccessStatusCode)
                {
                    var queueJson = await queueResponse.Content.ReadAsStringAsync(cancellationToken);
                    var queueDoc = JsonDocument.Parse(queueJson);

                    if (queueDoc.RootElement.TryGetProperty("queue", out var queue) &&
                        queue.TryGetProperty("slots", out var queueSlots) &&
                        queueSlots.ValueKind == JsonValueKind.Array)
                    {
                        foreach (Download download in downloads)
                        {
                            var clientDownloadId = download.GetExternalId();

                            foreach (var slot in queueSlots.EnumerateArray())
                            {
                                try
                                {
                                    var nzoId = slot.TryGetProperty("nzo_id", out var nzoIdProp) ? nzoIdProp.GetString() ?? "" : "";
                                    if (!string.IsNullOrEmpty(clientDownloadId) && !string.Equals(nzoId, clientDownloadId, StringComparison.OrdinalIgnoreCase))
                                    {
                                        continue;
                                    }

                                    var filename = slot.TryGetProperty("filename", out var filenameProp) ? filenameProp.GetString() ?? "" : "";
                                    if (!TitleUtils.AreTitlesSimilar(download.Title, filename))
                                    {
                                        continue;
                                    }

                                    var percentage = slot.TryGetProperty("percentage", out var percentageProp) ? SabnzbdResponseMapper.ParseJsonDouble(percentageProp) : 0.0;
                                    var mbleft = slot.TryGetProperty("mbleft", out var mbleftProp) ? SabnzbdResponseMapper.ParseJsonDouble(mbleftProp) : 0.0;
                                    var status = slot.TryGetProperty("status", out var statusProp) ? statusProp.GetString() ?? "" : "";

                                    var progressPercent = percentage;
                                    var amountLeft = (long)(mbleft * 1024 * 1024);

                                    AdapterUtils.MapDownloadProgress(download, progressPercent, amountLeft, status);
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                {
                                    _logger.LogWarning(ex, "Error updating SABnzbd queue progress for slot");
                                }
                            }
                        }
                    }
                }

                var historyUrl = _requestBuilder.BuildUrl(requestContext, new Dictionary<string, string>
                {
                    ["mode"] = "history",
                    ["limit"] = "100",
                    ["output"] = "json"
                });
                _logger.LogDebug("SABnzbd history URL (redacted): {Url}", LogRedaction.RedactText(historyUrl, _requestBuilder.BuildSensitiveValues(requestContext)));
                using var historyResponse = await http.GetAsync(historyUrl, cancellationToken);

                if (!historyResponse.IsSuccessStatusCode)
                {
                    throw new DownloadClientAdapterPollingException($"Failed to fetch SABnzbd history for {client.Id}: {historyResponse.StatusCode}");
                }

                var historyJson = await historyResponse.Content.ReadAsStringAsync(cancellationToken);
                var historyDoc = JsonDocument.Parse(historyJson);

                if (!historyDoc.RootElement.TryGetProperty("history", out var history) ||
                    !history.TryGetProperty("slots", out var slots) ||
                    slots.ValueKind != JsonValueKind.Array)
                {
                    throw new DownloadClientAdapterPollingException($"No history data found for SABnzbd client {client.Id}");
                }

                var historyLookup = SabnzbdHistoryLookupBuilder.Build(slots, _logger);
                var completedItems = historyLookup.CompletedItems;
                var failedItems = historyLookup.FailedItems;

                _logger.LogDebug("Found {CompletedCount} completed items in SABnzbd history for client {ClientName}",
                    completedItems.Count, client.Name);

                foreach (var dl in downloads)
                {
                    if (dl.Status == DownloadStatus.Moved ||
                        dl.Status == DownloadStatus.Processing ||
                        dl.Status == DownloadStatus.ImportPending)
                        continue;

                    try
                    {
                        var failedMatch = failedItems.FirstOrDefault(item =>
                            (!string.IsNullOrEmpty(item.NzoId) && !string.IsNullOrEmpty(dl.GetExternalId()) &&
                                string.Equals(item.NzoId, dl.GetExternalId(), StringComparison.OrdinalIgnoreCase)) ||
                            string.Equals(item.Name, dl.Title, StringComparison.OrdinalIgnoreCase) ||
                            (!string.IsNullOrEmpty(dl.Title) && item.Name.Contains(dl.Title, StringComparison.OrdinalIgnoreCase))
                        );

                        if (!string.IsNullOrEmpty(failedMatch.Name))
                        {
                            continue;
                        }

                        var matchingItem = completedItems.FirstOrDefault(item =>
                            (!string.IsNullOrEmpty(item.NzoId) && !string.IsNullOrEmpty(dl.GetExternalId()) &&
                                string.Equals(item.NzoId, dl.GetExternalId(), StringComparison.OrdinalIgnoreCase)) ||
                            string.Equals(item.Name, dl.Title, StringComparison.OrdinalIgnoreCase) ||
                            (!string.IsNullOrEmpty(dl.Title) && item.Name.Contains(dl.Title, StringComparison.OrdinalIgnoreCase))
                        );

                        if (!string.IsNullOrEmpty(matchingItem.Name))
                        {
                            AdapterUtils.MapDownloadProgress(dl, 100.0, 0, "success");

                            if (!string.IsNullOrEmpty(matchingItem.Path))
                            {
                                dl.DownloadPath = matchingItem.Path;
                            }

                            try
                            {
                                if (!string.IsNullOrEmpty(matchingItem.NzoId) && !string.IsNullOrEmpty(dl.GetExternalId()) && string.Equals(matchingItem.NzoId, dl.GetExternalId(), StringComparison.OrdinalIgnoreCase))
                                {
                                    _appMetricsService.Increment("sabnzbd.history.match.nzo");
                                }
                                else if (!string.IsNullOrEmpty(matchingItem.Name) && string.Equals(matchingItem.Name, dl.Title, StringComparison.OrdinalIgnoreCase))
                                {
                                    _appMetricsService.Increment("sabnzbd.history.match.title_exact");
                                }
                                else
                                {
                                    _appMetricsService.Increment("sabnzbd.history.match.title_contains");
                                }
                            }
                            catch (Exception caughtEx) when (caughtEx is not OperationCanceledException && caughtEx is not OutOfMemoryException && caughtEx is not StackOverflowException)
                            {
                                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                            }
                            _logger.LogInformation("Found completed SABnzbd download: {DownloadTitle} -> {CompletedName} at {Path}",
                                dl.Title, matchingItem.Name, matchingItem.Path);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Error processing download {DownloadId} while polling SABnzbd", dl.Id);
                    }
                }

                return downloads;
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                throw new DownloadClientAdapterPollingException($"Error polling SABnzbd client {client.Id}");
            }
        }
    }
}
