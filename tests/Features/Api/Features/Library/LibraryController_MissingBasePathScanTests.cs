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
using Microsoft.AspNetCore.Mvc;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    [Trait("Area", "LibraryApi")]
    [Trait("Name", "LibraryController_MissingBasePathScanTests")]
    [Trait("Category", "LibraryController")]
    public class LibraryController_MissingBasePathScanTests : BaseTests
    {
        private const string Author = "Edgar Rice Burroughs";
        private const string Title = "A Princess of Mars";
        private const string SiblingAuthor = "L. Frank Baum";
        private const string SiblingTitle = "The Wonderful Wizard of Oz";

        [Fact]
        public async Task ScanAudiobook_DefaultScanOfMissingBasePath_RemovesOnlyRowsInsideBasePath()
        {
            var fixture = await CreateFixtureAsync("scan-missing-basepath");
            Directory.Delete(fixture.BasePath, recursive: true);

            var result = await _provider.GetRequiredService<LibraryController>()
                .ScanAudiobookFiles(fixture.Audiobook.Id, request: null);

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Contains("reconciliationPerformed = True", ok.Value?.ToString() ?? string.Empty);
            var remaining = Assert.Single(
                await _audiobookFileRepository.GetByAudiobookIdAsync(fixture.Audiobook.Id));
            Assert.Equal(fixture.OutsideRow.Id, remaining.Id);
            var siblingRow = Assert.Single(
                await _audiobookFileRepository.GetByAudiobookIdAsync(fixture.Sibling.Id));
            Assert.Equal(fixture.SiblingRow.Id, siblingRow.Id);
            var history = await _historyRepository.GetByAudiobookIdAsync(fixture.Audiobook.Id);
            Assert.Equal(2, history.Count(entry => entry.EventType == "File Removed"));
            Assert.Empty(await _historyRepository.GetByAudiobookIdAsync(fixture.Sibling.Id));
            Assert.False(Directory.Exists(fixture.BasePath));
            Assert.Equal(
                [fixture.SiblingFile],
                Directory.EnumerateFileSystemEntries(fixture.Root, "*", SearchOption.AllDirectories)
                    .Where(File.Exists));
            var persisted = await _audiobookRepository.GetByIdSnapshotAsync(fixture.Audiobook.Id);
            Assert.Equal(fixture.BasePath, persisted!.BasePath);
        }

        [Fact]
        public async Task ScanAudiobook_DefaultScanWithMissingRoot_StillRefusesAndPreservesRows()
        {
            var fixture = await CreateFixtureAsync("scan-missing-root");
            Directory.Delete(fixture.Root, recursive: true);

            var result = await _provider.GetRequiredService<LibraryController>()
                .ScanAudiobookFiles(fixture.Audiobook.Id, request: null);

            AssertRefusedAndPreserved(result);
            Assert.Equal(3, (await _audiobookFileRepository.GetByAudiobookIdAsync(fixture.Audiobook.Id)).Count);
            Assert.False(Directory.Exists(fixture.Root));
        }

        [Fact]
        public async Task ScanAudiobook_DefaultScanWithEmptyRoot_RefusesAsPossiblyUnmounted()
        {
            var fixture = await CreateFixtureAsync("scan-missing-basepath-empty-root");
            foreach (var entry in Directory.EnumerateFileSystemEntries(fixture.Root))
            {
                Directory.Delete(entry, recursive: true);
            }

            var result = await _provider.GetRequiredService<LibraryController>()
                .ScanAudiobookFiles(fixture.Audiobook.Id, request: null);

            AssertRefusedAndPreserved(result);
            Assert.Equal(3, (await _audiobookFileRepository.GetByAudiobookIdAsync(fixture.Audiobook.Id)).Count);
            Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root));
        }

        [DirectoryLinkFact]
        public async Task ScanAudiobook_DefaultScanWithDanglingBasePathLink_RefusesAndPreservesRows()
        {
            var fixture = await CreateFixtureAsync("scan-missing-basepath-link");
            Directory.Delete(fixture.BasePath, recursive: true);
            var missingTarget = Path.Join(fixture.Root, "missing-link-target");
            Directory.CreateSymbolicLink(fixture.BasePath, missingTarget);

            var result = await _provider.GetRequiredService<LibraryController>()
                .ScanAudiobookFiles(fixture.Audiobook.Id, request: null);

            AssertRefusedAndPreserved(result);
            Assert.Equal(3, (await _audiobookFileRepository.GetByAudiobookIdAsync(fixture.Audiobook.Id)).Count);
            Assert.NotNull(new DirectoryInfo(fixture.BasePath).LinkTarget);
        }

        [Fact]
        public async Task ScanAudiobook_ExplicitScanOfMissingPath_StillRefusesAndPreservesRows()
        {
            var fixture = await CreateFixtureAsync("scan-missing-explicit-path");
            Directory.Delete(fixture.BasePath, recursive: true);

            var result = await _provider.GetRequiredService<LibraryController>()
                .ScanAudiobookFiles(
                    fixture.Audiobook.Id,
                    new LibraryController.ScanRequest { Path = fixture.BasePath });

            AssertRefusedAndPreserved(result);
            Assert.Equal(3, (await _audiobookFileRepository.GetByAudiobookIdAsync(fixture.Audiobook.Id)).Count);
        }

        [LinuxFact]
        public async Task ScanAudiobook_InsensitiveRootWithRecasedBasePath_RefusesAndPreservesLiveRows()
        {
            var fixture = await CreateFixtureAsync(
                "scan-missing-basepath-recased",
                FileSystemCaseSensitivityMode.Insensitive);
            var liveFile = await FileService.GetFileAsync(fixture.BasePath, Title + ".m4b", "audio");
            var recasedBasePath = Path.Join(Path.GetDirectoryName(fixture.BasePath)!, Title.ToLowerInvariant());
            Directory.Move(fixture.BasePath, recasedBasePath);

            var result = await _provider.GetRequiredService<LibraryController>()
                .ScanAudiobookFiles(fixture.Audiobook.Id, request: null);

            AssertRefusedAndPreserved(result);
            Assert.Equal(3, (await _audiobookFileRepository.GetByAudiobookIdAsync(fixture.Audiobook.Id)).Count);
            Assert.True(File.Exists(Path.Join(recasedBasePath, Path.GetFileName(liveFile))));
        }

        [DirectoryLinkFact]
        public async Task ScanAudiobook_DefaultScanWithDanglingAuthorFolderLink_RefusesAndPreservesRows()
        {
            var fixture = await CreateFixtureAsync("scan-missing-author-link");
            var authorFolder = Path.GetDirectoryName(fixture.BasePath)!;
            Directory.Delete(authorFolder, recursive: true);
            Directory.CreateSymbolicLink(authorFolder, Path.Join(fixture.Root, "missing-author-target"));

            var result = await _provider.GetRequiredService<LibraryController>()
                .ScanAudiobookFiles(fixture.Audiobook.Id, request: null);

            AssertRefusedAndPreserved(result);
            Assert.Equal(3, (await _audiobookFileRepository.GetByAudiobookIdAsync(fixture.Audiobook.Id)).Count);
            Assert.NotNull(new DirectoryInfo(authorFolder).LinkTarget);
        }

        [DirectoryLinkTheory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ScanAudiobook_DanglingLinkWithNoRowsInsideBasePath_RefusesAtTheAbsenceProbe(
            bool linkReplacesAuthorFolder)
        {
            var fixture = await CreateFixtureAsync(
                "scan-missing-basepath-probe-link",
                includeRowsInsideBasePath: false);
            var linkPath = linkReplacesAuthorFolder
                ? Path.GetDirectoryName(fixture.BasePath)!
                : fixture.BasePath;
            Directory.Delete(linkPath, recursive: true);
            Directory.CreateSymbolicLink(linkPath, Path.Join(fixture.Root, "missing-link-target"));

            var result = await _provider.GetRequiredService<LibraryController>()
                .ScanAudiobookFiles(fixture.Audiobook.Id, request: null);

            AssertRefusedAndPreserved(result);
            var remaining = Assert.Single(
                await _audiobookFileRepository.GetByAudiobookIdAsync(fixture.Audiobook.Id));
            Assert.Equal(fixture.OutsideRow.Id, remaining.Id);
            Assert.NotNull(new DirectoryInfo(linkPath).LinkTarget);
        }

        [Fact]
        public async Task ScanAudiobook_UnreadableRootDuringMissingBasePathChecks_KeepsOriginalRefusal()
        {
            var localFileSystem = new LocalFileSystem();
            var fileSystem = new Mock<IFileSystem>();
            fileSystem.Setup(system => system.DirectoryExists(It.IsAny<string>()))
                .Returns<string>(localFileSystem.DirectoryExists);
            fileSystem.Setup(system => system.IsReparsePoint(It.IsAny<string>()))
                .Returns<string>(localFileSystem.IsReparsePoint);
            fileSystem.Setup(system => system.EnumerateFileSystemEntries(It.IsAny<string>()))
                .Throws(new UnauthorizedAccessException("simulated unreadable root"));
            Init(services => services.WithSingleton(fileSystem.Object));
            var fixture = await CreateFixtureAsync("scan-missing-basepath-unreadable");
            Directory.Delete(fixture.BasePath, recursive: true);

            var result = await _provider.GetRequiredService<LibraryController>()
                .ScanAudiobookFiles(fixture.Audiobook.Id, request: null);

            AssertRefusedAndPreserved(result);
            Assert.Equal(3, (await _audiobookFileRepository.GetByAudiobookIdAsync(fixture.Audiobook.Id)).Count);
            fileSystem.Verify(system => system.EnumerateFileSystemEntries(It.IsAny<string>()), Times.Once);
        }

        private void AssertRefusedAndPreserved(IActionResult result)
        {
            var refused = Assert.IsType<ObjectResult>(result);
            Assert.Equal(409, refused.StatusCode);
            Assert.Contains(
                nameof(ScanPathAuthorizationFailure.IdentityUnavailable),
                refused.Value?.ToString() ?? string.Empty,
                StringComparison.Ordinal);
        }

        private async Task<MissingBasePathFixture> CreateFixtureAsync(
            string name,
            FileSystemCaseSensitivityMode? caseSensitivityMode = null,
            bool includeRowsInsideBasePath = true)
        {
            var root = FileService.GetTempDirectory(name);
            var basePath = Path.Join(root, Author, Title);
            Directory.CreateDirectory(basePath);
            var siblingBasePath = Path.Join(root, SiblingAuthor, SiblingTitle);
            Directory.CreateDirectory(siblingBasePath);
            var siblingFile = await FileService.GetFileAsync(siblingBasePath, SiblingTitle + ".m4b", "audio");
            await AddAuthorizedRootAsync(root, caseSensitivityMode: caseSensitivityMode);
            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle(Title)
                .WithAuthor(Author)
                .WithBasePath(basePath)
                .Build());
            var sibling = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle(SiblingTitle)
                .WithAuthor(SiblingAuthor)
                .WithBasePath(siblingBasePath)
                .Build());
            if (includeRowsInsideBasePath)
            {
                await AddRowAsync(audiobook, Path.Join(basePath, Title + ".m4b"));
                await AddRowAsync(audiobook, Path.Join("Disc 2", Title + ".m4b"));
            }

            var outsideRow = await AddRowAsync(audiobook, Path.Join(root, Author, Title + ".m4b"));
            var siblingRow = await AddRowAsync(sibling, siblingFile);
            return new MissingBasePathFixture(
                root,
                basePath,
                siblingFile,
                audiobook,
                sibling,
                outsideRow,
                siblingRow);
        }

        private Task<AudiobookFile> AddRowAsync(Audiobook audiobook, string path) =>
            _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(path)
                .Build());

        private sealed record MissingBasePathFixture(
            string Root,
            string BasePath,
            string SiblingFile,
            Audiobook Audiobook,
            Audiobook Sibling,
            AudiobookFile OutsideRow,
            AudiobookFile SiblingRow);
    }
}
