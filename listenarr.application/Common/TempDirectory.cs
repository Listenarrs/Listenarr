namespace Listenarr.Application.Common
{
    public sealed class TempDirectory : IDisposable
    {
        private readonly Action<string> _cleanup;

        public string Path { get; }
        public TempDirectory(string path, Action<string> cleanup)
        {
            Path = path;
            _cleanup = cleanup;
        }

        public void Dispose() => _cleanup(Path);
    }
}
