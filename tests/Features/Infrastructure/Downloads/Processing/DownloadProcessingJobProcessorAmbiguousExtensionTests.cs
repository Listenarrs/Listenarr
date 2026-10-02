using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks;

namespace Listenarr.Tests.Features.Infrastructure.Downloads.Processing
{
    // Split out of DownloadProcessingJobProcessorTests so this branch does not touch a file
    // owned by the rewrite in upstream PR 993. Test content is unchanged.
    [Trait("Name", "DownloadProcessingJobProcessorAmbiguousExtensionTests")]
    [Trait("Category", "DownloadProcessingJob")]
    public class DownloadProcessingJobProcessorAmbiguousExtensionTests : BaseTests
    {
        private readonly DownloadClientGatewayMock downloadClientGatewayMock = new();

        public override async Task InitializeAsync()
        {
            _services.AddSingleton<IDownloadClientGateway>(downloadClientGatewayMock);
            Init();
            await AddAuthorizedRootAsync(FileService.GetTempPath());
        }

        // Listenarr#890. A batch whose only file carries an extension outside
        // FileUtils.AudioExtensions plans zero audio imports, so every file is demoted to a
        // companion and skipped, nothing is registered, and the job is failed. .mp4 is the
        // extension the reporters hit: it is not blacklisted, and the bytes may be the same
        // MP4/AAC a .m4b would carry, but no gate looks past Path.GetExtension.
        // The two exact strings are asserted because a generic "the job failed" assertion is
        // what let this reach users. The control for this case is the test below it, where the
        // same batch against an audiobook that already has a file row does not block.
        [Fact]
        [Trait("Scenario", "NoAudioCandidateBlocksImport")]
        public async Task Import_BatchWithNoAudioExtension_BlocksDownloadWithNoFilesRegisteredReason()
        {
            // Given
            var sourceDirectory = FileService.GetTempDirectory("no-audio-candidate-source");
            var filePath = await FileService.GetFileAsync(sourceDirectory, "Target Book.mp4");
            Assert.False(FileUtils.IsAudioFile(filePath));
            downloadClientGatewayMock.SourceFiles = [filePath];

            var audiobook = await CreateAudiobook();
            Assert.Empty(await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));

            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(audiobook)
                .WithDownloadClientConfiguration(await CreateDownloadClientConfiguration())
                .WithPath(sourceDirectory)
                .WithCompletedStatus(at: DateTime.UtcNow)
                .Build());

