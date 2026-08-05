namespace Listenarr.Tests.Common
{
    public class TempFileService : IAsyncLifetime
    {
        private string? _tempFolder = null;
        private readonly List<string> _additionalTempFolders = [];

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
            if (_tempFolder != null)
            {
                TryDeleteTempFolder(_tempFolder);
            }

            foreach (var additionalTempFolder in _additionalTempFolders)
            {
                TryDeleteTempFolder(additionalTempFolder);
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
            if (!Path.IsPathFullyQualified(directory)
                && !directory.StartsWith(GetTempPath(), StringComparison.Ordinal))
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

        public string GetWindowsRootRelativeTempPath(string name)
        {
            var target = WindowsPathTestFixture
                .GetRootRelativeAliasCompatiblePath(name);
            _additionalTempFolders.Add(target);
            return target;
        }

        public string GetWindowsRootRelativeTempDirectory(string directory)
        {
            var target = GetWindowsRootRelativeTempPath(directory);
            Directory.CreateDirectory(target);
            return target;
        }

        public static string GetWindowsRootRelativeForeignAlias(string nativePath) =>
            WindowsPathTestFixture.GetRootRelativeForeignAlias(nativePath);

        private static void TryDeleteTempFolder(string directory)
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException)
            {
                // FIXME: Folder is probably not deleted
            }
        }
    }
}
