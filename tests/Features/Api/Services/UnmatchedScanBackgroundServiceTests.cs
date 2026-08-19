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

namespace Listenarr.Tests.Features.Api.Services
{
    public class UnmatchedScanBackgroundServiceTests
    {
        [Fact]
        public void BuildGroupedFilesForFolder_MergesForewordIntoSingleBookGroup()
        {
            var folder = @"D:\test\Jack of Shadows - Roger Zelazny (narrated by Eric Jason Martin)";
            var files = new[]
            {
                Path.Join(folder, "(Foreword by Joe Haldeman).mp3"),
                Path.Join(folder, "Chapter 01.mp3"),
                Path.Join(folder, "Chapter 02.mp3")
            };

            var groups = UnmatchedScanBackgroundService.BuildGroupedFilesForFolder(
                files,
                folder,
                FileSystemPathSemantics.CurrentHostDefault);

            var group = Assert.Single(groups);
            Assert.Equal(3, group.Count);
            Assert.Contains(Path.Join(folder, "(Foreword by Joe Haldeman).mp3"), group);
            Assert.Contains(Path.Join(folder, "Chapter 01.mp3"), group);
            Assert.Contains(Path.Join(folder, "Chapter 02.mp3"), group);
        }

        [Fact]
        public void BuildGroupedFilesForFolder_KeepsDistinctTitlesSeparated()
        {
            var folder = @"D:\test\Roger Zelazny";
            var files = new[]
            {
                Path.Join(folder, "Jack of Shadows.mp3"),
                Path.Join(folder, "Lord of Light.mp3")
            };

            var groups = UnmatchedScanBackgroundService.BuildGroupedFilesForFolder(
                files,
                folder,
                FileSystemPathSemantics.CurrentHostDefault);

            Assert.Equal(2, groups.Count);
            Assert.Contains(groups, group => group.Single() == Path.Join(folder, "Jack of Shadows.mp3"));
            Assert.Contains(groups, group => group.Single() == Path.Join(folder, "Lord of Light.mp3"));
        }

        [Fact]
        public void BuildGroupedFilesForFolder_UsesEmbeddedTitleAndAuthorToMergeMixedFolderTracks()
        {
            var folder = @"D:\test\test-import";
            var foreword = Path.Join(folder, "(Foreword by Joe Haldeman).mp3");
            var chapter1 = Path.Join(folder, "Chapter 01.mp3");
            var alchemised = Path.Join(folder, "Alchemised (Spanish Edition)_ No queda nadie a quien salvar.m4b");
            var files = new[]
            {
                foreword,
                chapter1,
                alchemised
            };

            var embeddedTags = new Dictionary<string, PathParsedMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                [foreword] = new() { Title = "Jack of Shadows", Author = "Roger Zelazny" },
                [chapter1] = new() { Title = "Jack of Shadows", Author = "Roger Zelazny" },
                [alchemised] = new() { Title = "Alchemised (Spanish Edition)", Author = "SenLinYu" }
            };

            var groups = UnmatchedScanBackgroundService.BuildGroupedFilesForFolder(
                files,
                folder,
                FileSystemPathSemantics.CurrentHostDefault,
                embeddedTags);

            Assert.Equal(2, groups.Count);
            Assert.Contains(groups, group => group.Count == 2 && group.Contains(foreword) && group.Contains(chapter1));
            Assert.Contains(groups, group => group.Count == 1 && group.Contains(alchemised));
        }

        [Fact]
        public void BuildGroupedFilesForFolder_UsesAuthorToKeepSameTitleSeparated()
        {
            var folder = @"D:\test\same-title";
            var fileA = Path.Join(folder, "Book A.m4b");
            var fileB = Path.Join(folder, "Book B.m4b");
            var files = new[] { fileA, fileB };

            var embeddedTags = new Dictionary<string, PathParsedMetadata>(StringComparer.OrdinalIgnoreCase)
            {
                [fileA] = new() { Title = "Shared Title", Author = "Roger Zelazny" },
                [fileB] = new() { Title = "Shared Title", Author = "SenLinYu" }
            };

            var groups = UnmatchedScanBackgroundService.BuildGroupedFilesForFolder(
                files,
                folder,
                FileSystemPathSemantics.CurrentHostDefault,
                embeddedTags);

            Assert.Equal(2, groups.Count);
            Assert.Contains(groups, group => group.Single() == fileA);
            Assert.Contains(groups, group => group.Single() == fileB);
        }

        [Fact]
        public void BuildGroupedFilesForFolder_GroupsChapterFilesIndexedAsNumberOfTotal()
        {
            // A chapter-per-file rip that numbers its parts "N of M". The index carries its
            // own total, so it describes a set rather than distinct works, and every file
            // belongs to the one book the folder names.
            var folder = @"D:\test\Jack of Shadows";
            var files = Enumerable.Range(1, 4)
                .Select(n => Path.Join(folder, $"Jack of Shadows {n:000} of 004.mp3"))
                .ToArray();

            var groups = UnmatchedScanBackgroundService.BuildGroupedFilesForFolder(
                files,
                folder,
                FileSystemPathSemantics.CurrentHostDefault);

            var group = Assert.Single(groups);
            Assert.Equal(4, group.Count);
        }

        [Fact]
        public void BuildGroupedFilesForFolder_GroupsBareNumberOfTotalFilenames()
        {
            // The same convention with no title in the filename at all. Stripping the index
            // empties the stem, which is what lets the folder-name fallback gather them.
            var folder = @"D:\test\Jack of Shadows";
            var files = Enumerable.Range(1, 3)
                .Select(n => Path.Join(folder, $"{n:000} of 003.mp3"))
                .ToArray();

            var groups = UnmatchedScanBackgroundService.BuildGroupedFilesForFolder(
                files,
                folder,
                FileSystemPathSemantics.CurrentHostDefault);

            var group = Assert.Single(groups);
            Assert.Equal(3, group.Count);
        }

        [Fact]
        public void BuildGroupedFilesForFolder_KeepsTitlesWhoseOwnWordsReadLikeAnIndex()
        {
            // "of" between two words is not an index, and a trailing number is not a total.
            // Two separate works in one author folder must stay separate.
            var folder = @"D:\test\Roger Zelazny";
            var jack = Path.Join(folder, "Jack of Shadows.mp3");
            var nine = Path.Join(folder, "Nine Princes in Amber 2.mp3");

            var groups = UnmatchedScanBackgroundService.BuildGroupedFilesForFolder(
                new[] { jack, nine },
                folder,
                FileSystemPathSemantics.CurrentHostDefault);

            Assert.Equal(2, groups.Count);
            Assert.Contains(groups, group => group.Single() == jack);
            Assert.Contains(groups, group => group.Single() == nine);
        }
    }
}
