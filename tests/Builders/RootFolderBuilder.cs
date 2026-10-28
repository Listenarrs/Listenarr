namespace Listenarr.Tests.Builders
{
    public class RootFolderBuilder
    {
        private RootFolder _rootFolder = new();

        public RootFolderBuilder()
        {
            _rootFolder.Id = TestEntityIdGenerator.Next();
            _rootFolder.CreatedAt = DateTime.UtcNow;
        }

        public RootFolderBuilder WithId(int value)
        {
            _rootFolder.Id = TestEntityIdGenerator.Explicit(value);
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

        public RootFolderBuilder WithCaseSensitivityMode(FileSystemCaseSensitivityMode value)
        {
            _rootFolder.CaseSensitivityMode = value;
            return this;
        }

        public RootFolder Build()
        {
            return _rootFolder;
        }
    }
}
