using Listenarr.Domain.Models;

namespace Listenarr.Tests.Builders
{
    public class QueueItemBuilder
    {
        private readonly QueueItem _item = new();

        public QueueItemBuilder()
        {
            _item.Progress = 0;
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

        public QueueItem Build()
        {
            return _item;
        }
    }
}
