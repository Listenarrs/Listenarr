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

using System.Globalization;
using System.Xml.Linq;

namespace Listenarr.Infrastructure.DownloadClients.Nzbget;

internal static class NzbgetConfigValidator
{
    public static (bool Success, string Message) ValidateKeepHistory(XElement configResult)
    {
        var keepHistory = FindConfigValue(configResult, "KeepHistory");
        if (keepHistory == null)
        {
            return (
                false,
                "NZBGet: KeepHistory setting was not found. Listenarr requires NZBGet history so completed downloads can be imported reliably.");
        }

        var normalizedKeepHistory = keepHistory.Trim();
        if (!int.TryParse(normalizedKeepHistory, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            return (
                false,
                "NZBGet: KeepHistory setting is invalid. Listenarr requires KeepHistory to be greater than 0.");
        }

        if (value <= 0)
        {
            return (
                false,
                "NZBGet: KeepHistory must be greater than 0 so Listenarr can read completed download history and resolve import paths.");
        }

        return (true, "NZBGet: connected");
    }

    private static string? FindConfigValue(XElement configResult, string name)
    {
        var entries = configResult
            .Element("array")?
            .Element("data")?
            .Elements("value") ?? [];

        foreach (var entry in entries)
        {
            var members = ReadStructMembers(entry.Element("struct"));
            if (members.TryGetValue("Name", out var entryName) &&
                string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase))
            {
                return members.GetValueOrDefault("Value");
            }
        }

        return null;
    }

    private static IReadOnlyDictionary<string, string> ReadStructMembers(XElement? structElement)
    {
        if (structElement == null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return structElement.Elements("member").ToDictionary(
            member => member.Element("name")?.Value ?? string.Empty,
            member => member.Element("value")?.Elements().FirstOrDefault()?.Value ?? string.Empty,
            StringComparer.OrdinalIgnoreCase);
    }
}
