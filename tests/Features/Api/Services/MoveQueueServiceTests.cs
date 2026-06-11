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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Infrastructure.Persistence;
using Listenarr.Application.Audiobooks;

namespace Listenarr.Tests.Features.Api.Services
{
    public class MoveQueueServiceTests
    {
        [Fact]
        public async Task UpdateJobStatus_PersistsAndUpdatesInMemory()
        {
            var dbOpts = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase("test_db_movejob_" + Guid.NewGuid().ToString("N"))
                .Options;
            var db = new ListenArrDbContext(dbOpts);

            var services = new ServiceCollection();
            services.AddSingleton(db);
            services.AddScoped<IMoveJobRepository>(_ => new EfMoveJobRepository(db));
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
            var logger = new NullLogger<MoveQueueService>();

            var svc = new MoveQueueService(logger, scopeFactory);

            // Enqueue a job (creates DB entry)
            var jobId = await svc.EnqueueMoveAsync(1, "C:\\dest\\path", "C:\\src\\path");

            // Initially the job should be queued
            Assert.True(svc.TryGetJob(jobId, out var job1));
            Assert.Equal("Queued", job1!.Status);

            // Update status to Processing
            svc.UpdateJobStatus(jobId, "Processing", null);
            Assert.True(svc.TryGetJob(jobId, out var job2));
            Assert.Equal("Processing", job2!.Status);

            // Verify persisted in DB
            using (var scope = scopeFactory.CreateScope())
            {
                var verifyDb = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();
                var dbJob = await verifyDb.MoveJobs.FindAsync(jobId);
                Assert.NotNull(dbJob);
                Assert.Equal("Processing", dbJob!.Status);
            }
        }
    }
}
