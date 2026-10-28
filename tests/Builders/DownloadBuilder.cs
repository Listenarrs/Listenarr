namespace Listenarr.Tests.Builders
{
    public class DownloadBuilder
    {
        private readonly Download _download = new();

        public DownloadBuilder()
        {
            _download.Id = Guid.NewGuid().ToString();
            _download.StartedAt = DateTime.UtcNow;
            _download.Status = DownloadStatus.Queued;
            _download.DownloadPath = Directory.GetCurrentDirectory();
            _download.FinalPath = Directory.GetCurrentDirectory();
            _download.Metadata = [];
            _download.ImportBlockMessages = [];
        }

        public DownloadBuilder WithId(string value)
        {
            _download.Id = value;
            return this;
        }

        public DownloadBuilder WithDownloadClientConfiguration(DownloadClientConfiguration value)
        {
            _download.DownloadClientId = value.Id;
            return this;
        }

        public DownloadBuilder WithStatus(DownloadStatus value)
        {
            _download.Status = value;
            return this;
        }

        public DownloadBuilder WithCompletedStatus(DateTime at)
        {
            _download.Status = DownloadStatus.Completed;
            _download.CompletedAt = at;
            return this;
        }

        public DownloadBuilder WithBlockedStatus(string value)
        {
            _download.Status = DownloadStatus.ImportBlocked;
            _download.ImportBlockReason = value;
            return this;
        }

        public DownloadBuilder WithStartDate(DateTime value)
        {
            _download.StartedAt = value;
            return this;
        }

        public DownloadBuilder WithPath(string value)
        {
            _download.DownloadPath = value;
            _download.FinalPath = value;
            return this;
        }

        public DownloadBuilder WithTorrentHash(string value)
        {
            _download.Metadata["TorrentHash"] = value;
            return this;
        }

        public DownloadBuilder WithUploader(string value)
        {
            _download.Metadata["Uploader"] = value;
            return this;
        }

        public DownloadBuilder WithProtocol(DownloadProtocol value)
        {
            _download.Metadata["Protocol"] = value;
            return this;
        }

        public DownloadBuilder WithTitle(string value)
        {
            _download.Title = value;
            return this;
        }

        public DownloadBuilder WithArtist(string value)
        {
            _download.Artist = value;
            return this;
        }

        public DownloadBuilder WithSeries(string? value)
        {
            _download.Series = value;
            return this;
        }

        public DownloadBuilder WithAudiobookId(int value)
        {
            _download.AudiobookId = value;
            return this;
        }

        public DownloadBuilder WithAudiobook(Audiobook value)
        {
            _download.AudiobookId = value.Id;
            return this;
        }

        public DownloadBuilder WithBlockReason(string value)
        {
            _download.ImportBlockReason = value;
            return this;
        }

        public DownloadBuilder WithBlockMessage(string value)
        {
            _download.ImportBlockMessages.Add(value);
            return this;
        }

        public DownloadBuilder WithImportAttempts(int value)
        {
            _download.ImportAttempts = value;
            return this;
        }

        public DownloadBuilder WithProgress(decimal value)
        {
            _download.Progress = value;
            return this;
        }

        public DownloadBuilder WithDownloading(decimal value)
        {
            _download.Status = DownloadStatus.Downloading;
            WithProgress(value);
            return this;
        }

        public DownloadBuilder WithExternalId(string value)
        {
            _download.SetExternalId(value);
            return this;
        }

        public DownloadBuilder WithClientDownloadId(string value)
            => WithExternalId(value);

        public Download Build()
        {
            return _download;
        }
    }
}
