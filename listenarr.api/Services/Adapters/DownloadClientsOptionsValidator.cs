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
using Microsoft.Extensions.Options;

namespace Listenarr.Api.Services.Adapters
{
    /// <summary>
    /// Validates DownloadClientsOptions at startup to surface configuration problems early.
    /// Ensures each configured client has a Type and Host/Port at minimum.
    /// </summary>
    public class DownloadClientsOptionsValidator : IValidateOptions<DownloadClientsOptions>
    {
        public ValidateOptionsResult Validate(string? name, DownloadClientsOptions options)
        {
            if (options == null) return ValidateOptionsResult.Fail("DownloadClients configuration section is missing.");

            var errors = new List<string>();

            foreach (var kv in options.Clients)
            {
                var key = kv.Key ?? "<unknown>";
                var c = kv.Value;
                if (c == null)
                {
                    errors.Add($"Download client '{key}' is null.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(c.Type))
                    errors.Add($"Download client '{key}' type is not configured (Type).");

                if (string.IsNullOrWhiteSpace(c.Host))
                    errors.Add($"Download client '{key}' host is not configured (Host).");

                if (c.Port <= 0 || c.Port > 65535)
                    errors.Add($"Download client '{key}' has invalid Port value: {c.Port}.");

                // Additional checks: but keep light to avoid blocking dynamic setups.
            }

            if (errors.Count > 0) return ValidateOptionsResult.Fail(errors);
            return ValidateOptionsResult.Success;
        }
    }
}
