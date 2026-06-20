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
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Search.Indexers.MyAnonamouse
{
    internal static class MyAnonamouseSizeParser
    {
        public static long ExtractFromDescription(string? description, ILogger logger)
        {
            if (string.IsNullOrEmpty(description))
                return 0;

            var match = Regex.Match(description, @"Total Size\s*:\s*([\d\.,]+)\s*(MB|GB|KB|B)\s*\(([\d\s,]+)\s*bytes?\)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var bytesStr = match.Groups[3].Value.Replace(",", "").Replace(" ", "");
                if (long.TryParse(bytesStr, out var bytes))
                {
                    logger.LogDebug("Extracted size from MyAnonamouse description bytes: {Bytes}", bytes);
                    return bytes;
                }

                var sizeValue = match.Groups[1].Value.Replace(",", "");
                var unit = match.Groups[2].Value.ToUpper();
                if (double.TryParse(sizeValue, out var value))
                {
                    var result = ParseDecimalUnit(value, unit, binary: true);
                    logger.LogDebug("Extracted size from MyAnonamouse description formatted: {Value} {Unit} = {Result} bytes", value, unit, result);
                    return result;
                }
            }

            match = Regex.Match(description, @"Total Size\s*:\s*([\d\.,]+)\s*(MB|GB|KB|B)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var sizeValue = match.Groups[1].Value.Replace(",", "");
                var unit = match.Groups[2].Value.ToUpper();
                if (double.TryParse(sizeValue, out var value))
                {
                    var result = ParseDecimalUnit(value, unit, binary: true);
                    logger.LogDebug("Extracted size from MyAnonamouse description (no bytes): {Value} {Unit} = {Result} bytes", value, unit, result);
                    return result;
                }
            }

            logger.LogDebug("No size found in MyAnonamouse description");
            return 0;
        }

        public static long ParseSizeString(string sizeStr, ILogger logger)
        {
            if (string.IsNullOrEmpty(sizeStr))
                return 0;

            sizeStr = sizeStr.Replace(",", "").Trim();

            if (long.TryParse(sizeStr, out var bytes))
                return bytes;

            var match = Regex.Match(sizeStr, @"^([\d\.]+)\s*(KiB|MiB|GiB|TiB|KB|MB|GB|TB|B)$", RegexOptions.IgnoreCase);
            if (match.Success &&
                double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
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

            logger.LogWarning("Unable to parse size string: '{SizeStr}'", sizeStr);
            return 0;
        }

        private static long ParseDecimalUnit(double value, string unit, bool binary)
        {
            var multiplier = binary ? 1024L : 1000L;
            return unit switch
            {
                "B" => (long)value,
                "KB" => (long)(value * multiplier),
                "MB" => (long)(value * multiplier * multiplier),
                "GB" => (long)(value * multiplier * multiplier * multiplier),
                _ => (long)value
            };
        }
    }
}
