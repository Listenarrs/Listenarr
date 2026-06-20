/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Search.Indexers.Torznab
{
    internal sealed partial class TorznabResponseParser
    {
        private long ParseSizeString(string sizeStr)
        {
            if (string.IsNullOrEmpty(sizeStr))
                return 0;

            // Remove any commas and extra spaces
            sizeStr = sizeStr.Replace(",", "").Trim();

            // Try to parse as direct bytes first
            if (long.TryParse(sizeStr, out var bytes))
                return bytes;

            // Handle formats like "500 MB", "1.2 GB", "1024 KB", "3.7 GiB", "279.0 MiB", etc.
            // Support both decimal (KB/MB/GB/TB) and binary (KiB/MiB/GiB/TiB) units
            var match = System.Text.RegularExpressions.Regex.Match(sizeStr, @"^([\d\.]+)\s*(KiB|MiB|GiB|TiB|KB|MB|GB|TB|B)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success &&
                double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                var unit = match.Groups[2].Value.ToUpper();
                return unit switch
                {
                    "B" => (long)value,
                    "KB" => (long)(value * 1000),
                    "MB" => (long)(value * 1000 * 1000),
                    "GB" => (long)(value * 1000 * 1000 * 1000),
                    "TB" => (long)(value * 1000 * 1000 * 1000 * 1000),
                    "KIB" => (long)(value * 1024),
                    "MIB" => (long)(value * 1024 * 1024),
                    "GIB" => (long)(value * 1024 * 1024 * 1024),
                    "TIB" => (long)(value * 1024 * 1024 * 1024 * 1024),
                    _ => (long)value
                };
            }

            _logger.LogWarning("Unable to parse size string: '{SizeStr}'", sizeStr);
            return 0;
        }

        // (Helper methods for containment and fuzzy scoring are implemented above.)
    }
}
