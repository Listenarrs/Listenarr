namespace Listenarr.Tests.Builders
{
    public class RootFolderBuilder
    {
        private static int IdCounter = 0;
        private RootFolder _rootFolder = new();

        public RootFolderBuilder()
        {
            _rootFolder.Id = ++IdCounter;
            _rootFolder.CreatedAt = DateTime.UtcNow;
        }

        public RootFolderBuilder WithId(int value)
        {
            _rootFolder.Id = value;
            return this;
        }

        public RootFolderBuilder WithName(string value)
        {
            _rootFolder.Name = value;
            return this;
        }

        public RootFolderBuilder WithPath(string value)
        {
            _rootFolder.Path = value;
            return this;
        }

        public RootFolderBuilder WithIsDefault()
        {
            _rootFolder.IsDefault = true;
            return this;
        }

        public RootFolderBuilder WithoutIsDefault()
        {
            _rootFolder.IsDefault = false;
            return this;
        }

        public RootFolder Build()
        {
            return _rootFolder;
        }
    }
}
