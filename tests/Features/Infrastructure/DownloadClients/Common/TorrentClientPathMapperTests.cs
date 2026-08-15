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
using System.Text.Json;
using Listenarr.Infrastructure.DownloadClients.Common;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.DownloadClients.Common
{
    [Trait("Name", "TorrentClientPathMapperTests")]
    [Trait("Category", "DownloadClientPathMapping")]
    public class TorrentClientPathMapperTests : BaseTests
    {
        [Fact]
        public void BuildTransmissionSourceFiles_PreservesTopLevelFolderWhitespace()
        {
            var downloadDir = FileUtils.GetAbsolutePath("downloads");
            using var document = JsonDocument.Parse(
                """
                [
                  { "name": " Book Folder /chapter1.m4b" }
                ]
                """);

            var sourceFiles = TorrentClientPathMapper.BuildTransmissionSourceFiles(downloadDir, document.RootElement);

            var expected = Path.Join(downloadDir, " Book Folder ", "chapter1.m4b");
            Assert.Equal([expected], sourceFiles);
            Assert.StartsWith(downloadDir + Path.DirectorySeparatorChar, expected, StringComparison.Ordinal);
            Assert.Contains(" Book Folder ", expected, StringComparison.Ordinal);
        }

        [Fact]
        public void BuildTransmissionSourceFiles_WindowsClientPath_UsesWindowsSyntaxOnAnyHost()
        {
            using var document = JsonDocument.Parse(
                """
                [
                  { "name": "Author/book.m4b" }
                ]
                """);

            var sourceFiles = TorrentClientPathMapper.BuildTransmissionSourceFiles(
                "C:\\downloads",
                document.RootElement);

            Assert.Equal(["C:\\downloads\\Author\\book.m4b"], sourceFiles);
        }

        [Fact]
        public void BuildTransmissionSourceFiles_UnixClientPath_PreservesLiteralBackslash()
        {
            using var document = JsonDocument.Parse(
                """
                [
                  { "name": "Author\\Name/book.m4b" }
                ]
                """);

            var sourceFiles = TorrentClientPathMapper.BuildTransmissionSourceFiles(
                "/downloads",
                document.RootElement);

            Assert.Equal(["/downloads/Author\\Name/book.m4b"], sourceFiles);
        }

        [Fact]
        public void BuildTransmissionSourceFiles_PreservesTrailingFolderWhitespace()
        {
            var downloadDir = FileUtils.GetAbsolutePath("downloads");
            using var document = JsonDocument.Parse(
                """
                [
                  { "name": "Book Folder /chapter1.m4b" }
                ]
                """);

            var sourceFiles = TorrentClientPathMapper.BuildTransmissionSourceFiles(downloadDir, document.RootElement);

            var expected = Path.Join(downloadDir, "Book Folder ", "chapter1.m4b");
            Assert.Equal([expected], sourceFiles);
            Assert.StartsWith(downloadDir + Path.DirectorySeparatorChar, expected, StringComparison.Ordinal);
            Assert.Contains("Book Folder ", expected, StringComparison.Ordinal);
        }

        [Fact]
        public void BuildQbittorrentSourceFiles_PreservesTorrentFolderWhitespace()
        {
            var savePath = FileUtils.GetAbsolutePath("downloads");
            var files = ParseFiles(
                """
                [
                  { "name": " Book Folder /chapter1.m4b" }
                ]
                """);

            var sourceFiles = TorrentClientPathMapper.BuildQbittorrentSourceFiles(savePath, files);

            var expected = Path.Join(savePath, " Book Folder ", "chapter1.m4b");
            Assert.Equal([expected], sourceFiles);
        }

        [Fact]
        public void BuildQbittorrentSourceFiles_PreservesCaseDistinctSourceFiles()
        {
            var savePath = FileUtils.GetAbsolutePath("downloads");
            var files = ParseFiles(
                """
                [
                  { "name": "Book/chapter.m4b" },
                  { "name": "Book/Chapter.m4b" }
                ]
                """);

            var sourceFiles = TorrentClientPathMapper.BuildQbittorrentSourceFiles(savePath, files);

            Assert.Equal(
                [
                    Path.Join(savePath, "Book", "chapter.m4b"),
                    Path.Join(savePath, "Book", "Chapter.m4b")
                ],
                sourceFiles);
        }

        [Fact]
        public void TranslateSourceFiles_PreservesCaseDistinctSourceFiles()
        {
            var savePath = FileUtils.GetAbsolutePath("downloads");
            var lower = Path.Join(savePath, "Book", "chapter.m4b");
            var upper = Path.Join(savePath, "Book", "Chapter.m4b");

            var sourceFiles = QbittorrentImportPathResolver.TranslateSourceFiles([lower, upper]);

            Assert.Equal([lower, upper], sourceFiles);
        }

        [Fact]
        public void ResolveQbittorrentContentPath_PreservesSharedTopLevelFolderWhitespace()
        {
            var savePath = FileUtils.GetAbsolutePath("downloads");
            var files = ParseFiles(
                """
                [
                  { "name": " Book Folder /chapter1.m4b" },
                  { "name": " Book Folder /chapter2.m4b" }
                ]
                """);

            var contentPath = TorrentClientPathMapper.ResolveQbittorrentContentPath(savePath, files);

            Assert.Equal(Path.Join(savePath, " Book Folder "), contentPath);
        }

        [Fact]
        public void BuildQbittorrentSourceFiles_RootedChildPathsStayUnderSavePath()
        {
            var savePath = FileUtils.GetAbsolutePath("downloads");
            var files = ParseFiles(
                """
                [
                  { "name": "/ Book Folder /chapter1.m4b" }
                ]
                """);

            var sourceFile = Assert.Single(TorrentClientPathMapper.BuildQbittorrentSourceFiles(savePath, files));

            Assert.Equal(Path.Join(savePath, " Book Folder ", "chapter1.m4b"), sourceFile);
            Assert.True(FileUtils.IsPathSameOrInside(sourceFile, savePath));
        }

        [Fact]
        public void BuildQbittorrentSourceFiles_DropsParentTraversalChildPaths()
        {
            var savePath = FileUtils.GetAbsolutePath("downloads");
            var parentSegment = new string('.', 2);
            var files = ParseFiles(
                $$"""
                [
                  { "name": "{{parentSegment}}/escape.m4b" },
                  { "name": " Book Folder /chapter1.m4b" }
                ]
                """);

            var sourceFiles = TorrentClientPathMapper.BuildQbittorrentSourceFiles(savePath, files);

            var sourceFile = Assert.Single(sourceFiles);
            Assert.Equal(Path.Join(savePath, " Book Folder ", "chapter1.m4b"), sourceFile);
            Assert.True(FileUtils.IsPathSameOrInside(sourceFile, savePath));
        }

        [Fact]
        public void BuildTransmissionSourceFiles_DropsParentTraversalChildPaths()
        {
            var downloadDir = FileUtils.GetAbsolutePath("downloads");
            var parentSegment = new string('.', 2);
            using var document = JsonDocument.Parse(
                $$"""
                [
                  { "name": "Book/{{parentSegment}}/{{parentSegment}}/escape.m4b" },
                  { "name": " Book Folder /chapter1.m4b" }
                ]
                """);

            var sourceFiles = TorrentClientPathMapper.BuildTransmissionSourceFiles(downloadDir, document.RootElement);

            var sourceFile = Assert.Single(sourceFiles);
            Assert.Equal(Path.Join(downloadDir, " Book Folder ", "chapter1.m4b"), sourceFile);
            Assert.True(FileUtils.IsPathSameOrInside(sourceFile, downloadDir));
        }

        [Fact]
        public void ResolveQbittorrentContentPath_IgnoresParentTraversalTopLevelPath()
        {
            var savePath = FileUtils.GetAbsolutePath("downloads");
            var parentSegment = new string('.', 2);
            var files = ParseFiles(
                $$"""
                [
                  { "name": "{{parentSegment}}/escape.m4b" },
                  { "name": " Book Folder /chapter1.m4b" },
                  { "name": " Book Folder /chapter2.m4b" }
                ]
                """);

            var contentPath = TorrentClientPathMapper.ResolveQbittorrentContentPath(savePath, files);

            Assert.Equal(Path.Join(savePath, " Book Folder "), contentPath);
            Assert.True(FileUtils.IsPathSameOrInside(contentPath, savePath));
        }

        private static List<Dictionary<string, JsonElement>> ParseFiles(string json)
        {
            using var document = JsonDocument.Parse(json);
            var files = new List<Dictionary<string, JsonElement>>();

            foreach (var element in document.RootElement.EnumerateArray())
            {
                var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in element.EnumerateObject())
                {
                    map[property.Name] = property.Value.Clone();
                }

                files.Add(map);
            }

            return files;
        }
    }
}
