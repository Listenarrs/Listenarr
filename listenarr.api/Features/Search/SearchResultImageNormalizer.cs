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


namespace Listenarr.Api.Features.Search
{
    internal static class SearchResultImageNormalizer
    {
        public static async Task NormalizeMetadataResultsAsync(
            IEnumerable<MetadataSearchResult>? results,
            IImageCacheService? imageCacheService,
            HttpContext httpContext,
            Microsoft.Extensions.Logging.ILogger logger,
            string logLabel,
            bool setApiPathWhenNoExternalImage)
        {
            if (imageCacheService == null || results == null)
            {
                return;
            }

            foreach (var result in results)
            {
                await NormalizeMetadataResultAsync(
                    result,
                    imageCacheService,
                    httpContext,
                    logger,
                    logLabel,
                    setApiPathWhenNoExternalImage);
            }
        }

        public static async Task NormalizeMetadataResultAsync(
            MetadataSearchResult? result,
            IImageCacheService? imageCacheService,
            HttpContext httpContext,
            Microsoft.Extensions.Logging.ILogger logger,
            string logLabel,
            bool setApiPathWhenNoExternalImage)
        {
            if (imageCacheService == null || result == null || string.IsNullOrWhiteSpace(result.Asin))
            {
                return;
            }

            try
            {
                var cached = await imageCacheService.GetCachedImagePathAsync(result.Asin);
                if (!string.IsNullOrWhiteSpace(cached))
                {
                    result.ImageUrl = BuildApiImagePath(result.Asin, httpContext);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(result.ImageUrl) && IsExternalHttpUrl(result.ImageUrl))
                {
                    var downloaded = await imageCacheService.DownloadAndCacheImageAsync(result.ImageUrl, result.Asin);
                    result.ImageUrl = !string.IsNullOrWhiteSpace(downloaded)
                        ? BuildApiImagePath(result.Asin, httpContext)
                        : BuildApiImagePath(result.Asin, httpContext, result.ImageUrl);
                }
                else if (setApiPathWhenNoExternalImage)
                {
                    result.ImageUrl = BuildApiImagePath(result.Asin, httpContext);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Failed to normalize image for {LogLabel} ASIN {Asin}", logLabel, result.Asin);
            }
        }

        private static bool IsExternalHttpUrl(string url)
        {
            return url.StartsWith("http://") || url.StartsWith("https://");
        }

        private static string BuildApiImagePath(string identifier, HttpContext httpContext, string? sourceUrl = null)
        {
            return HttpApiVersionUtils.BuildImagePath(identifier, httpContext, sourceUrl: sourceUrl);
        }
    }
}
