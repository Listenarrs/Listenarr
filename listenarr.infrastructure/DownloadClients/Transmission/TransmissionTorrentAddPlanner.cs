/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */


namespace Listenarr.Infrastructure.DownloadClients.Transmission;

internal static class TransmissionTorrentAddPlanner
{
    public static Dictionary<string, object> BuildArguments(
        DownloadClientConfiguration client,
        PreparedTorrentSubmission submission,
        IReadOnlyCollection<string> labels)
    {
        var arguments = new Dictionary<string, object>();
        if (submission.TorrentBytes is { Length: > 0 })
        {
            arguments["metainfo"] = Convert.ToBase64String(submission.TorrentBytes);
        }
        else if (!string.IsNullOrWhiteSpace(submission.MagnetUri))
        {
            arguments["filename"] = NormalizeMagnetUri(submission.MagnetUri);
        }
        else
        {
            throw new DownloadClientSubmissionException(
                "Transmission requires prepared torrent bytes or a verified magnet link.");
        }

        if (!string.IsNullOrWhiteSpace(client.DownloadPath))
        {
            arguments["download-dir"] = client.DownloadPath;
        }

        arguments["paused"] = false;
        if (labels.Count > 0)
        {
            arguments["labels"] = labels.ToArray();
        }

        return arguments;
    }

    private static string NormalizeMagnetUri(string magnetUri)
    {
        var queryStart = magnetUri.IndexOf('?');
        if (queryStart < 0 || queryStart >= magnetUri.Length - 1)
        {
            return magnetUri;
        }

        var segments = magnetUri[(queryStart + 1)..].Split('&');
        var changed = false;
        for (var index = 0; index < segments.Length; index++)
        {
            var equals = segments[index].IndexOf('=');
            if (equals <= 0 || equals >= segments[index].Length - 1)
            {
                continue;
            }

            var encodedValue = segments[index][(equals + 1)..];
            if (!encodedValue.Contains('%'))
            {
                continue;
            }

            var decoded = Uri.UnescapeDataString(encodedValue);
            if (decoded.Contains('&') || decoded.Contains('#'))
            {
                continue;
            }

            if (!string.Equals(decoded, encodedValue, StringComparison.Ordinal))
            {
                segments[index] = $"{segments[index][..(equals + 1)]}{decoded}";
                changed = true;
            }
        }

        return changed
            ? $"{magnetUri[..(queryStart + 1)]}{string.Join("&", segments)}"
            : magnetUri;
    }
}
