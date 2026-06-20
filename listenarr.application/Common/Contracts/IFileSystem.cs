namespace Listenarr.Application.Common.Contracts;

public interface IFileSystem
{
    string CurrentDirectory { get; }
    bool FileExists(string path);
    bool DirectoryExists(string path);
    string? GetParentDirectory(string path);
    DateTime GetLastWriteTimeUtc(string path);
    long GetFileLength(string path);
    string ReadAllText(string path);
    void WriteAllText(string path, string contents);
    void CreateDirectory(string path);
    void DeleteDirectory(string path, bool recursive);
    IEnumerable<string> EnumerateFiles(string path);
    IEnumerable<string> EnumerateFileSystemEntries(string path);
    string[] GetFiles(string path, string searchPattern, SearchOption searchOption);
    Task<bool> FilesHaveSameContentAsync(
        string firstPath,
        string secondPath,
        CancellationToken cancellationToken = default);
}
