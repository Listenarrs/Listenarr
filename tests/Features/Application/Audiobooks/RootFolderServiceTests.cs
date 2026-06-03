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
using Xunit;
using Xunit.Abstractions;
using Moq;
using Listenarr.Domain.Common;
using Listenarr.Application.Interfaces;
using Listenarr.Tests.Common;
using Listenarr.Tests.Builders;
using Microsoft.Extensions.DependencyInjection;

namespace Listenarr.Tests.Features.Application.Audiobooks
{
    public class RootFolderServiceTests : BaseTests
    {
        private readonly string booksPath = FileUtils.GetAbsoluteDirectoryPath("books");
        private readonly string rootPath = FileUtils.GetAbsoluteDirectoryPath("root");
        private readonly string newRootPath = FileUtils.GetAbsoluteDirectoryPath("newroot");
        private readonly string rootAuthorTitlePath = FileUtils.GetAbsoluteDirectoryPath("root", "Author", "Title");
        private readonly string newRootAuthorTitlePath = FileUtils.GetAbsoluteDirectoryPath("newroot", "Author", "Title");

        private readonly ITestOutputHelper _output;
        public RootFolderServiceTests(ITestOutputHelper output) { _output = output; }

        [Fact]
        public async Task Create_Throws_WhenPathDuplicate()
        {
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("A")
                .WithPath(booksPath)
                .Build());

            var rootFolderService = _provider.GetRequiredService<IRootFolderService>();

            await Assert.ThrowsAsync<InvalidOperationException>(() => rootFolderService.CreateAsync(new RootFolderBuilder()
                .WithName("B")
                .WithPath(booksPath)
                .Build()));
        }

        [Fact]
        public async Task Delete_Throws_WhenReferencedWithoutReassign()
        {
            var root = await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("A")
                .WithPath(booksPath)
                .Build());

            await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("T")
                .WithBasePath(booksPath)
                .Build());

            var rootFolderService = _provider.GetRequiredService<IRootFolderService>();

            await Assert.ThrowsAsync<InvalidOperationException>(() => rootFolderService.DeleteAsync(root.Id));
        }

        [Fact]
        public async Task Update_RenameWithoutMove_UpdatesAudiobookBasePaths()
        {
            var root = await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("R")
                .WithPath(rootPath)
                .Build());

            var a1 = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("A1")
                .WithBasePath(rootAuthorTitlePath)
                .Build());

            var a2 = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("A2")
                .WithBasePath(rootPath)
                .Build());

            var rootFolderService = _provider.GetRequiredService<IRootFolderService>();

            await rootFolderService.UpdateAsync(new RootFolderBuilder()
                .WithId(root.Id)
                .WithName("R2")
                .WithPath(newRootPath)
                .Build(), moveFiles: false);

            a1 = await _audiobookRepository.GetByIdAsync(a1.Id);
            a2 = await _audiobookRepository.GetByIdAsync(a2.Id);
            Assert.Equal(newRootAuthorTitlePath, a1.BasePath);
            Assert.Equal(newRootPath, a2.BasePath);
        }

        [Fact]
        public async Task Update_RenameWithMove_EnqueuesMovesAndUpdatesDB()
        {
            var mockMove = new Mock<IMoveQueueService>();
            mockMove.Setup(m => m.EnqueueMoveAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(Guid.NewGuid());
            _services.AddSingleton(mockMove.Object);
            Init();

            var root = await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("R1")
                .WithPath(rootPath)
                .Build());

            var a1 = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("A1")
                .WithBasePath(rootAuthorTitlePath)
                .Build());

            var a2 = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("A2")
                .WithBasePath(rootPath)
                .Build());

            var rootFolderService = _provider.GetRequiredService<IRootFolderService>();
            await rootFolderService.UpdateAsync(new RootFolderBuilder()
                .WithId(root.Id)
                .WithName("R2")
                .WithPath(newRootPath)
                .Build(), moveFiles: true);

            a1 = await _audiobookRepository.GetByIdAsync(a1.Id);
            a2 = await _audiobookRepository.GetByIdAsync(a2.Id);
            Assert.Equal(newRootAuthorTitlePath, a1.BasePath);
            Assert.Equal(newRootPath, a2.BasePath);

            mockMove.Verify(m => m.EnqueueMoveAsync(a1.Id, newRootAuthorTitlePath, rootAuthorTitlePath), Times.Once);
            mockMove.Verify(m => m.EnqueueMoveAsync(a2.Id, newRootPath, rootPath), Times.Once);
        }

        [Fact]
        public async Task Delete_WhenThereIsOtherRootWithinThatOne_StillUsed()
        {
            var r1 = await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("R")
                .WithPath(FileUtils.GetAbsolutePath("data", "media", "books"))
                .Build());

            var r2 = await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("R")
                .WithPath(FileUtils.GetAbsolutePath("data", "media", "books", "audiobooks"))
                .Build());

            var a1 = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("A1")
                .WithBasePath(FileUtils.GetAbsolutePath(r1.Path, "a1"))
                .Build());

            var a2 = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("A2")
                .WithBasePath(FileUtils.GetAbsolutePath(r2.Path, "a2"))
                .Build());

            var rootFolderService = _provider.GetRequiredService<IRootFolderService>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => rootFolderService.DeleteAsync(r1.Id));
        }

        [Fact]
        public async Task Delete_WhenThereIsOtherRootWithinThatOne_Unused()
        {
            var r1 = await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("R")
                .WithPath(FileUtils.GetAbsolutePath("data", "media", "books"))
                .Build());

            var r2 = await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("R")
                .WithPath(FileUtils.GetAbsolutePath("data", "media", "books", "audiobooks"))
                .Build());

            var a1 = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("A1")
                .WithBasePath(FileUtils.GetAbsolutePath(r2.Path, "a1"))
                .Build());

            var a2 = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("A2")
                .WithBasePath(FileUtils.GetAbsolutePath(r2.Path, "a2"))
                .Build());

            var rootFolderService = _provider.GetRequiredService<IRootFolderService>();
            await rootFolderService.DeleteAsync(r1.Id);
            Assert.Null(await _rootFolderRepository.GetByIdAsync(r1.Id));
        }

        [Fact]
        public async Task Delete_EvenIfThereIsAlreadyOrphanedAudiobooks()
        {
            var r1 = await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("R")
                .WithPath(FileUtils.GetAbsolutePath("data", "media", "books"))
                .Build());

            var a1 = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("A1")
                .Build());

            var rootFolderService = _provider.GetRequiredService<IRootFolderService>();
            await rootFolderService.DeleteAsync(r1.Id);
            Assert.Null(await _rootFolderRepository.GetByIdAsync(r1.Id));
        }

        [Fact]
        public async Task Delete_WithSiblingsPath()
        {
            var r1 = await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("R1")
                .WithPath(FileUtils.GetAbsolutePath("data", "media", "books"))
                .Build());

            var r2 = await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("R2")
                .WithPath(FileUtils.GetAbsolutePath("data", "media", "book"))
                .Build());

            var a1 = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("A1")
                .WithBasePath(FileUtils.GetAbsolutePath(r1.Path, "a1"))
                .Build());

            var a2 = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("A2")
                .WithBasePath(FileUtils.GetAbsolutePath(r2.Path, "a2"))
                .Build());

            var rootFolderService = _provider.GetRequiredService<IRootFolderService>();

            // Should throw as R2 /book should not be detected as root path for A1 (/books)
            await Assert.ThrowsAsync<InvalidOperationException>(() => rootFolderService.DeleteAsync(r1.Id));
        }
    }
}
