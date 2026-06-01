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
using Listenarr.Application.Downloads;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Listenarr.Tests.Features.Application.Downloads
{
    [Trait("Name", "DownloadProcessingJobCleanupServiceTests")]
    [Trait("Category", "DownloadProcessingJob")]
    public class DownloadProcessingJobCleanupServiceTests : BaseTests
    {
        [Fact]
        [Trait("Scenario", "Cleanup removes terminal jobs past the retention window")]
        public async Task RunCleanupAsync_RemovesOldTerminalJobs_KeepsRecentAndActive()
        {
            var beyondRetention = DateTime.UtcNow.AddDays(-(DownloadProcessingJobCleanupService.RetentionDays + 1));
            var withinRetention = DateTime.UtcNow.AddDays(-1);

            // Old completed job -> should be purged
            await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithId("job-old-completed")
                .WithCompleted(at: beyondRetention)
                .Build());

            // Recent completed job -> should be retained
            await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithId("job-recent-completed")
                .WithCompleted(at: withinRetention)
                .Build());

            // Pending job -> never purged regardless of age
            await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithId("job-pending")
                .WithPending(at: beyondRetention)
                .Build());

            var service = _provider.GetRequiredService<DownloadProcessingJobCleanupService>();
            await service.RunCleanupAsync(CancellationToken.None);

            Assert.Null(await _downloadProcessingJobRepository.GetByIdAsync("job-old-completed"));
            Assert.NotNull(await _downloadProcessingJobRepository.GetByIdAsync("job-recent-completed"));
            Assert.NotNull(await _downloadProcessingJobRepository.GetByIdAsync("job-pending"));
        }

        [Fact]
        [Trait("Scenario", "Cleanup is a no-op when nothing is eligible")]
        public async Task RunCleanupAsync_NoEligibleJobs_DoesNothing()
        {
            await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithId("job-recent-completed")
                .WithCompleted(at: DateTime.UtcNow.AddHours(-1))
                .Build());

            var service = _provider.GetRequiredService<DownloadProcessingJobCleanupService>();
            await service.RunCleanupAsync(CancellationToken.None);

            Assert.NotNull(await _downloadProcessingJobRepository.GetByIdAsync("job-recent-completed"));
        }
    }
}
