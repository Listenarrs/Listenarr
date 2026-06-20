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

namespace Listenarr.Infrastructure.DownloadClients.Nzbget
{
    internal sealed class NzbgetRemovalWorkflow
    {
        private readonly NzbgetXmlRpcClient _xmlRpcClient;
        private readonly ILogger _logger;

        public NzbgetRemovalWorkflow(NzbgetXmlRpcClient xmlRpcClient, ILogger logger)
        {
            _xmlRpcClient = xmlRpcClient;
            _logger = logger;
        }

        public async Task<bool> RemoveAsync(DownloadClientConfiguration client, string id, bool deleteFiles = false, CancellationToken ct = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            // First try to parse as numeric NZBID (for queue removal)
            var numericId = NzbgetRequestPlanner.TryParseId(id);

            // If it's not a numeric ID, it might be a droneId (GUID from Listenarr)
            // Try to find it in history first
            if (!numericId.HasValue)
            {
                _logger.LogInformation("ID {Id} is not numeric, searching NZBGet history for matching download", LogRedaction.SanitizeText(id));

                try
                {
                    // Get history to find the NZBID by matching droneId
                    var historyResult = await _xmlRpcClient.CallAsync(client, "history", false);
                    var arrayData = historyResult.Element("array")?.Element("data");

                    var historyCount = arrayData?.Elements("value").Count() ?? 0;
                    _logger.LogInformation("NZBGet history contains {Count} entries", historyCount);

                    if (arrayData != null)
                    {
                        foreach (var members in arrayData.Elements("value")
                            .Select(valueElement => valueElement.Element("struct"))
                            .Where(s => s != null)
                            .Select(s => s!.Elements("member").ToDictionary(
                                m => m.Element("name")?.Value ?? string.Empty,
                                m => m.Element("value")?.Elements().FirstOrDefault()
                            )))
                        {

                            // Log what fields this history entry has
                            _logger.LogInformation("History entry has fields: {Fields}", string.Join(", ", members.Keys));

                            // Check if this history entry has matching droneId in parameters
                            if (members.TryGetValue("Parameters", out var paramsElement))
                            {
                                var paramsArray = paramsElement?.Element("array")?.Element("data");
                                var paramCount = paramsArray?.Elements("value").Count() ?? 0;
                                _logger.LogInformation("History entry has {Count} parameters", paramCount);

                                if (paramsArray != null)
                                {
                                    foreach (var paramMembers in paramsArray.Elements("value")
                                        .Select(paramValueElement => paramValueElement.Element("struct"))
                                        .Where(ps => ps != null)
                                        .Select(ps => ps!.Elements("member").ToDictionary(
                                            m => m.Element("name")?.Value ?? string.Empty,
                                            m => m.Element("value")?.Elements().FirstOrDefault()?.Value ?? string.Empty
                                        )))
                                    {

                                        // Log all parameters for debugging
                                        foreach (var pm in paramMembers)
                                        {
                                            _logger.LogDebug("NZBGet History Parameter: Name={Name}, Value={Value}", pm.Key, LogRedaction.SanitizeText(pm.Value));
                                        }

                                        if (paramMembers.TryGetValue("Name", out var paramName) &&
                                            paramMembers.TryGetValue("Value", out var paramValue) &&
                                            paramName == "*drone" && paramValue == id &&
                                            members.TryGetValue("ID", out var idElement) &&
                                            int.TryParse(idElement?.Value, out var foundNumericId))
                                        {
                                            // Found matching droneId, get the NZBID
                                            _logger.LogDebug("Found NZBID {NzbId} for droneId {DroneId} in history", foundNumericId, LogRedaction.SanitizeText(id));
                                            numericId = foundNumericId;
                                            break;
                                        }
                                    }
                                }
                            }

                            if (numericId.HasValue) break;
                        }
                    }
                }
                catch (Exception histEx) when (histEx is not OperationCanceledException && histEx is not OutOfMemoryException && histEx is not StackOverflowException)
                {
                    _logger.LogDebug(histEx, "Failed to search NZBGet history for download {Id}", LogRedaction.SanitizeText(id));
                }
            }

            if (!numericId.HasValue)
            {
                _logger.LogWarning("Cannot remove NZB {Id} - not found in queue or history", LogRedaction.SanitizeText(id));
                return false;
            }

            // Try to remove from history first (for completed downloads)
            try
            {
                var historyDeleteResult = await _xmlRpcClient.CallAsync(client, "editqueue", "HistoryDelete", 0, string.Empty, new[] { numericId.Value });
                var historySuccess = historyDeleteResult.Element("boolean")?.Value == "1";

                if (historySuccess)
                {
                    _logger.LogInformation("Removed NZB {Id} from NZBGet history (deleteFiles={DeleteFiles})", LogRedaction.SanitizeText(id), deleteFiles);
                    return true;
                }
            }
            catch (Exception histEx) when (histEx is not OperationCanceledException && histEx is not OutOfMemoryException && histEx is not StackOverflowException)
            {
                _logger.LogDebug(histEx, "Could not remove {Id} from NZBGet history (may not be in history)", LogRedaction.SanitizeText(id));
            }

            // Fall back to queue removal (for active downloads)
            try
            {
                var command = deleteFiles ? "GroupDeleteFinal" : "GroupDelete";
                var editResult = await _xmlRpcClient.CallAsync(client, "editqueue", command, 0, string.Empty, new[] { numericId.Value });
                var success = editResult.Element("boolean")?.Value == "1";

                if (success)
                {
                    _logger.LogInformation("Removed NZB {Id} from NZBGet queue (deleteFiles={DeleteFiles})", LogRedaction.SanitizeText(id), deleteFiles);
                    return true;
                }

                _logger.LogWarning("NZBGet reported failure when removing {Id} from both history and queue", LogRedaction.SanitizeText(id));
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error removing NZB {Id} from NZBGet", LogRedaction.SanitizeText(id));
                return false;
            }
        }
    }
}
