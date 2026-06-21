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
    public class MoveBackgroundService_FailureTests : BaseTests
    {
        [Fact(Timeout = 20000)]
        public async Task MoveBackgroundService_Fails_WhenFileLocked_IncrementsAttemptCount()
        {
            var moveQueue = _provider.GetRequiredService<IMoveQueueService>();
            var bg = _provider.GetRequiredService<MoveBackgroundService>();

            // Create source with a file
            var src = FileService.GetTempDirectory("listenarr_test_src_lock");
            var file = await FileService.GetFileAsync(src, "file_locked.txt", "locked");

            var dst = await FileService.GetFileAsync(FileService.GetTempPath(), "listenarr_test_dst_lock", "block");

            var ab = new Audiobook { Title = "MoveFailTest", BasePath = src };
            await _audiobookRepository.AddAsync(ab);

            // Start background service
            await bg.StartAsync(CancellationToken.None);

            var jobId = await moveQueue.EnqueueMoveAsync(ab.Id, dst, src);

            // Wait for job to fail
            var failed = false;
            for (int i = 0; i < 60; i++)
            {
                var job = await moveQueue.GetJobAsync(jobId);
                if (job != null && string.Equals(job.Status, "Failed", StringComparison.OrdinalIgnoreCase))
                {
                    failed = true; break;
                }
                await Task.Delay(200, CancellationToken.None);
            }

            await bg.StopAsync(CancellationToken.None);

            Assert.True(failed, "Move job did not fail as expected when file was locked");

            // Check attempt count incremented in DB
            var dbJob = await _moveJobRepository.GetByIdAsync(jobId);
            Assert.True(dbJob.AttemptCount > 0, "AttemptCount was not incremented on failure");
        }
    }
}
