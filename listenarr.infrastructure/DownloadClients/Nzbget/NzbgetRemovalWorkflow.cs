/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Xml.Linq;
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

            var numericId = NzbgetRequestPlanner.TryParseId(id);
            if (!numericId.HasValue)
            {
                _logger.LogWarning(
                    "NZBGet removal skipped because item id {ItemId} is not a numeric NZBGet id",
                    LogRedaction.SanitizeText(id));

                return false;
            }

            try
            {
                var historyCommand = deleteFiles ? "HistoryFinalDelete" : "HistoryDelete";
                var historyDeleteResult = await _xmlRpcClient.CallAsync(
                    client,
                    "editqueue",
                    historyCommand,
                    0,
                    string.Empty,
                    new[] { numericId.Value });
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

            try
            {
                var command = deleteFiles ? "GroupFinalDelete" : "GroupDelete";
                var editResult = await _xmlRpcClient.CallAsync(client, "editqueue", command, 0, string.Empty, new[] { numericId.Value });
                var success = editResult.Element("boolean")?.Value == "1";

                if (success)
                {
                    _logger.LogInformation("Removed NZB {Id} from NZBGet queue (deleteFiles={DeleteFiles})", LogRedaction.SanitizeText(id), deleteFiles);
                    return true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Could not remove {Id} from NZBGet queue (may not be active)", LogRedaction.SanitizeText(id));
            }

            var absent = await IsAbsentFromHistoryAndQueueAsync(client, numericId.Value, id, ct);
            if (absent)
            {
                _logger.LogInformation(
                    "NZBGet item {Id} is already absent after removal check; treating cleanup as complete",
                    LogRedaction.SanitizeText(id));
                return true;
            }

            _logger.LogWarning("NZBGet reported failure when removing {Id} from both history and queue", LogRedaction.SanitizeText(id));
            return false;
        }

        private async Task<bool> IsAbsentFromHistoryAndQueueAsync(
            DownloadClientConfiguration client,
            int numericId,
            string originalId,
            CancellationToken ct)
        {
            try
            {
                var historyResult = await _xmlRpcClient.CallAsync(client, "history", false);
                var inHistory = GetHistoryEntries(historyResult).Any(entry =>
                    TryGetInt(entry, "ID", out var historyId) && historyId == numericId);
                if (inHistory)
                {
                    return false;
                }

                var queueResult = await _xmlRpcClient.CallAsync(client, "listgroups");
                var inQueue = GetGroupEntries(queueResult).Any(entry =>
                    TryGetInt(entry, "NZBID", out var nzbId) && nzbId == numericId ||
                    TryGetInt(entry, "ID", out var id) && id == numericId);
                return !inQueue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(
                    ex,
                    "Could not verify whether NZBGet item {Id} is absent after cleanup failure",
                    LogRedaction.SanitizeText(originalId));
                return false;
            }
        }

        private static IEnumerable<Dictionary<string, XElement?>> GetHistoryEntries(XElement result) =>
            GetStructEntries(result);

        private static IEnumerable<Dictionary<string, XElement?>> GetGroupEntries(XElement result) =>
            GetStructEntries(result);

        private static IEnumerable<Dictionary<string, XElement?>> GetStructEntries(XElement result)
        {
            var arrayData = result.Element("array")?.Element("data");
            if (arrayData == null)
            {
                yield break;
            }

            foreach (var structElement in arrayData.Elements("value")
                .Select(valueElement => valueElement.Element("struct"))
                .Where(s => s != null))
            {
                yield return structElement!.Elements("member").ToDictionary(
                    m => m.Element("name")?.Value ?? string.Empty,
                    m => m.Element("value")?.Elements().FirstOrDefault());
            }
        }

        private static bool TryGetInt(
            Dictionary<string, XElement?> members,
            string key,
            out int value)
        {
            value = 0;
            return members.TryGetValue(key, out var element) && int.TryParse(element?.Value, out value);
        }
    }
}
