/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Images
{
    internal sealed class ImageResponseBuilder
    {
        private readonly ImagePlaceholderResolver _placeholderResolver;
        private readonly ILogger _logger;
        private readonly string _contentRootPath;

        public ImageResponseBuilder(ImagePlaceholderResolver placeholderResolver, ILogger logger, string contentRootPath)
        {
            _placeholderResolver = placeholderResolver;
            _logger = logger;
            _contentRootPath = contentRootPath;
        }

        public IActionResult CreateCachedImageResult(
            IHeaderDictionary headers,
            string identifier,
            string relativePath,
            string fullPath)
        {
            var extension = Path.GetExtension(fullPath).ToLowerInvariant();
            var contentType = extension switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".svg" => "image/svg+xml",
                _ => "application/octet-stream"
            };

            _logger.LogInformation("Serving cached image for identifier: {Identifier}, path: {Path}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(relativePath));
            headers["Cache-Control"] = "private, max-age=3600";
            return new PhysicalFileResult(fullPath, contentType)
            {
                EnableRangeProcessing = true
            };
        }

        public IActionResult CreatePlaceholderResult(
            IHeaderDictionary headers,
            PathString requestPath,
            string logContext,
            string? logValue,
            string notFoundMessage)
        {
            try
            {
                var placeholderPath = _placeholderResolver.ResolvePlaceholderPath(_contentRootPath);
                if (!string.IsNullOrWhiteSpace(placeholderPath))
                {
                    _logger.LogInformation("Serving placeholder image for {LogContext}: {LogValue}", LogRedaction.SanitizeText(logContext), LogRedaction.SanitizeText(logValue));
                    headers["Cache-Control"] = "public, max-age=300";
                    return new PhysicalFileResult(placeholderPath, "image/svg+xml");
                }
            }
            catch (Exception ex) when (IsRecoverableImageLookupException(ex))
            {
                _logger.LogDebug(ex, "Failed to resolve placeholder for {LogContext}: {LogValue}", LogRedaction.SanitizeText(logContext), LogRedaction.SanitizeText(logValue));
            }

            if (!string.Equals(requestPath.Value, "/placeholder.svg", StringComparison.OrdinalIgnoreCase))
            {
                headers["Cache-Control"] = "public, max-age=300";
                return new RedirectResult("/placeholder.svg");
            }

            headers["Cache-Control"] = "public, max-age=300";
            return new NotFoundObjectResult(new { message = notFoundMessage });
        }

        private static bool IsRecoverableImageLookupException(Exception ex)
        {
            return ex is System.IO.IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or ArgumentException
                or FormatException
                or UriFormatException
                or System.Net.Http.HttpRequestException
                or System.Text.Json.JsonException;
        }
    }
}
