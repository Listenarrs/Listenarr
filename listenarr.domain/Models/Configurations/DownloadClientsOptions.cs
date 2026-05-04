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
namespace Listenarr.Domain.Models.Configurations
{
    // Options for a single download client instance
    public class DownloadClientOptions
    {
        public string? Id { get; set; }          // logical id, e.g. "home-qbit"
        public string? Type { get; set; }        // client type, e.g. "qbittorrent", "transmission"
        public string? Host { get; set; }
        public int Port { get; set; }
        public bool UseSSL { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? ApiKey { get; set; }
        public string? DownloadPath { get; set; }
    }

    // Top-level binding for multiple download clients
    public class DownloadClientsOptions
    {
        // key = logical id or name from configuration, value = options
        public Dictionary<string, DownloadClientOptions> Clients { get; set; } = new();
    }
}
