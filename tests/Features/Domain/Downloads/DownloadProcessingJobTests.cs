using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Domain.Downloads
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

        [Fact]
        [Trait("Method", "ScheduleRetry")]
        [Trait("Scenario", "The first retry waits the configured delay, not double it")]
        public void ScheduleRetry_FirstRetry_WaitsTheConfiguredDelay()
        {
            var job = new DownloadProcessingJobBuilder().Build();

            var before = DateTime.UtcNow;
            job.ScheduleRetry("source not ready", initialDelaySeconds: 30);

            Assert.NotNull(job.NextRetryAt);
            var waited = job.NextRetryAt!.Value - before;

            // 30s, not the 60s the old expression produced by reading RetryCount after
            // incrementing it. Both comments on that expression claimed 30 and neither matched it.
            Assert.InRange(waited.TotalSeconds, 29, 35);
        }

        [Fact]
        [Trait("Method", "ScheduleRetry")]
        [Trait("Scenario", "The configured delay doubles per retry")]
        public void ScheduleRetry_SecondRetry_DoublesTheConfiguredDelay()
        {
            var job = new DownloadProcessingJobBuilder().Build();
            job.MaxRetries = 5;

            job.ScheduleRetry("first", initialDelaySeconds: 10);
            var before = DateTime.UtcNow;
            job.ScheduleRetry("second", initialDelaySeconds: 10);

            Assert.NotNull(job.NextRetryAt);
            var waited = job.NextRetryAt!.Value - before;
            Assert.InRange(waited.TotalSeconds, 19, 25);
        }

        [Fact]
        [Trait("Method", "ScheduleRetry")]
        [Trait("Scenario", "A caller that supplies no delay is unchanged")]
        public void ScheduleRetry_WithoutADelay_UsesTheSettingsDefault()
        {
            // The control on the default. ApplicationSettings.MissingSourceRetryInitialDelaySeconds
            // defaults to 30, and so must this, or an unconverted caller changes behaviour silently.
            var job = new DownloadProcessingJobBuilder().Build();

            var before = DateTime.UtcNow;
            job.ScheduleRetry("no delay supplied");

            Assert.NotNull(job.NextRetryAt);
            Assert.InRange((job.NextRetryAt!.Value - before).TotalSeconds, 29, 35);
        }
    }
}
