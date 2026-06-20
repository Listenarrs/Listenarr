/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using System.Text.RegularExpressions;

namespace Listenarr.Infrastructure.SystemDiagnostics.Diagnostics
{
    internal static class SystemLogParser
    {
        public static LogEntry? ParseLogLine(string line)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(line))
                    return null;

                var match = Regex.Match(
                    line,
                    @"^(\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d{3}\s+[+-]\d{2}:\d{2})\s+\[(\w{3})\]\s+(.+)$"
                );

                if (match.Success)
                {
                    var timestampStr = match.Groups[1].Value;
                    var level = match.Groups[2].Value.ToUpperInvariant();
                    var message = match.Groups[3].Value;

                    if (!DateTime.TryParse(timestampStr, out var timestamp))
                    {
                        timestamp = DateTime.UtcNow;
                    }

                    var mappedLevel = level switch
                    {
                        "VRB" => "Debug",
                        "DBG" => "Debug",
                        "INF" => "Info",
                        "WRN" => "Warning",
                        "ERR" => "Error",
                        "FTL" => "Error",
                        _ => "Info"
                    };

                    return new LogEntry
                    {
                        Timestamp = timestamp,
                        Level = mappedLevel,
                        Message = message,
                        Source = "Application"
                    };
                }

                return new LogEntry
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Info",
                    Message = line,
                    Source = "Application"
                };
            }
            catch (Exception caughtEx) when (caughtEx is not OperationCanceledException && caughtEx is not OutOfMemoryException && caughtEx is not StackOverflowException)
            {
                return null;
            }
        }
    }
}
