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

    public void DeleteFile(string path) => File.Delete(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);

    public IEnumerable<string> EnumerateFiles(string path) => Directory.EnumerateFiles(path);

    public IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption) =>
        Directory.EnumerateFiles(path, searchPattern, searchOption);

    public IEnumerable<string> EnumerateFileSystemEntries(string path) =>
        Directory.EnumerateFileSystemEntries(path);

    public string[] GetFiles(string path, string searchPattern, SearchOption searchOption) =>
        Directory.GetFiles(path, searchPattern, searchOption);

    public Task<bool> FilesHaveSameContentAsync(
        string firstPath,
        string secondPath,
        CancellationToken cancellationToken = default) =>
        FileSystemSafety.FilesHaveSameContentAsync(firstPath, secondPath, cancellationToken);

    public bool TryValidateMutationTarget(
        string targetPath,
        IEnumerable<string?> allowedRoots,
        out string normalizedPath,
        out string reason) =>
        FileSystemSafety.TryValidateMutationTarget(
            targetPath,
            allowedRoots,
            out normalizedPath,
            out reason);

    public void DeleteEmptyDirectories(string rootPath) =>
        FileSystemSafety.DeleteEmptyDirectories(rootPath);
}
