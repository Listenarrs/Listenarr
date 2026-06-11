/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Domain.Common;

namespace Listenarr.Api.Common
{
    public static class HttpApiVersionUtils
    {
        public static string ResolveApiVersion(HttpContext? context, string? fallbackVersion = null, ILogger? logger = null)
        {
            try
            {
                if (context?.Request?.RouteValues?.TryGetValue("version", out var routeVersionObj) is true)
                {
                    var routeVersion = routeVersionObj?.ToString();
                    if (!string.IsNullOrWhiteSpace(routeVersion))
                    {
                        return ApiVersionNormalizer.NormalizeOrDefault(routeVersion);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "API version route parse failed.");
            }

            return Listenarr.Application.Common.ApiVersionUtils.ResolveApiVersion(context?.Request?.Path.Value, fallbackVersion, logger);
        }

        public static string GetApiVersionSegment(HttpContext? context, string? fallbackVersion = null)
            => $"v{ResolveApiVersion(context, fallbackVersion)}";

        public static string BuildApiPath(string endpoint, HttpContext? context, string? fallbackVersion = null)
            => Listenarr.Application.Common.ApiVersionUtils.BuildApiPath(endpoint, context?.Request?.Path.Value, fallbackVersion);

        public static string BuildImagePath(string identifier, HttpContext? context, string? fallbackVersion = null, string? sourceUrl = null)
            => Listenarr.Application.Common.ApiVersionUtils.BuildImagePath(identifier, context?.Request?.Path.Value, fallbackVersion, sourceUrl);
    }
}
