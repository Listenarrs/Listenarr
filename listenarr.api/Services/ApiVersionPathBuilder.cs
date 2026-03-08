using System;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Listenarr.Api.Services
{
    /// <summary>
    /// Utility methods for building versioned API paths from request context.
    /// Falls back to v1 when no explicit version can be resolved.
    /// </summary>
    public static class ApiVersionPathBuilder
    {
        private const string DefaultApiVersion = "1";
        private static readonly Regex ApiVersionFromPathRegex = new(@"^/api/v(?<version>\d+(?:\.\d+)?)(?:/|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex LeadingApiPrefixRegex = new(@"^/api(?:/v\d+(?:\.\d+)?)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string NormalizeApiVersionString(string? version)
        {
            if (string.IsNullOrWhiteSpace(version)) return DefaultApiVersion;

            var trimmed = version.Trim();
            if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
            {
                trimmed = trimmed[1..];
            }

            return TryNormalizeNumericApiVersion(trimmed, out var normalized) ? normalized : DefaultApiVersion;
        }

        public static string ResolveApiVersion(HttpContext? context, string? fallbackVersion = null)
        {
            var fallback = NormalizeApiVersionString(fallbackVersion);

            try
            {
                if (context?.Request?.RouteValues?.TryGetValue("version", out var routeVersionObj) is true)
                {
                    var routeVersion = routeVersionObj?.ToString();
                    if (!string.IsNullOrWhiteSpace(routeVersion))
                    {
                        return NormalizeApiVersionString(routeVersion);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"ApiVersionPathBuilder.ResolveApiVersion route parse failed: {ex.Message}");
            }

            try
            {
                var path = context?.Request?.Path.Value;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var match = ApiVersionFromPathRegex.Match(path);
                    if (match.Success)
                    {
                        var parsed = match.Groups["version"].Value;
                        if (!string.IsNullOrWhiteSpace(parsed))
                        {
                            return NormalizeApiVersionString(parsed);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"ApiVersionPathBuilder.ResolveApiVersion path parse failed: {ex.Message}");
            }

            return fallback;
        }

        public static string GetApiVersionSegment(HttpContext? context, string? fallbackVersion = null)
            => $"v{ResolveApiVersion(context, fallbackVersion)}";

        public static string BuildApiPath(string endpoint, HttpContext? context = null, string? fallbackVersion = null)
        {
            var normalizedEndpoint = NormalizeEndpoint(endpoint);
            return $"/api/{GetApiVersionSegment(context, fallbackVersion)}{normalizedEndpoint}";
        }

        public static string BuildImagePath(string identifier, HttpContext? context = null, string? fallbackVersion = null, string? sourceUrl = null)
        {
            var encodedIdentifier = Uri.EscapeDataString(identifier ?? string.Empty);
            var path = BuildApiPath($"/images/{encodedIdentifier}", context, fallbackVersion);
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

        private static bool TryNormalizeNumericApiVersion(string value, out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var segments = new System.Collections.Generic.List<string>();
            var segmentStart = 0;

            for (var i = 0; i <= value.Length; i++)
            {
                if (i < value.Length && value[i] != '.')
                {
                    continue;
                }

                var segmentLength = i - segmentStart;
                if (segmentLength <= 0)
                {
                    return false;
                }

                var segment = value.Substring(segmentStart, segmentLength);
                for (var j = 0; j < segment.Length; j++)
                {
                    if (!char.IsDigit(segment[j]))
                    {
                        return false;
                    }
                }

                var nonZeroIndex = 0;
                while (nonZeroIndex < segment.Length - 1 && segment[nonZeroIndex] == '0')
                {
                    nonZeroIndex++;
                }

                segments.Add(segment[nonZeroIndex..]);
                segmentStart = i + 1;
            }

            while (segments.Count > 1 && segments[^1] == "0")
            {
                segments.RemoveAt(segments.Count - 1);
            }

            normalized = string.Join('.', segments);
            return !string.IsNullOrWhiteSpace(normalized);
        }
    }
}
