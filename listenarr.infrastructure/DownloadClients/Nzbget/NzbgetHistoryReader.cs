/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System.Globalization;
using System.Xml.Linq;

namespace Listenarr.Infrastructure.DownloadClients.Nzbget
{
    internal sealed record NzbgetHistoryEntry
    {
        public required string CanonicalNzbId { get; init; }
        public required string Title { get; init; }
        public required string Category { get; init; }
        public required string RawStatus { get; init; }
        public required NzbgetHistoryOutcome Outcome { get; init; }
        public required string DestDir { get; init; }
        public required string FinalDir { get; init; }
        public required long TotalSizeBytes { get; init; }
        public required long DownloadedSizeBytes { get; init; }
        public required DateTime? HistoryTimeUtc { get; init; }

        public string CompletedPath =>
            Outcome == NzbgetHistoryOutcome.Completed
                ? (!string.IsNullOrWhiteSpace(FinalDir) ? FinalDir : DestDir)
                : string.Empty;
    }

    internal enum NzbgetHistoryOutcome
    {
        Ignored = 0,
        Completed = 1,
        Failed = 2
    }

    internal sealed class NzbgetHistoryReader
    {
        private const decimal BytesPerMegabyte = 1_048_576m;
        private readonly NzbgetXmlRpcClient _xmlRpcClient;

        public NzbgetHistoryReader(NzbgetXmlRpcClient xmlRpcClient)
        {
            _xmlRpcClient = xmlRpcClient;
        }

        public async Task<IReadOnlyList<NzbgetHistoryEntry>> ReadAsync(
            DownloadClientConfiguration client,
            CancellationToken cancellationToken)
        {
            var result = await _xmlRpcClient.CallAsync(
                new NzbgetXmlRpcRequest
                {
                    Client = client,
                    MethodName = "history",
                    Parameters = [false]
                },
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            return ParseEntries(result, cancellationToken);
        }

        internal static IReadOnlyList<NzbgetHistoryEntry> ParseEntries(
            XElement result,
            CancellationToken cancellationToken)
        {
            var data = result.Element("array")?.Element("data")
                ?? throw new InvalidOperationException(
                    "Invalid NZBGet history response: expected array/data.");
            var entries = new List<NzbgetHistoryEntry>();

            foreach (var value in data.Elements("value"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var structElement = value.Element("struct");
                if (structElement != null)
                {
                    entries.Add(ParseEntry(structElement));
                }
            }

            return entries.AsReadOnly();
        }

        private static NzbgetHistoryEntry ParseEntry(XElement structElement)
        {
            var members = structElement.Elements("member").ToDictionary(
                member => member.Element("name")?.Value ?? string.Empty,
                member => member.Element("value"),
                StringComparer.Ordinal);
            var rawStatus = ReadScalar(members, "Status").Trim();
            var (totalSizeBytes, totalSizeKnown) = ParseMegabytes(ReadScalar(members, "FileSizeMB"));
            var (downloadedSizeBytes, _) = ParseMegabytes(ReadScalar(members, "DownloadedSizeMB"));

            if (totalSizeKnown)
            {
                downloadedSizeBytes = Math.Min(downloadedSizeBytes, totalSizeBytes);
            }

            return new NzbgetHistoryEntry
            {
                CanonicalNzbId = ParseCanonicalNzbId(ReadScalar(members, "NZBID")),
                Title = ReadScalar(members, "NZBName"),
                Category = ReadScalar(members, "Category"),
                RawStatus = rawStatus,
                Outcome = ClassifyOutcome(rawStatus),
                DestDir = ReadScalar(members, "DestDir"),
                FinalDir = ReadScalar(members, "FinalDir"),
                TotalSizeBytes = totalSizeBytes,
                DownloadedSizeBytes = downloadedSizeBytes,
                HistoryTimeUtc = ParseHistoryTime(ReadScalar(members, "HistoryTime"))
            };
        }

        private static string ReadScalar(
            IReadOnlyDictionary<string, XElement?> members,
            string name)
        {
            return members.TryGetValue(name, out var value)
                ? value?.Elements().FirstOrDefault()?.Value ?? string.Empty
                : string.Empty;
        }

        private static string ParseCanonicalNzbId(string value)
        {
            var trimmedValue = value.Trim();
            return long.TryParse(
                trimmedValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsedValue) &&
                parsedValue > 0
                    ? trimmedValue
                    : string.Empty;
        }

        private static NzbgetHistoryOutcome ClassifyOutcome(string rawStatus)
        {
            if (rawStatus.StartsWith("SUCCESS/", StringComparison.OrdinalIgnoreCase))
            {
                return NzbgetHistoryOutcome.Completed;
            }

            return rawStatus.StartsWith("FAILURE/", StringComparison.OrdinalIgnoreCase)
                ? NzbgetHistoryOutcome.Failed
                : NzbgetHistoryOutcome.Ignored;
        }

        private static (long Bytes, bool IsKnown) ParseMegabytes(string value)
        {
            if (!decimal.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var megabytes))
            {
                return (0, false);
            }

            if (megabytes <= 0)
            {
                return (0, true);
            }

            var maxMegabytes = long.MaxValue / BytesPerMegabyte;
            return megabytes >= maxMegabytes
                ? (long.MaxValue, true)
                : (decimal.ToInt64(decimal.Truncate(megabytes * BytesPerMegabyte)), true);
        }

        private static DateTime? ParseHistoryTime(string value)
        {
            if (!long.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var unixSeconds))
            {
                return null;
            }

            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
    }
}
