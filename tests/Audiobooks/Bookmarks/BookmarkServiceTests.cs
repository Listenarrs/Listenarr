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
using Listenarr.Application.Audiobooks.Bookmarks;

namespace Listenarr.Tests.Audiobooks.Bookmarks
{
    public class BookmarkServiceTests
    {
        [Fact]
        public async Task GetAsync_ReturnsMappedDtos()
        {
            var stored = new List<Bookmark>
            {
                new() { Id = 1, AudiobookId = 10, FileIndex = 0, PositionSeconds = 120.5, Label = "Great scene", CreatedUtc = DateTime.UtcNow }
            };

            var repo = new Mock<IBookmarkRepository>();
            repo.Setup(r => r.GetByAudiobookIdAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(stored);

            var svc = new BookmarkService(repo.Object);
            var result = await svc.GetAsync(10);

            Assert.Single(result);
            Assert.Equal(1, result[0].Id);
            Assert.Equal(0, result[0].FileIndex);
            Assert.Equal(120.5, result[0].PositionSeconds);
            Assert.Equal("Great scene", result[0].Label);
        }

        [Fact]
        public async Task AddAsync_PersistsAndReturnsDto()
        {
            Bookmark? saved = null;
            var repo = new Mock<IBookmarkRepository>();
            repo.Setup(r => r.AddAsync(It.IsAny<Bookmark>(), It.IsAny<CancellationToken>()))
                .Callback<Bookmark, CancellationToken>((b, _) =>
                {
                    b.Id = 99;
                    saved = b;
                })
                .ReturnsAsync((Bookmark b, CancellationToken _) => b);

            var svc = new BookmarkService(repo.Object);
            var req = new CreateBookmarkRequest(FileIndex: 2, PositionSeconds: 300.0, Label: "note");
            var dto = await svc.AddAsync(audiobookId: 5, req);

            Assert.NotNull(saved);
            Assert.Equal(5, saved!.AudiobookId);
            Assert.Equal(2, saved.FileIndex);
            Assert.Equal(300.0, saved.PositionSeconds);
            Assert.Equal("note", saved.Label);
            Assert.Equal(99, dto.Id);
        }

        [Fact]
        public async Task DeleteAsync_WhenFound_ReturnsTrue()
        {
            var repo = new Mock<IBookmarkRepository>();
            repo.Setup(r => r.DeleteAsync(5, 7, It.IsAny<CancellationToken>())).ReturnsAsync(true);

            var svc = new BookmarkService(repo.Object);
            var result = await svc.DeleteAsync(audiobookId: 5, bookmarkId: 7);

            Assert.True(result);
        }

        [Fact]
        public async Task DeleteAsync_WhenNotFound_ReturnsFalse()
        {
            var repo = new Mock<IBookmarkRepository>();
            repo.Setup(r => r.DeleteAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            var svc = new BookmarkService(repo.Object);
            var result = await svc.DeleteAsync(audiobookId: 5, bookmarkId: 999);

            Assert.False(result);
        }
    }
}
