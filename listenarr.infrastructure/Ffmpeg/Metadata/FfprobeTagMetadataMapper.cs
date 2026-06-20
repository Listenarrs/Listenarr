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

namespace Listenarr.Infrastructure.Ffmpeg.Metadata
{
    internal static class FfprobeTagMetadataMapper
    {
        public static void Apply(AudioMetadata metadata, JsonElement tags)
        {
            metadata.Title = FirstNonEmpty(metadata.Title, GetTag(tags, "title", "TITLE"));
            metadata.Artist = FirstNonEmpty(metadata.Artist, GetTag(tags, "artist", "ARTIST"));
            metadata.Album = FirstNonEmpty(metadata.Album, GetTag(tags, "album", "ALBUM"));
            metadata.AlbumArtist = FirstNonEmpty(metadata.AlbumArtist, GetTag(tags, "album_artist", "ALBUM_ARTIST", "album artist"));

            metadata.TrackNumber ??= ParseNumericTag(tags, "track", "TRACK", "tracknumber", "TRACKNUMBER");
            metadata.DiscNumber ??= ParseNumericTag(tags, "disc", "DISC", "discnumber", "DISCNUMBER");
            metadata.Year ??= ParseNumericTag(tags, "date", "DATE", "year", "YEAR");
        }

        private static string FirstNonEmpty(params string?[] candidates)
        {
            foreach (var candidate in candidates.Where(candidate => !string.IsNullOrWhiteSpace(candidate)))
            {
                return candidate!;
            }

            return string.Empty;
        }

        private static string? GetTag(JsonElement tags, params string[] names)
        {
            return names
                .Select(name => TryGetTagValue(tags, name, out var value) ? value : null)
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
                ?.Trim();
        }

        private static int? ParseNumericTag(JsonElement tags, params string[] names)
        {
            var raw = GetTag(tags, names);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var token = raw.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? raw;
            var match = System.Text.RegularExpressions.Regex.Match(token, @"\d+");
            return match.Success && int.TryParse(match.Value, out var parsed) ? parsed : null;
        }

        private static bool TryGetTagValue(JsonElement tags, string name, out string? value)
        {
            if (tags.TryGetProperty(name, out var direct) && direct.ValueKind == JsonValueKind.String)
            {
                value = direct.GetString();
                return true;
            }

            foreach (var property in tags.EnumerateObject())
            {
                if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    value = property.Value.GetString();
                    return true;
                }
            }

            value = null;
            return false;
        }
    }
}
