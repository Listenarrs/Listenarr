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
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving
{
    public class MoveBackgroundServiceTests : BaseTests
    {
        [Fact(Timeout = 20000)]
        public async Task MoveBackgroundService_PerformsMoveAndUpdatesDb()
        {
            // Create source with files
            var src = FileService.GetTempDirectory("listenarr_test_src");
            var nested = Path.Join(src, "Nested");
            Directory.CreateDirectory(nested);

            await FileService.GetFileAsync(src, "file1.txt", "one");
            await FileService.GetFileAsync(nested, "file2.txt", "two");
            // Additionally create a cover image inside the audiobook folder and set ImageUrl to it
            await FileService.GetFileAsync(src, "cover.jpg", "coverdata");

            // Audiobook record uses src and points to the local cover
            var ab = await _audiobookRepository.AddAsync(new Audiobook { Title = "MoveTest", BasePath = src, ImageUrl = Path.GetFullPath(Path.Join(src, "cover.jpg")) });

            // Snapshot source timestamps before move
            var srcFile1 = Path.Join(src, "file1.txt");
            var srcFile2 = Path.Join(src, "Nested", "file2.txt");
            var srcFile1WriteUtc = File.GetLastWriteTimeUtc(srcFile1);
            var srcFile2WriteUtc = File.GetLastWriteTimeUtc(srcFile2);

            var moveQueue = _provider.GetRequiredService<IMoveQueueService>();
            var bg = _provider.GetRequiredService<MoveBackgroundService>();

            // Destination
            var dst = FileService.GetTempDirectory("listenarr_test_dst");

            // Start the background service
            await bg.StartAsync(CancellationToken.None);

            // Enqueue move
            var jobId = await moveQueue.EnqueueMoveAsync(ab.Id, dst, src);

            // Poll for job completion (timeout ~15s)
            var succeeded = false;
            for (int i = 0; i < 60; i++)
            {
                var job = await moveQueue.GetJobAsync(jobId);
                if (job != null && string.Equals(job.Status, "Completed", StringComparison.OrdinalIgnoreCase))
                {
                    succeeded = true; break;
                }
                await Task.Delay(250, CancellationToken.None);
            }

            // Stop background service
            await bg.StopAsync(CancellationToken.None);

            Assert.True(succeeded, "Move job did not complete in time");

            // Verify destination has files and source removed
            Assert.True(Directory.Exists(dst));
            Assert.True(File.Exists(Path.Join(dst, "file1.txt")));
            Assert.True(File.Exists(Path.Join(dst, "Nested", "file2.txt")));
            Assert.False(Directory.Exists(src));

            // Verify timestamps preserved (took snapshots before move)
            var dstFile1 = Path.Join(dst, "file1.txt");
            var dstFile2 = Path.Join(dst, "Nested", "file2.txt");

            Assert.Equal(srcFile1WriteUtc, File.GetLastWriteTimeUtc(dstFile1));
            Assert.Equal(srcFile2WriteUtc, File.GetLastWriteTimeUtc(dstFile2));

            using var scope = _provider.CreateScope();
            _audiobookRepository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            var ab2 = await _audiobookRepository.GetByIdAsync(ab.Id);

            // Verify ImageUrl was updated to new location when the cover file exists
            var expectedCover = Path.GetFullPath(Path.Join(dst, "cover.jpg"));
            Assert.Equal(expectedCover, ab2.ImageUrl);
        }
    }
}
