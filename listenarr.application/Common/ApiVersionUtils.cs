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
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Common
{
    /// <summary>
    /// Utility methods for building versioned API paths from request context.
    /// Falls back to v1 when no explicit version can be resolved.
    /// </summary>
    public static class ApiVersionUtils
    {
        private static readonly Regex ApiVersionFromPathRegex = new(@"^/api/v(?<version>\d+(?:\.\d+)?)(?:/|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex LeadingApiPrefixRegex = new(@"^/api(?:/v\d+(?:\.\d+)?)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string ResolveApiVersion(string? path = null, string? fallbackVersion = null, ILogger? logger = null)
        {
            var fallback = ApiVersionNormalizer.NormalizeOrDefault(fallbackVersion);

            try
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var match = ApiVersionFromPathRegex.Match(path);
                    if (match.Success)
                    {
                        var parsed = match.Groups["version"].Value;
                        if (!string.IsNullOrWhiteSpace(parsed))
                        {
                            return ApiVersionNormalizer.NormalizeOrDefault(parsed);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "API version path parse failed.");
            }

            return fallback;
        }

        public static string GetApiVersionSegment(string? path = null, string? fallbackVersion = null)
            => $"v{ResolveApiVersion(path, fallbackVersion)}";

        public static string BuildApiPath(string endpoint, string? requestPath = null, string? fallbackVersion = null)
        {
            var normalizedEndpoint = NormalizeEndpoint(endpoint);
            return $"/api/{GetApiVersionSegment(requestPath, fallbackVersion)}{normalizedEndpoint}";
        }

        public static string BuildImagePath(string identifier, string? requestPath = null, string? fallbackVersion = null, string? sourceUrl = null)
        {
            var encodedIdentifier = Uri.EscapeDataString(identifier ?? string.Empty);
            var path = BuildApiPath($"/images/{encodedIdentifier}", requestPath, fallbackVersion);
            if (string.IsNullOrWhiteSpace(sourceUrl)) return path;
            return $"{path}?url={Uri.EscapeDataString(sourceUrl)}";
        }

        private static string NormalizeEndpoint(string endpoint)
        {
            var normalized = string.IsNullOrWhiteSpace(endpoint) ? "/" : endpoint.Trim();
            if (!normalized.StartsWith('/')) normalized = "/" + normalized;

            normalized = LeadingApiPrefixRegex.Replace(normalized, string.Empty);
            if (string.IsNullOrWhiteSpace(normalized)) return "/";
            return normalized.StartsWith('/') ? normalized : "/" + normalized;
        }

    }
}
