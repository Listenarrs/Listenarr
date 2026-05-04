using Listenarr.Domain.Models;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Xunit;

namespace Listenarr.Tests.Features.Domain.Models
{
    public class DownloadProcessingJob : BaseTests
    {
        [Fact]
        public async Task ScheduleRetry_FirstAttempt_KeepsImportPendingForRetry()
        {
            var job = new DownloadProcessingJobBuilder()
                .Build();

            job.ScheduleRetry();

            Assert.Equal(1, job.RetryCount);
            Assert.Equal(ProcessingJobStatus.Pending, job.Status);

            job.RetryCount = job.MaxRetries;

            job.ScheduleRetry();

            Assert.True(job.RetryCount >= job.MaxRetries);
            Assert.Equal(ProcessingJobStatus.Failed, job.Status);
        }
    }
}
