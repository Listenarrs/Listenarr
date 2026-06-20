using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Repositories
{
    public class DownloadProcessingJobRepositoryTests : BaseTests
    {
        [Fact]
        public async Task RetryCount_ShouldPersist_AfterDbContextRecreation()
        {
            using var connection = new SqliteConnection("Filename=:memory:");
            connection.Open();

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection)
                .Options;

            using (var context = new ListenArrDbContext(options))
            {
                context.Database.EnsureCreated();
                var job = new DownloadProcessingJobBuilder()
                    .WithPending(at: DateTime.UtcNow)
                    .Build();

                context.DownloadProcessingJobs.Add(job);
                await context.SaveChangesAsync();

                job.RetryCount = 4;
                await context.SaveChangesAsync();
            }

            using (var context = new ListenArrDbContext(options))
            {
                var job = await context.DownloadProcessingJobs.FirstOrDefaultAsync();

                Assert.NotNull(job);
                Assert.Equal(4, job.RetryCount);
            }
        }
    }
}
