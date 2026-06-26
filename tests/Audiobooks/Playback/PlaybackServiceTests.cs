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
using Listenarr.Application.Audiobooks.Playback;

namespace Listenarr.Tests.Audiobooks.Playback
{
    public class PlaybackServiceTests
    {
        // ── Natural-sort comparer ──────────────────────────────────────────────

        [Fact]
        public void NaturalSort_OrdersNumericSegmentsCorrectly()
        {
            var unsorted = new[]
            {
                "b - Part 10.mp3",
                "a - Part 2.mp3",
            };

            var sorted = unsorted
                .OrderBy(x => x, PlaybackService.NaturalSortComparer.Instance)
                .ToList();

            Assert.Equal("a - Part 2.mp3", sorted[0]);
            Assert.Equal("b - Part 10.mp3", sorted[1]);
        }

        // ── GetStateAsync ──────────────────────────────────────────────────────

        [Fact]
        public async Task GetStateAsync_ReturnsNull_WhenBookNotFound()
        {
            var repo = new Mock<IAudiobookRepository>();
            repo.Setup(r => r.GetByIdsWithFilesAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);

            var svc = new PlaybackService(repo.Object, BuildSettingsRepo(autoUnmonitor: true).Object);

            var result = await svc.GetStateAsync(999);
            Assert.Null(result);
        }

        [Fact]
        public async Task GetStateAsync_ReturnsFilesInNaturalSortOrder()
        {
            // Files listed out of order: Part 10 first, then Part 2, then Part 1
            // Natural sort should yield: Part 1 (dur=1) < Part 2 (dur=2) < Part 10 (dur=10)
            var book = new Audiobook
            {
                Id = 1,
                Title = "Test Book",
                Files = new List<AudiobookFile>
                {
                    new() { Path = "/audio/Part 10.mp3", Container = "mp3", DurationSeconds = 10 },
                    new() { Path = "/audio/Part 2.mp3",  Container = "mp3", DurationSeconds = 2  },
                    new() { Path = "/audio/Part 1.mp3",  Container = "mp3", DurationSeconds = 1  },
                }
            };

            var svc = new PlaybackService(
                BuildRepoWithBook(book).Object,
                BuildSettingsRepo(autoUnmonitor: true).Object);

            var state = await svc.GetStateAsync(1);

            Assert.NotNull(state);
            Assert.Equal(3, state.Files.Count);
            // Indices must be sequential 0, 1, 2 after natural sort
            Assert.Equal(0, state.Files[0].Index);
            Assert.Equal(1, state.Files[1].Index);
            Assert.Equal(2, state.Files[2].Index);
            // DurationSeconds reflects ordering: Part 1=1s, Part 2=2s, Part 10=10s
            Assert.Equal(1, state.Files[0].DurationSeconds);
            Assert.Equal(2, state.Files[1].DurationSeconds);
            Assert.Equal(10, state.Files[2].DurationSeconds);
        }

        [Fact]
        public async Task GetStateAsync_AssignsCorrectContentType()
        {
            var book = new Audiobook
            {
                Id = 1,
                Files = new List<AudiobookFile>
                {
                    new() { Path = "/a/file.m4b", Container = "m4b" },
                }
            };

            var svc = new PlaybackService(
                BuildRepoWithBook(book).Object,
                BuildSettingsRepo(autoUnmonitor: true).Object);

            var state = await svc.GetStateAsync(1);

            Assert.NotNull(state);
            Assert.Equal("audio/mp4", state.Files[0].ContentType);
        }

        // ── SaveAsync ─────────────────────────────────────────────────────────