            var job = await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithDownload(download)
                .Build());

            // When
            await _provider.GetRequiredService<DownloadProcessingJobProcessor>()
                .ProcessQueueAsync(CancellationToken.None);

            // Then
            job = await _downloadProcessingJobRepository.GetByIdAsync(job.Id);
            Assert.NotNull(job);
            Assert.Equal(ProcessingJobStatus.Failed, job.Status);
            Assert.Equal("No audio files were registered after file import", job.ErrorMessage);
            Assert.Contains(
                job.ProcessingLog,
                entry => entry.Contains("No successful audio import in batch", StringComparison.Ordinal));
            Assert.Contains(
                job.ProcessingLog,
                entry => entry.Contains(
                    "Job failed: No audio files were registered after file import",
                    StringComparison.Ordinal));
            Assert.Empty(await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));

            download = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.NotNull(download);
            Assert.Equal(DownloadStatus.ImportBlocked, download.Status);
            Assert.Equal("Unable to import the download", download.ImportBlockReason);
            Assert.NotNull(download.ImportBlockMessages);
            Assert.Contains(
                download.ImportBlockMessages,
                message => message == $"See the log of job {job.Id} for more information");
        }

        // The control for the test above, and the half that must come out differently. The
        // no-audio-registered failure is conditional on the audiobook having no AudiobookFile
        // rows, so the identical batch against an audiobook that already holds a file imports
        // nothing and still does not block. If both cases blocked, or neither did, the test
        // above would prove nothing about the condition.
        [Fact]
        [Trait("Scenario", "NoAudioCandidateDoesNotBlockWhenAudiobookHasAFile")]
        public async Task Import_BatchWithNoAudioExtension_DoesNotBlockWhenAudiobookAlreadyHasAFile()
        {
            // Given
            var sourceDirectory = FileService.GetTempDirectory("no-audio-candidate-existing-source");
            var filePath = await FileService.GetFileAsync(sourceDirectory, "Target Book.mp4");
            Assert.False(FileUtils.IsAudioFile(filePath));
            downloadClientGatewayMock.SourceFiles = [filePath];

            var audiobook = await CreateAudiobook();
            var existingFile = await FileService.GetFileAsync(
                FileService.GetTempDirectory("existing-library-file"),
                "Existing Book.m4b");
            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(audiobook)
                .WithPath(existingFile)
                .WithFormat("m4b")
                .WithSize(4)
                .Build());
            Assert.NotEmpty(await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));

            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(audiobook)
                .WithDownloadClientConfiguration(await CreateDownloadClientConfiguration())
                .WithPath(sourceDirectory)
                .WithCompletedStatus(at: DateTime.UtcNow)
                .Build());

            var job = await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithDownload(download)
                .Build());

            // When
            await _provider.GetRequiredService<DownloadProcessingJobProcessor>()
                .ProcessQueueAsync(CancellationToken.None);

            // Then
            job = await _downloadProcessingJobRepository.GetByIdAsync(job.Id);
            Assert.NotNull(job);
            Assert.Equal(ProcessingJobStatus.Completed, job.Status);
            Assert.DoesNotContain(
                job.ProcessingLog,
                entry => entry.Contains(
                    "No audio files were registered after file import",
                    StringComparison.Ordinal));

            // The same demotion ran, so only the existing-file condition separates this case
            // from the blocked one. Without this entry the two tests would not be comparable.
            Assert.Contains(
                job.ProcessingLog,
                entry => entry.Contains("No successful audio import in batch", StringComparison.Ordinal));

            download = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.NotNull(download);
            Assert.Equal(DownloadStatus.Moved, download.Status);
            Assert.Null(download.ImportBlockReason);
            Assert.Empty(download.ImportBlockMessages ?? []);

            // Nothing new was imported, which is the part both halves share. Only the existing
            // row is still there.
            Assert.Single(await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
        }

        // The no-audio-registered path calls FailImportAsync rather than ScheduleRetryAsync, so
        // it spends none of the MaxRetries budget and flips the download to Import Blocked on the
        // first processing cycle. That is what separates it from the retrying gates above, where
        // the same download sits at Pending with RetryCount 1 and only blocks once the budget is
        // exhausted, and it is why the time between Completed and Import Blocked is diagnostic.
        [Fact]
        [Trait("Scenario", "NoAudioCandidateFailsWithoutSpendingTheRetryBudget")]
        public async Task Import_BatchWithNoAudioExtension_FailsWithoutSchedulingARetry()
        {
            // Given
            var sourceDirectory = FileService.GetTempDirectory("no-audio-candidate-noretry-source");
            var filePath = await FileService.GetFileAsync(sourceDirectory, "Target Book.mp4");
            downloadClientGatewayMock.SourceFiles = [filePath];

            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(await CreateAudiobook())
                .WithDownloadClientConfiguration(await CreateDownloadClientConfiguration())
                .WithPath(sourceDirectory)
                .WithCompletedStatus(at: DateTime.UtcNow)
                .Build());

            var job = await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithDownload(download)
                .Build());
            Assert.Equal(3, job.MaxRetries);

            // When
            await _provider.GetRequiredService<DownloadProcessingJobProcessor>()
                .ProcessQueueAsync(CancellationToken.None);

            // Then
            job = await _downloadProcessingJobRepository.GetByIdAsync(job.Id);
            Assert.NotNull(job);
            Assert.Equal(ProcessingJobStatus.Failed, job.Status);
            Assert.Equal(0, job.RetryCount);
            Assert.Null(job.NextRetryAt);
            Assert.DoesNotContain(
                job.ProcessingLog,
                entry => entry.Contains("Scheduled for retry", StringComparison.Ordinal));
            Assert.NotEqual("Max retries (3) exceeded", job.ErrorMessage);

            download = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.NotNull(download);
            Assert.Equal(DownloadStatus.ImportBlocked, download.Status);
        }
    }
}
