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

        [Theory]
        [Trait("Method", "ScheduleRetry")]
        [Trait("Scenario", "The doubling stops at a one day ceiling")]
        [InlineData(1, 30, 30)]
        [InlineData(3, 30, 120)]
        [InlineData(12, 30, 61440)]
        [InlineData(13, 30, 86400)]
        [InlineData(20, 600, 86400)]
        public void ScheduleRetry_NeverSchedulesFurtherOutThanADay(int attempt, int initialDelaySeconds, double expectedSeconds)
        {
            // The first three rows are the control: below the ceiling the ladder is untouched, so
            // a clamp that simply pinned every delay to a day would fail them. The last two are
            // the reason the ceiling exists. Both inputs are operator settable, and the download
            // settings screen offers 600 seconds and 20 retries, which without a ceiling puts the
            // twentieth attempt a little over ten years out.
            var job = new DownloadProcessingJobBuilder().Build();
            job.MaxRetries = 25;
            job.RetryCount = attempt - 1;

            var before = DateTime.UtcNow;
            job.ScheduleRetry("failed", initialDelaySeconds: initialDelaySeconds);

            Assert.NotNull(job.NextRetryAt);
            var waited = (job.NextRetryAt!.Value - before).TotalSeconds;
            Assert.InRange(waited, expectedSeconds - 1, expectedSeconds + 5);
        }
    }
}
