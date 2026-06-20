/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using BencodeNET.Parsing;
using BencodeNET.Torrents;

namespace Listenarr.Infrastructure.Downloads.Submission;

public sealed class TorrentMetadataService : ITorrentMetadataService
{
    public PreparedTorrentSubmission Prepare(
        TrustedDownloadCandidate candidate,
        byte[]? torrentBytes,
        string? magnetUri,
        string originalLocator,
        string? fileName = null)
    {
        var normalizedMagnet = DownloadClientUriBuilder.NormalizeMagnetLink(magnetUri);
        var infoHash = torrentBytes is { Length: > 0 }
            ? GetHashFromTorrent(torrentBytes)
            : GetHashFromMagnet(normalizedMagnet);

        if (string.IsNullOrWhiteSpace(infoHash))
        {
            throw new DownloadClientSubmissionException(
                "Unable to obtain a verified hash from the torrent metadata.");
        }

        var trackers = torrentBytes is { Length: > 0 }
            ? MyAnonamouseHelper.ExtractAnnounceUrls(torrentBytes)
            : [];

        return new PreparedTorrentSubmission(
            candidate.Title,
            candidate.Artist,
            candidate.Album,
            candidate.Source,
            candidate.Quality,
            candidate.Language,
            candidate.Size,
            originalLocator,
            infoHash,
            torrentBytes,
            string.IsNullOrWhiteSpace(normalizedMagnet) ? null : normalizedMagnet,
            fileName ?? candidate.SourceDescriptor.FileName,
            trackers);
    }

    private static string GetHashFromTorrent(byte[] torrentBytes)
    {
        try
        {
            using var stream = new MemoryStream(torrentBytes);
            var torrent = new BencodeParser().Parse<Torrent>(stream);
            return torrent.GetInfoHash().ToUpperInvariant();
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            throw new DownloadClientSubmissionException(
                "The downloaded torrent metadata is invalid.",
                exception);
        }
    }

    private static string? GetHashFromMagnet(string? magnetUri)
    {
        if (string.IsNullOrWhiteSpace(magnetUri))
        {
            return null;
        }

        var marker = "xt=urn:btih:";
        var start = magnetUri.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = magnetUri.IndexOf('&', start);
        var value = Uri.UnescapeDataString(
            magnetUri[start..(end < 0 ? magnetUri.Length : end)]).Trim();
        if (value.Length == 40 && value.All(Uri.IsHexDigit))
        {
            return value.ToUpperInvariant();
        }

        return value.Length == 32 && TryDecodeBase32(value, out var bytes)
            ? Convert.ToHexString(bytes)
            : null;
    }

    private static bool TryDecodeBase32(string value, out byte[] decoded)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        decoded = new byte[20];
        var outputIndex = 0;
        var buffer = 0;
        var bits = 0;

        foreach (var character in value.ToUpperInvariant())
        {
            var digit = alphabet.IndexOf(character);
            if (digit < 0)
            {
                decoded = [];
                return false;
            }

            buffer = (buffer << 5) | digit;
            bits += 5;
            if (bits < 8)
            {
                continue;
            }

            bits -= 8;
            if (outputIndex >= decoded.Length)
            {
                decoded = [];
                return false;
            }

            decoded[outputIndex++] = (byte)(buffer >> bits);
            buffer &= (1 << bits) - 1;
        }

        if (outputIndex != 20 || bits != 0)
        {
            decoded = [];
            return false;
        }

        return true;
    }
}