        [Fact]
        public async Task SaveAsync_WithFinished_AndAutoUnmonitorOn_SetsMonitoredFalse()
        {
            var book = new Audiobook { Id = 1, Monitored = true, Files = new List<AudiobookFile>() };
            Audiobook? saved = null;

            var repo = new Mock<IAudiobookRepository>();
            repo.Setup(r => r.GetByIdsWithFilesAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([book]);
            repo.Setup(r => r.UpdateAsync(It.IsAny<Audiobook>()))
                .Callback<Audiobook>(b => saved = b)
                .ReturnsAsync(true);

            var svc = new PlaybackService(repo.Object, BuildSettingsRepo(autoUnmonitor: true).Object);

            var result = await svc.SaveAsync(1, new SavePlaybackRequest(0, 100.0, Finished: true));

            Assert.True(result);
            repo.Verify(r => r.UpdateAsync(It.IsAny<Audiobook>()), Times.Once);
            Assert.NotNull(saved);
            Assert.False(saved!.Monitored);
        }

        [Fact]
        public async Task SaveAsync_WithFinished_AndAutoUnmonitorOff_LeavesMonitoredTrue()
        {
            var book = new Audiobook { Id = 1, Monitored = true, Files = new List<AudiobookFile>() };
            Audiobook? saved = null;

            var repo = new Mock<IAudiobookRepository>();
            repo.Setup(r => r.GetByIdsWithFilesAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([book]);
            repo.Setup(r => r.UpdateAsync(It.IsAny<Audiobook>()))
                .Callback<Audiobook>(b => saved = b)
                .ReturnsAsync(true);

            var svc = new PlaybackService(repo.Object, BuildSettingsRepo(autoUnmonitor: false).Object);

            var result = await svc.SaveAsync(1, new SavePlaybackRequest(0, 100.0, Finished: true));

            Assert.True(result);
            Assert.NotNull(saved);
            Assert.True(saved!.Monitored);
        }

        [Fact]
        public async Task SaveAsync_ReturnsFalse_WhenBookNotFound()
        {
            var repo = new Mock<IAudiobookRepository>();
            repo.Setup(r => r.GetByIdsWithFilesAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);

            var svc = new PlaybackService(repo.Object, BuildSettingsRepo(autoUnmonitor: true).Object);

            var result = await svc.SaveAsync(999, new SavePlaybackRequest(0, 0, Finished: false));
            Assert.False(result);
        }

        // ── ResolveFileAsync ───────────────────────────────────────────────────

        [Fact]
        public async Task ResolveFileAsync_OutOfRangeIndex_ReturnsNull()
        {
            var book = new Audiobook
            {
                Id = 1,
                Files = new List<AudiobookFile>
                {
                    new() { Path = "/audio/Part 1.mp3", Container = "mp3" },
                }
            };

            var svc = new PlaybackService(
                BuildRepoWithBook(book).Object,
                BuildSettingsRepo(autoUnmonitor: true).Object);

            var result = await svc.ResolveFileAsync(1, fileIndex: 5);
            Assert.Null(result);
        }

        [Fact]
        public async Task ResolveFileAsync_ValidIndex_ReturnsPathAndContentType()
        {
            var book = new Audiobook
            {
                Id = 1,
                Files = new List<AudiobookFile>
                {
                    new() { Path = "/audio/book.m4b", Container = "m4b" },
                }
            };

            var svc = new PlaybackService(
                BuildRepoWithBook(book).Object,
                BuildSettingsRepo(autoUnmonitor: true).Object);

            var result = await svc.ResolveFileAsync(1, fileIndex: 0);

            Assert.NotNull(result);
            Assert.Equal("/audio/book.m4b", result.Value.Path);
            Assert.Equal("audio/mp4", result.Value.ContentType);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static Mock<IAudiobookRepository> BuildRepoWithBook(Audiobook book)
        {
            var repo = new Mock<IAudiobookRepository>();
            repo.Setup(r => r.GetByIdsWithFilesAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([book]);
            repo.Setup(r => r.UpdateAsync(It.IsAny<Audiobook>()))
                .ReturnsAsync(true);
            return repo;
        }

        private static Mock<IApplicationSettingsRepository> BuildSettingsRepo(bool autoUnmonitor)
        {
            var settingsRepo = new Mock<IApplicationSettingsRepository>();
            settingsRepo.Setup(r => r.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ApplicationSettings { PlayerAutoUnmonitorOnFinish = autoUnmonitor });
            return settingsRepo;
        }
    }
}
