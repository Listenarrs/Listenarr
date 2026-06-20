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

namespace Listenarr.Api.Features.Images
{
    internal sealed class ImagePathValidator
    {
        private readonly string _contentRootPath;

        public ImagePathValidator(string contentRootPath)
        {
            _contentRootPath = contentRootPath;
        }

        public string ResolvePathWithOptionalBase(string candidatePath)
        {
            return FileUtils.CombineWithOptionalBase(_contentRootPath, candidatePath.Trim());
        }

        public bool IsInsidePermittedImageRoot(string fullPath)
        {
            var candidateFull = Path.GetFullPath(fullPath);
            return GetPermittedImageRoots().Any(root => IsSamePathOrInside(candidateFull, root));
        }

        private IEnumerable<string> GetPermittedImageRoots()
        {
            yield return Path.GetFullPath(FileUtils.CombineRelativePath(_contentRootPath, "cache", "images"));
            yield return Path.GetFullPath(FileUtils.CombineRelativePath(_contentRootPath, "config", "cache", "images"));
            yield return Path.GetFullPath(FileUtils.CombineRelativePath(_contentRootPath, "wwwroot"));
        }

        private static bool IsSamePathOrInside(string candidateFullPath, string rootFullPath)
        {
            var relativePath = Path.GetRelativePath(rootFullPath, candidateFullPath);
            return relativePath == "." ||
                (!relativePath.StartsWith("..", StringComparison.Ordinal) &&
                 !Path.IsPathRooted(relativePath));
        }
    }
}
