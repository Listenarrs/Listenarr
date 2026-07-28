/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Application.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads.Submission;

/// <summary>
/// Selects and submits audiobook-level downloads to the native Slskd workflow.
/// A null result tells the caller to continue with torrent/NZB search.
/// </summary>
public sealed class NativeSlskdDownloadRouter(
    IConfigurationService configurationService,
    ISlskdDownloadService slskdDownloadService,
    ILogger<NativeSlskdDownloadRouter> logger)
{
    public async Task<SearchAndDownloadResult?> TryRouteAsync(Audiobook audiobook, CancellationToken cancellationToken = default)
    {
        var clients = await configurationService.GetDownloadClientConfigurationsAsync();
        var slskdClient = SelectClient(clients);
        if (slskdClient is null)
        {
            return null;
        }

        try
        {
            var native = await slskdDownloadService.SearchSubmitAndPollAsync(
                slskdClient,
                new SlskdSubmissionRequest(
                    audiobook.Id,
                    DownloadSearchQueryBuilder.Build(audiobook),
                    audiobook.Title,
                    audiobook.Authors?.FirstOrDefault()),
                cancellationToken);

            return new SearchAndDownloadResult
            {
                Success = true,
                Message = "Successfully submitted native Slskd audiobook download",
                DownloadId = native.BatchId,
                IndexerUsed = "Slskd",
                DownloadClientUsed = slskdClient.Id
            };
        }
        catch (DuplicateDownloadSubmissionException ex)
        {
            logger.LogInformation(
                "Native Slskd submission rejected as a duplicate for audiobook {AudiobookId}",
                audiobook.Id);
            return new SearchAndDownloadResult
            {
                Success = false,
                Message = ex.Message,
                IndexerUsed = "Slskd",
                DownloadClientUsed = slskdClient.Id
            };
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException or StackOverflowException)
                                   && GetBooleanSetting(slskdClient, "allowProtocolFallback"))
        {
            logger.LogWarning(
                ex,
                "Native Slskd submission failed; explicit protocol fallback is enabled for {ClientId}",
                slskdClient.Id);
            return null;
        }
    }

    internal static DownloadClientConfiguration? SelectClient(IEnumerable<DownloadClientConfiguration> clients)
    {
        var first = clients
            .Where(client => client.IsEnabled)
            .OrderByDescending(client => GetBooleanSetting(client, "isDefault"))
            .ThenBy(client => GetIntegerSetting(client, "priority", 50))
            .ThenBy(client => client.CreatedAt)
            .FirstOrDefault();

        return first is not null && string.Equals(first.Type, "slskd", StringComparison.OrdinalIgnoreCase)
            ? first
            : null;
    }

    private static bool GetBooleanSetting(DownloadClientConfiguration client, string key)
    {
        if (!client.Settings.TryGetValue(key, out var value) || value is null) return false;
        return value is System.Text.Json.JsonElement element
            ? element.ValueKind == System.Text.Json.JsonValueKind.True ||
              (element.ValueKind == System.Text.Json.JsonValueKind.String &&
               bool.TryParse(element.GetString(), out var parsedElement) && parsedElement)
            : bool.TryParse(value.ToString(), out var parsedValue) && parsedValue;
    }

    private static int GetIntegerSetting(DownloadClientConfiguration client, string key, int fallback)
    {
        if (!client.Settings.TryGetValue(key, out var value) || value is null) return fallback;
        return int.TryParse(value.ToString(), out var parsed) ? parsed : fallback;
    }
}
