using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Listenarr.Api.Services
{
    /// <summary>
    /// Shared helper utilities for qBittorrent operations.
    /// Centralizes common functionality to eliminate code duplication across adapters and services.
    /// </summary>
    internal static class QBittorrentHelpers
    {
        /// <summary>
        /// Builds a URL parameter string for qBittorrent category filtering.
        /// Extracts the category from client settings and returns a properly formatted parameter.
        /// </summary>
        /// <param name="settings">The client settings dictionary containing optional "category" key</param>
        /// <param name="parameterPrefix">The parameter prefix ("?" for first param, "&amp;" for subsequent params)</param>
        /// <returns>A URL parameter string like "?category=audiobooks" or "&amp;category=audiobooks", or empty string if no category is configured</returns>
        public static string BuildCategoryParameter(Dictionary<string, object> settings, string parameterPrefix)
        {
            if (settings == null || settings.Count == 0)
                return string.Empty;

            if (!settings.TryGetValue("category", out var categoryObj) || categoryObj == null)
                return string.Empty;

            var category = categoryObj.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(category))
                return string.Empty;

            // Properly encode the category value for URL safety
            var encodedCategory = Uri.EscapeDataString(category);
            return $"{parameterPrefix}category={encodedCategory}";
        }

        /// <summary>
        /// Logs information about category filtering if a category is configured.
        /// Provides visibility into filtering behavior for debugging and monitoring.
        /// </summary>
        /// <param name="logger">The logger instance to use for logging</param>
        /// <param name="category">The category value to log (can be null or empty)</param>
        public static void LogCategoryFiltering(ILogger logger, string? category)
        {
            if (logger == null)
                return;

            if (string.IsNullOrWhiteSpace(category))
            {
                logger.LogDebug("qBittorrent category filtering not configured - will fetch all torrents");
            }
            else
            {
                logger.LogInformation("Fetching qBittorrent queue filtered by category: {Category}", category);
            }
        }
    }
}
