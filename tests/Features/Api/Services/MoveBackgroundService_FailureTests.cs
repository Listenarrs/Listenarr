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
    public class MoveBackgroundService_FailureTests
    {
        private static void TryDeleteFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                Directory.Delete(path, true);
            }
            catch (IOException ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }
        }

        [Fact(Timeout = 20000)]
        public async Task MoveBackgroundService_Fails_WhenFileLocked_IncrementsAttemptCount()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<ListenArrDbContext>(opts => opts.UseInMemoryDatabase("test_db_move_failure"));
            services.AddSingleton<IMoveQueueService, MoveQueueService>();
            services.AddSingleton<MoveBackgroundService>();
            services.AddScoped<IMoveJobRepository, EfMoveJobRepository>();
            services.AddScoped<IAudiobookRepository, AudiobookRepository>();

            var provider = services.BuildServiceProvider();
            var db = provider.GetRequiredService<ListenArrDbContext>();
            var moveQueue = provider.GetRequiredService<IMoveQueueService>();
            var bg = provider.GetRequiredService<MoveBackgroundService>();

            // Create source with a file
            var src = Path.Join(Path.GetTempPath(), "listenarr_test_src_lock_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(src);
            var file = Path.Join(src, "file_locked.txt");
            File.WriteAllText(file, "locked");

            var dst = Path.Join(Path.GetTempPath(), "listenarr_test_dst_lock_" + Guid.NewGuid().ToString("N"));

            // Create a blocking file at the destination path so Directory.Move will fail
            var dstParent = Path.GetDirectoryName(dst) ?? Path.GetTempPath();
            Directory.CreateDirectory(dstParent);
            File.WriteAllText(dst, "block");

            var ab = new Audiobook { Title = "MoveFailTest", BasePath = src };
            db.Audiobooks.Add(ab);
            await db.SaveChangesAsync();

            // Start background service
            await bg.StartAsync(CancellationToken.None);

            var jobId = await moveQueue.EnqueueMoveAsync(ab.Id, dst, src);

            // Wait for job to fail
            var failed = false;
            for (int i = 0; i < 60; i++)
            {
                if (moveQueue.TryGetJob(jobId, out var job) && string.Equals(job.Status, "Failed", StringComparison.OrdinalIgnoreCase))
                {
                    failed = true; break;
                }
                await Task.Delay(200, CancellationToken.None);
            }

            await bg.StopAsync(CancellationToken.None);

            Assert.True(failed, "Move job did not fail as expected when file was locked");

            // Check attempt count incremented in DB
            using (var scope = provider.GetRequiredService<IServiceScopeFactory>().CreateScope())
            {
                var db2 = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();
                var dbJob = await db2.MoveJobs.FindAsync(jobId);
                Assert.True(dbJob.AttemptCount > 0, "AttemptCount was not incremented on failure");
            }

            // Cleanup
            TryDeleteFile(dst);
            TryDeleteDirectory(src);
            TryDeleteDirectory(dst);
        }
    }
}
