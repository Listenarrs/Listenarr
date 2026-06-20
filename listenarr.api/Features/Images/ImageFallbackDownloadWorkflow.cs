/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */


namespace Listenarr.Api.Features.Images
{
    internal sealed class ImageFallbackDownloadWorkflow
    {
        private readonly IImageCacheService _imageCacheService;
        private readonly ILogger _logger;

        public ImageFallbackDownloadWorkflow(IImageCacheService imageCacheService, ILogger logger)
        {
            _imageCacheService = imageCacheService;
            _logger = logger;
        }

        public async Task<string?> TryDownloadFirstCachedAsync(string identifier, IEnumerable<string> candidateUrls)
        {
            foreach (var urlCandidate in candidateUrls)
            {
                _logger.LogInformation("Attempting metadata-driven image download for identifier {Identifier} from {Url}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(urlCandidate));
                try
                {
                    _logger.LogDebug("Calling DownloadAndCacheImageAsync for {Identifier} from {Url}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(urlCandidate));
                    var downloaded = await _imageCacheService.DownloadAndCacheImageAsync(urlCandidate, identifier);
                    if (!string.IsNullOrWhiteSpace(downloaded))
                    {
                        _logger.LogInformation("Downloaded metadata image for identifier: {Identifier}", LogRedaction.SanitizeText(identifier));
                        var relativePath = await _imageCacheService.GetCachedImagePathAsync(identifier);
                        if (!string.IsNullOrWhiteSpace(relativePath))
                        {
                            return relativePath;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ImageIdentifierHelper.IsRecoverableImageLookupException(ex))
                {
                    _logger.LogWarning(ex, "Failed to download metadata-driven image for {Identifier} from {Url}", LogRedaction.SanitizeText(identifier), LogRedaction.SanitizeText(urlCandidate));
                }
            }

            return null;
        }
    }
}
