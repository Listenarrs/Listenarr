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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Listenarr.Api.Services;
using Listenarr.Infrastructure.Models;
using Listenarr.Domain.Models;
using Listenarr.Application.Repositories;
using Listenarr.Infrastructure.Repositories;

namespace Listenarr.Tests.Features.Api.Services
{
    public class MoveBackgroundServiceTests
    {
        [Fact(Timeout = 20000)]
        public async Task MoveBackgroundService_PerformsMoveAndUpdatesDb()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<ListenArrDbContext>(opts => opts.UseInMemoryDatabase("test_db_move_background"));
            services.AddSingleton<IMoveQueueService, MoveQueueService>();
            services.AddSingleton<MoveBackgroundService>();
            services.AddScoped<IMoveJobRepository, EfMoveJobRepository>();
            services.AddScoped<IAudiobookRepository, AudiobookRepository>();

            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
            var db = provider.GetRequiredService<ListenArrDbContext>();

            // Create source with files
            var src = Path.Join(Path.GetTempPath(), "listenarr_test_src_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(src);
            var nested = Path.Join(src, "Nested");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Join(src, "file1.txt"), "one");
            File.WriteAllText(Path.Join(nested, "file2.txt"), "two");
            // Additionally create a cover image inside the audiobook folder and set ImageUrl to it
            File.WriteAllText(Path.Join(src, "cover.jpg"), "coverdata");

            // Audiobook record uses src and points to the local cover
            var ab = new Audiobook { Title = "MoveTest", BasePath = src, ImageUrl = Path.GetFullPath(Path.Join(src, "cover.jpg")) };
            db.Audiobooks.Add(ab);
            await db.SaveChangesAsync();

            // Snapshot source timestamps before move
            var srcFile1 = Path.Join(src, "file1.txt");
            var srcFile2 = Path.Join(src, "Nested", "file2.txt");
            var srcFile1WriteUtc = File.GetLastWriteTimeUtc(srcFile1);
            var srcFile2WriteUtc = File.GetLastWriteTimeUtc(srcFile2);

            var moveQueue = provider.GetRequiredService<IMoveQueueService>();
            var bg = provider.GetRequiredService<MoveBackgroundService>();

            // Destination
            var dst = Path.Join(Path.GetTempPath(), "listenarr_test_dst_" + Guid.NewGuid().ToString("N"));

            // Start the background service
            await bg.StartAsync(CancellationToken.None);

            // Enqueue move
            var jobId = await moveQueue.EnqueueMoveAsync(ab.Id, dst, src);

            // Poll for job completion (timeout ~15s)
            var succeeded = false;
            for (int i = 0; i < 60; i++)
            {
                if (moveQueue.TryGetJob(jobId, out var job) && string.Equals(job.Status, "Completed", StringComparison.OrdinalIgnoreCase))
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

            // Verify DB base path updated
            using (var scope = scopeFactory.CreateScope())
            {
                var db2 = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();
                var ab2 = await db2.Audiobooks.FindAsync(ab.Id);
                Assert.Equal(Path.GetFullPath(dst), ab2.BasePath);

                // Verify ImageUrl was updated to new location when the cover file exists
                var expectedCover = Path.GetFullPath(Path.Join(dst, "cover.jpg"));
                Assert.Equal(expectedCover, ab2.ImageUrl);
            }

            // Cleanup
            try { Directory.Delete(dst, true); } catch (IOException ex) { _ = ex; } catch (UnauthorizedAccessException ex) { _ = ex; }
        }
    }
}
