/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Infrastructure.DownloadClients.Common
{
    internal static class DownloadClientTypes
    {
        public const string Qbittorrent = "qbittorrent";
        public const string Transmission = "transmission";
        public const string Sabnzbd = "sabnzbd";
        public const string Nzbget = "nzbget";
    }
}
