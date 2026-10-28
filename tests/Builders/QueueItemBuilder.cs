namespace Listenarr.Tests.Builders
{
    public class QueueItemBuilder
    {
        private readonly QueueItem _item = new();

        public QueueItemBuilder()
        {
            _item.Id = "1";
            _item.Progress = 0;
        }

        public QueueItemBuilder WithId(string value)
        {
            _item.Id = value;
            return this;
        }

        public QueueItemBuilder WithRemotePath(string value)
        {
            _item.RemotePath = value;
            return this;
        }

        public QueueItemBuilder WithSourceFile(string value)
        {
            _item.SourceFiles ??= [];
            _item.SourceFiles.Add(value);

            _item.Progress = 100.0;
            return this;
        }

        public QueueItemBuilder WithContentPath(string value)
        {
            _item.ContentPath = value;
            return this;
        }

        public QueueItemBuilder WithProgress(double value)
        {
            _item.Progress = value;
            return this;
        }

        public QueueItemBuilder WithStatus(string value)
        {
            _item.Status = value;
            return this;
        }

        public QueueItem Build()
        {
            return _item;
        }
    }
}
