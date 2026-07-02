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
using Listenarr.Application.Audiobooks.Verification;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Audiobooks.Verification
{
    public class LibraryVerificationQueueServiceTests
    {
        private static LibraryVerificationQueueService CreateService() =>
            new(new NullLogger<LibraryVerificationQueueService>());

        [Fact]
        public async Task TryCancel_QueuedJob_SignalsItsCancelToken()
        {
            var service = CreateService();
            var jobId = await service.EnqueueAsync(new List<int> { 1, 2 });

            Assert.True(service.TryCancel(jobId));

            Assert.True(service.TryGetJob(jobId, out var job));
            Assert.True(job!.CancelSource.IsCancellationRequested);
        }

        [Fact]
        public void TryCancel_UnknownJob_ReturnsFalse()
        {
            var service = CreateService();
            Assert.False(service.TryCancel(Guid.NewGuid()));
        }

        [Theory]
        [InlineData("Completed")]
        [InlineData("Failed")]
        [InlineData("Cancelled")]
        public async Task TryCancel_FinishedJob_ReturnsFalse(string terminalStatus)
        {
            var service = CreateService();
            var jobId = await service.EnqueueAsync(new List<int> { 1 });
            Assert.True(service.TryGetJob(jobId, out var job));
            job!.Status = terminalStatus;

            Assert.False(service.TryCancel(jobId));
            Assert.False(job.CancelSource.IsCancellationRequested);
        }

        [Fact]
        public async Task EnqueueAsync_StampsTrigger()
        {
            var service = CreateService();
            var jobId = await service.EnqueueAsync(new List<int> { 1 }, VerificationTriggers.Import);

            Assert.True(service.TryGetJob(jobId, out var job));
            Assert.Equal(VerificationTriggers.Import, job!.Trigger);
        }
    }
}
