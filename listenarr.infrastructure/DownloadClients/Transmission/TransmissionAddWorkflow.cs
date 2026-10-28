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

namespace Listenarr.Infrastructure.DownloadClients.Transmission
{
    internal sealed class TransmissionAddWorkflow(
        TransmissionRpcClient rpcClient,
        ILogger<TransmissionAdapter> logger)
    {
        public async Task<DownloadClientSubmissionResult> AddAsync(
            DownloadClientConfiguration client,
            PreparedDownloadSubmission submission,
            CancellationToken ct = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (submission is not PreparedTorrentSubmission torrent)
                throw new DownloadClientSubmissionException("Transmission requires a prepared torrent submission.");

            var labels = TransmissionRequestPlanner.CollectLabels(client);
            var arguments = TransmissionTorrentAddPlanner.BuildArguments(client, torrent, labels);

            // Use old format for compatibility with Transmission < 4.1.0.
            var payload = new
            {
                method = "torrent-add",
                arguments,
                tag = 1
            };

            try
            {
                var response = await rpcClient.InvokeAsync(client, payload, ct);
                logger.LogDebug("Transmission add torrent response: {Response}", response.GetRawText());

                if (!response.TryGetProperty("result", out var resultProp) ||
                    !string.Equals(resultProp.GetString(), "success", StringComparison.OrdinalIgnoreCase))
                {
                    var errorMsg = resultProp.ValueKind == JsonValueKind.String ? resultProp.GetString() : "Unknown error";
                    throw new Exception($"Transmission RPC error: {errorMsg}");
                }

                if (response.TryGetProperty("arguments", out var args))
                {
                    if (args.TryGetProperty("torrent-added", out var added) && added.ValueKind == JsonValueKind.Object)
                    {
                        var torrentId = TransmissionRequestPlanner.ExtractTorrentIdentifier(added);
                        if (string.IsNullOrWhiteSpace(torrentId))
                            throw new DownloadClientSubmissionException("Transmission did not return a verified torrent identifier.");
                        logger.LogInformation("Transmission successfully added torrent '{Title}' with id/hash: {Id}", LogRedaction.SanitizeText(torrent.Title), LogRedaction.SanitizeText(torrentId));
                        return new DownloadClientSubmissionResult(torrentId, torrent.InfoHash);
                    }

                    if (args.TryGetProperty("torrent-duplicate", out var duplicate) && duplicate.ValueKind == JsonValueKind.Object)
                    {
                        var existingId = TransmissionRequestPlanner.ExtractTorrentIdentifier(duplicate);
                        if (string.IsNullOrWhiteSpace(existingId))
                            throw new DownloadClientSubmissionException("Transmission did not return a verified duplicate torrent identifier.");
                        logger.LogInformation("Transmission reported duplicate torrent for '{Title}' with id/hash {Id}", LogRedaction.SanitizeText(torrent.Title), LogRedaction.SanitizeText(existingId));
                        return new DownloadClientSubmissionResult(existingId, torrent.InfoHash, WasDuplicate: true);
                    }
                }

                throw new DownloadClientSubmissionException("Transmission did not return a verified torrent identifier.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Failed to add torrent to Transmission for client {ClientName}", LogRedaction.SanitizeText(client.Name ?? client.Id));
                throw;
            }
        }
    }
}
