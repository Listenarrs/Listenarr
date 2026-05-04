using Listenarr.Domain.Models;

namespace Listenarr.Tests.Builders
{
    public class DownloadProcessingJobBuilder
    {
        private DownloadProcessingJob _job = new();
        private Download _download = null!;

        public DownloadProcessingJobBuilder()
        {
            _download = new DownloadBuilder().Build();

            _job.Id = Guid.NewGuid().ToString();
            _job.CreatedAt = DateTime.UtcNow;
            _job.Status = ProcessingJobStatus.Pending;
            _job.JobType = ProcessingJobType.MoveOrCopyFile;
        }

        public DownloadProcessingJobBuilder WithId(string value)
        {
            _job.Id = value;
            return this;
        }

        public DownloadProcessingJobBuilder WithDownload(Download value)
        {
            _download = value;
            return this;
        }

        public DownloadProcessingJobBuilder WithStatus(ProcessingJobStatus value)
        {
            _job.Status = value;
            return this;
        }

        public DownloadProcessingJobBuilder WithStartedDate(DateTime value)
        {
            _job.StartedAt = value;
            return this;
        }

        public DownloadProcessingJobBuilder WithCompletedDate(DateTime value)
        {
            _job.CompletedAt = value;
            return this;
        }

        public DownloadProcessingJobBuilder WithCreatedAt(DateTime value)
        {
            _job.CreatedAt = value;
            return this;
        }

        public DownloadProcessingJobBuilder WithPending(DateTime at)
        {
            _job.Status = ProcessingJobStatus.Pending;
            _job.CreatedAt = at;
            return this;
        }

        public DownloadProcessingJobBuilder WithProcessing(DateTime at)
        {
            _job.Status = ProcessingJobStatus.Processing;
            _job.StartedAt = at;
            return this;
        }

        public DownloadProcessingJobBuilder WithCompleted(DateTime at)
        {
            _job.Status = ProcessingJobStatus.Completed;
            _job.CompletedAt = at;
            return this;
        }

        public DownloadProcessingJob Build()
        {
            _job.DownloadId = _download.Id;
            return _job;
        }
    }
}
