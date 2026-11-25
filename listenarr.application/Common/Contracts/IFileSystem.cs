namespace Listenarr.Application.Common.Contracts;

public sealed record FileSystemEntrySnapshot(
    string Name,
    string FullPath,
    bool IsDirectory,
    DateTime LastWriteTime,
    bool IsHidden,
    bool IsSystem);

public sealed record FileSystemRootSnapshot(
    string Name,
    string FullPath,
    DateTime LastWriteTime);

public interface IFileSystem
{
    string CurrentDirectory { get; }
    bool FileExists(string path);
    bool DirectoryExists(string path);
    string? GetParentDirectory(string path);
    DateTime GetLastWriteTimeUtc(string path);
    long GetFileLength(string path);
    bool IsReparsePoint(string path);
    string ReadAllText(string path);
    byte[] ReadAllBytes(string path);
    void WriteAllText(string path, string contents);
    void DeleteFile(string path);
    void CreateDirectory(string path);
    void DeleteDirectory(string path, bool recursive);
    IEnumerable<string> EnumerateFiles(string path);
    IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption);
    IEnumerable<string> EnumerateDirectories(string path);
    IEnumerable<string> EnumerateFileSystemEntries(string path);
    IEnumerable<FileSystemEntrySnapshot> EnumerateEntries(string path);
    IEnumerable<FileSystemRootSnapshot> EnumerateRoots();
    string[] GetFiles(string path, string searchPattern, SearchOption searchOption);
    Task<bool> FilesHaveSameContentAsync(
        string firstPath,
        string secondPath,
        CancellationToken cancellationToken = default);
    bool TryValidateMutationTarget(
        string targetPath,
        IEnumerable<string?> allowedRoots,
        out string normalizedPath,
        out string reason);
    void DeleteEmptyDirectories(string rootPath);
}
