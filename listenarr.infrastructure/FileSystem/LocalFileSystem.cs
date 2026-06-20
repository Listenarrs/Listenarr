namespace Listenarr.Infrastructure.FileSystem;

public sealed class LocalFileSystem : IFileSystem
{
    public string CurrentDirectory => Directory.GetCurrentDirectory();

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string? GetParentDirectory(string path) => Directory.GetParent(path)?.FullName;

    public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);

    public long GetFileLength(string path) => new FileInfo(path).Length;

    public string ReadAllText(string path) => File.ReadAllText(path);

    public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);

    public IEnumerable<string> EnumerateFiles(string path) => Directory.EnumerateFiles(path);

    public IEnumerable<string> EnumerateFileSystemEntries(string path) =>
        Directory.EnumerateFileSystemEntries(path);

    public string[] GetFiles(string path, string searchPattern, SearchOption searchOption) =>
        Directory.GetFiles(path, searchPattern, searchOption);

    public async Task<bool> FilesHaveSameContentAsync(
        string firstPath,
        string secondPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(firstPath) || !File.Exists(secondPath))
        {
            return false;
        }

        var firstInfo = new FileInfo(firstPath);
        var secondInfo = new FileInfo(secondPath);
        if (firstInfo.Length != secondInfo.Length)
        {
            return false;
        }

        await using var firstStream = File.Open(firstPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var secondStream = File.Open(secondPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var firstBuffer = new byte[81920];
        var secondBuffer = new byte[81920];

        while (true)
        {
            var firstRead = await firstStream.ReadAsync(firstBuffer, cancellationToken);
            var secondRead = await secondStream.ReadAsync(secondBuffer, cancellationToken);
            if (firstRead != secondRead)
            {
                return false;
            }

            if (firstRead == 0)
            {
                return true;
            }

            if (!firstBuffer.AsSpan(0, firstRead).SequenceEqual(secondBuffer.AsSpan(0, secondRead)))
            {
                return false;
            }
        }
    }
}
