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

namespace Listenarr.Application.Audiobooks.Playback;

public static class AudioContentType
{
    public static string ForContainer(string? container) =>
        (container ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant() switch
        {
            "m4b" or "m4a" or "mp4" or "aac" => "audio/mp4",
            "mp3" => "audio/mpeg",
            "ogg" or "oga" or "opus" => "audio/ogg",
            "flac" => "audio/flac",
            "wav" => "audio/wav",
            _ => "application/octet-stream",
        };
}
