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

namespace Listenarr.Api.Features.Prowlarr
{
    internal static class ProwlarrImportUrlPlanner
    {
        public static string BuildBaseUrl(string rawUrl, int? port)
        {
            var trimmed = rawUrl.Trim();
            if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = "http://" + trimmed;
            }

            var builder = new UriBuilder(trimmed);
            if (port.HasValue && port.Value > 0)
            {
                builder.Port = port.Value;
            }

            return builder.Uri.ToString().TrimEnd('/');
        }

        public static string BuildProxyUrl(string baseUrl, int indexerId)
        {
            var root = baseUrl.TrimEnd('/');
            return $"{root}/{indexerId}/api";
        }

        public static string NormalizeProxyUrl(string? rawUrl)
        {
            if (string.IsNullOrWhiteSpace(rawUrl)) return rawUrl ?? string.Empty;
            return rawUrl.Trim().TrimEnd('/');
        }
    }
}
