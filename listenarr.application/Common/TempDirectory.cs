namespace Listenarr.Application.Common
{
    public sealed class TempDirectory : IDisposable
    {
        public string Path { get; }
        public TempDirectory(string path) => Path = path;

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
