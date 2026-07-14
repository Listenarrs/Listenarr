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
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library
{
    /// <summary>
    /// The descend-move shape: naming templates that append subfolders make
    /// organize request ".../Book" -> ".../Book/Narrator" — a target INSIDE
    /// its own source. The copy-walk mover can't do that (it would recurse
    /// into its own output), so it's handled by two same-volume renames.
    /// Live case: 97 organize jobs, every one failed "Source and target
    /// paths overlap".
    /// </summary>
    [Trait("Area", "Library")]
    [Trait("Name", "MoveJobProcessor_DescendMoveTests")]
    public class MoveJobProcessor_DescendMoveTests : BaseTests
    {
        private async Task<(Audiobook book, string source, int fileId)> CreateBookOnDiskAsync(string folder)
        {
            var source = FileService.GetTempDirectory(folder);
            await File.WriteAllTextAsync(Path.Join(source, "Book-001.mp3"), "audio-1");
            await File.WriteAllTextAsync(Path.Join(source, "Book-002.mp3"), "audio-2");

            var book = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Elevation")
                .WithBasePath(source)
                .Build());
            var file = await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(book)
                .WithPath(Path.Join(source, "Book-001.mp3"))
                .Build());
            return (book, source, file.Id);
        }

        [Fact]
        [Trait("Method", "ProcessJobAsync")]
        [Trait("Scenario", "TargetInsideSource_Succeeds")]
        public async Task DescendMove_TargetInsideSource_RenamesIntoPlace()
        {
            var (book, source, fileId) = await CreateBookOnDiskAsync("descend-src");
            var target = Path.Join(source, "Stephen King");

            var queue = _provider.GetRequiredService<IMoveQueueService>();
            var jobId = await queue.EnqueueMoveAsync(book.Id, target, source);
            var job = await queue.GetJobAsync(jobId);
            Assert.NotNull(job);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job!, CancellationToken.None);

            var finished = await queue.GetJobAsync(jobId);
            Assert.Equal("Completed", finished!.Status);
            Assert.True(string.IsNullOrEmpty(finished.Error), $"unexpected error: {finished.Error}");

            // Files physically renamed into the child folder; nothing lingers
            // beside it in the vacated source.
            Assert.True(File.Exists(Path.Join(target, "Book-001.mp3")));
            Assert.True(File.Exists(Path.Join(target, "Book-002.mp3")));
            Assert.False(File.Exists(Path.Join(source, "Book-001.mp3")));
            Assert.False(Directory.EnumerateFileSystemEntries(source).Any(e => !string.Equals(e, target, StringComparison.Ordinal)));

            // History records the move. (BasePath is set by the enqueue
            // workflow and file rows by the post-move scan — not this
            // processor's job; this test drives the processor directly.)
            var history = await _historyRepository.GetByAudiobookIdAsync(book.Id);
            Assert.Contains(history, h => h.EventType == "Moved");
            _ = fileId; // ownership unchanged by the processor itself
        }

        [Fact]
        [Trait("Method", "ProcessJobAsync")]
        [Trait("Scenario", "SourceInsideTarget_StillBlocked")]
        public async Task UpwardOverlap_SourceInsideTarget_StillFails()
        {
            // The inverse shape (flattening a folder up into its ancestor)
            // remains blocked — the ancestor is populated and the reclaim
            // rules don't apply.
            var parent = FileService.GetTempDirectory("descend-parent");
            var source = Path.Join(parent, "Nested");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Join(source, "Book-001.mp3"), "audio");
            var book = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Nested Book")
                .WithBasePath(source)
                .Build());

            var queue = _provider.GetRequiredService<IMoveQueueService>();
            var jobId = await queue.EnqueueMoveAsync(book.Id, parent, source);
            var job = await queue.GetJobAsync(jobId);

            var processor = _provider.GetRequiredService<IMoveJobProcessor>();
            await processor.ProcessJobAsync(job!, CancellationToken.None);

            var finished = await queue.GetJobAsync(jobId);
            Assert.Equal("Failed", finished!.Status);
            Assert.Contains("overlap", finished.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Join(source, "Book-001.mp3")));
        }
    }
}
