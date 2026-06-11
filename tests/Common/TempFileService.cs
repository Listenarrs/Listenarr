namespace Listenarr.Tests.Common
{
    public class TempFileService : IAsyncLifetime
    {
        private string? _tempFolder = null;

        public TempFileService()
        {
            _tempFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(_tempFolder);
        }

        public async Task InitializeAsync()
        {
        }

        public async Task DisposeAsync()
        {
            if (_tempFolder != null && Directory.Exists(_tempFolder))
            {
                try
                {
                    Directory.Delete(_tempFolder, true);
                }
                catch (IOException)
                {
                    // FIXME: Folder is probably not deleted
                }
            }
        }

        public string GetTempPath()
        {
            if (_tempFolder == null)
            {
                _tempFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                Directory.CreateDirectory(_tempFolder);
            }

            return _tempFolder;
        }

        public string GetTempDirectory(string directory)
        {
            if (!directory.StartsWith(GetTempPath()))
            {
                directory = Path.Join(GetTempPath(), directory);
            }
            Directory.CreateDirectory(directory);

            return directory;
        }

        public async Task<string> GetFileAsync(string directory, string filename, string content = "test")
        {
            if (!directory.StartsWith(GetTempPath()))
            {
                directory = Path.Join(GetTempPath(), directory);
            }

            var path = Path.Join(directory, filename);
            await File.WriteAllTextAsync(path, content);

            return path;
        }

        public async Task<string> GetTempFileAsync(string filename)
        {
            return await GetFileAsync(GetTempPath(), filename);
        }
    }
}
