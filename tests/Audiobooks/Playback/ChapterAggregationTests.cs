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
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Audiobooks.Playback
{
    public class ChapterAggregationTests
    {
        // ── File with embedded chapters ────────────────────────────────────────

        [Fact]
        public async Task GetStateAsync_FileWithEmbeddedChapters_EmitsOneChapterDtoPerChapter()
        {
            // ChaptersJson is pre-populated (two embedded chapters), ffprobe should not be called.
            const string chaptersJson = """[{"startSeconds":0.0,"endSeconds":60.0,"title":"Intro"},{"startSeconds":60.0,"endSeconds":120.0,"title":"Chapter 1"}]""";
            var book = new Audiobook
            {
                Id = 1,
                Files = new List<AudiobookFile>
                {
                    new() { Path = "/a/book.m4b", Container = "m4b", DurationSeconds = 120, ChaptersJson = chaptersJson }
                }
            };

            var svc = BuildService(book);
            var state = await svc.GetStateAsync(1);

            Assert.NotNull(state);
            Assert.Equal(2, state.Chapters.Count);
            Assert.Equal(0, state.Chapters[0].FileIndex);
            Assert.Equal(0.0, state.Chapters[0].StartSeconds);
            Assert.Equal(60.0, state.Chapters[0].EndSeconds);
            Assert.Equal("Intro", state.Chapters[0].Title);
            Assert.Equal(1, state.Chapters[1].Index);
        }

        // ── File without embedded chapters ────────────────────────────────────

        [Fact]
        public async Task GetStateAsync_FileWithNoChapters_EmitsSingleWholeFileChapter()
        {
            var book = new Audiobook
            {
                Id = 1,
                Files = new List<AudiobookFile>
                {
                    new() { Path = "/a/My Book Part 1.mp3", Container = "mp3", DurationSeconds = 3600, ChaptersJson = "[]" }
                }
            };

            var svc = BuildService(book);
            var state = await svc.GetStateAsync(1);

            Assert.NotNull(state);
            Assert.Single(state.Chapters);
            var ch = state.Chapters[0];
            Assert.Equal(0, ch.Index);
            Assert.Equal(0, ch.FileIndex);
            Assert.Equal(0.0, ch.StartSeconds);
            Assert.Equal(3600.0, ch.EndSeconds);
            Assert.Equal("My Book Part 1", ch.Title); // file name without extension
        }

        // ── Multi-file: chapters aggregated with correct FileIndex ─────────────

        [Fact]
        public async Task GetStateAsync_MultiFile_ChaptersHaveCorrectFileIndex()
        {
            const string file0Chapters = """[{"startSeconds":0.0,"endSeconds":30.0,"title":"Prologue"},{"startSeconds":30.0,"endSeconds":60.0,"title":"Ch1"}]""";
            var book = new Audiobook
            {
                Id = 1,
                Files = new List<AudiobookFile>
                {
                    // natural-sort order: Part 1 < Part 2
                    new() { Path = "/a/Part 1.mp3", Container = "mp3", DurationSeconds = 60,  ChaptersJson = file0Chapters },
                    new() { Path = "/a/Part 2.mp3", Container = "mp3", DurationSeconds = 120, ChaptersJson = "[]" },
                }
            };

            var svc = BuildService(book);
            var state = await svc.GetStateAsync(1);

            Assert.NotNull(state);
            // Part 1 has 2 embedded chapters, Part 2 has none → 3 total
            Assert.Equal(3, state.Chapters.Count);

            Assert.Equal(0, state.Chapters[0].Index);
            Assert.Equal(0, state.Chapters[0].FileIndex);
            Assert.Equal("Prologue", state.Chapters[0].Title);

            Assert.Equal(1, state.Chapters[1].Index);
            Assert.Equal(0, state.Chapters[1].FileIndex);
            Assert.Equal("Ch1", state.Chapters[1].Title);

            Assert.Equal(2, state.Chapters[2].Index);
            Assert.Equal(1, state.Chapters[2].FileIndex); // second file
            Assert.Equal("Part 2", state.Chapters[2].Title);
        }

        // ── ffprobe failure → cached as "[]", no throw ────────────────────────

        [Fact]
        public async Task GetStateAsync_FfprobeFails_TreatsFileAsNoChapters()
        {
            // ChaptersJson = null → will call ffprobe → ffprobe returns empty → cached as "[]"
            var book = new Audiobook
            {
                Id = 1,
                Files = new List<AudiobookFile>
                {
                    new() { Id = 42, Path = "/a/book.mp3", Container = "mp3", DurationSeconds = 100, ChaptersJson = null }
                }
            };

            // ffprobe returns no chapters
            var ffmpegMock = new Mock<IFfmpegService>();
            ffmpegMock.Setup(f => f.RunFfprobeChaptersAsync(It.IsAny<string>()))
                .ReturnsAsync(Array.Empty<FfprobeChapter>());

            var fileRepoMock = new Mock<IAudiobookFileRepository>();
            fileRepoMock.Setup(r => r.UpdateAsync(It.IsAny<AudiobookFile>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var svc = BuildService(book, ffmpegMock.Object, fileRepoMock.Object);

            // Must not throw
            var state = await svc.GetStateAsync(1);

            Assert.NotNull(state);
            Assert.Single(state.Chapters); // one whole-file chapter
            Assert.Equal(0.0, state.Chapters[0].StartSeconds);
            Assert.Equal(100.0, state.Chapters[0].EndSeconds);

            // chapter cache was persisted
            fileRepoMock.Verify(r => r.UpdateAsync(It.Is<AudiobookFile>(f => f.ChaptersJson == "[]"), It.IsAny<CancellationToken>()), Times.Once);
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static PlaybackService BuildService(
            Audiobook book,
            IFfmpegService? ffmpeg = null,
            IAudiobookFileRepository? fileRepo = null)
        {
            var repo = new Mock<IAudiobookRepository>();
            repo.Setup(r => r.GetByIdsWithFilesAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([book]);
            repo.Setup(r => r.UpdateAsync(It.IsAny<Audiobook>())).ReturnsAsync(true);

            var settingsRepo = new Mock<IApplicationSettingsRepository>();
            settingsRepo.Setup(r => r.GetAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ApplicationSettings());

            ffmpeg ??= Mock.Of<IFfmpegService>(f =>
                f.RunFfprobeChaptersAsync(It.IsAny<string>()) == Task.FromResult<IReadOnlyList<FfprobeChapter>>(Array.Empty<FfprobeChapter>()));
            fileRepo ??= Mock.Of<IAudiobookFileRepository>();

            return new PlaybackService(repo.Object, settingsRepo.Object, ffmpeg, fileRepo, NullLogger<PlaybackService>.Instance);
        }
    }
}
