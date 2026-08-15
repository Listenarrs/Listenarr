/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Scanning
{
    [Trait("Name", "ScanPathPlannerTests")]
    [Trait("Category", "Infrastructure")]
    public class ScanPathPlannerTests : BaseTests
    {
        [Fact]
        public void CalculateBasePath_DoesNotClimbAboveNestedBookBoundary()
        {
            var root = Path.Join(
                Path.GetTempPath(),
                "listenarr-scan-path-" + Guid.NewGuid().ToString("N"));
            var book = Path.Join(root, "Author", "Book");
            var first = Path.Join(book, "CD1", "01.mp3");
            var second = Path.Join(book, "CD2", "02.mp3");
            Directory.CreateDirectory(Path.GetDirectoryName(first)!);
            Directory.CreateDirectory(Path.GetDirectoryName(second)!);
            File.WriteAllText(first, "audio");
            File.WriteAllText(second, "audio");

            try
            {
                var result = ScanPathPlanner.CalculateBasePath(
                    [first, second],
                    FileSystemPathSemantics.CurrentHostDefault);

                Assert.Equal(book, result);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void CalculateBasePath_DedupesCaseOnlyDirectoriesUsingResolvedSemantics()
        {
            var root = Path.Join(Path.GetTempPath(), "listenarr-scan-path-" + Guid.NewGuid().ToString("N"));
            var upper = Path.Join(root, "Book", "Track01.m4b");
            var lower = Path.Join(root, "book", "Track02.m4b");

            var insensitive = new FileSystemPathSemantics(
                FileSystemPathSemantics.CurrentHostDefault.Syntax,
                FileSystemCaseSensitivity.Insensitive);
            var sensitive = insensitive with
            {
                CaseSensitivity = FileSystemCaseSensitivity.Sensitive
            };

            var insensitiveBasePath = ScanPathPlanner.CalculateBasePath(
                [upper, lower],
                insensitive);
            var sensitiveBasePath = ScanPathPlanner.CalculateBasePath(
                [upper, lower],
                sensitive);

            Assert.True(FileSystemPathIdentity.AreEquivalent(
                Path.Join(root, "Book"),
                insensitiveBasePath,
                insensitive));
            Assert.True(FileSystemPathIdentity.AreEquivalent(
                root,
                sensitiveBasePath,
                sensitive));
        }
    }
}
