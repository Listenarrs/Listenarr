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

using Listenarr.Domain.Common;

namespace Listenarr.Api.Features.Images;

public sealed class ImagePlaceholderResolver
{
    private readonly ILogger<ImagePlaceholderResolver> _logger;

    public ImagePlaceholderResolver(ILogger<ImagePlaceholderResolver> logger)
    {
        _logger = logger;
    }

    public string? ResolvePlaceholderPath(string effectiveContentRootPath)
    {
        foreach (var candidate in EnumeratePlaceholderCandidates(effectiveContentRootPath))
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                var fullPath = Path.GetFullPath(candidate);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Failed probing placeholder candidate path {Path}", candidate);
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumeratePlaceholderCandidates(string effectiveContentRootPath)
    {
        var baseDirectories = new[]
        {
            effectiveContentRootPath,
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory()
        }
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(path =>
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception ex) when (
                ex is ArgumentException or
                ArgumentNullException or
                PathTooLongException or
                NotSupportedException or
                System.Security.SecurityException)
            {
                return path;
            }
        })
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

        foreach (var baseDirectory in baseDirectories)
        {
            DirectoryInfo? current = null;
            try
            {
                current = new DirectoryInfo(baseDirectory);
            }
            catch (Exception ex) when (
                ex is ArgumentException or
                ArgumentNullException or
                PathTooLongException or
                NotSupportedException or
                System.Security.SecurityException)
            {
                current = null;
            }

            var depth = 0;
            while (current != null && depth++ < 8)
            {
                yield return FileUtils.CombineRelativePath(current.FullName, "wwwroot", "placeholder.svg");
                yield return FileUtils.CombineRelativePath(current.FullName, "fe", "public", "placeholder.svg");
                yield return FileUtils.CombineRelativePath(current.FullName, "listenarr.api", "wwwroot", "placeholder.svg");

                current = current.Parent;
            }
        }
    }
}
