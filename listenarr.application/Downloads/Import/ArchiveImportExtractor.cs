using Listenarr.Application.Common;
using Listenarr.Domain.Common;

namespace Listenarr.Application.Downloads.Import;

public sealed class ArchiveImportExtractor(
    IArchiveExtractor archiveExtractor,
    IFileSystem fileSystem)
{
    private readonly List<TempDirectory> _temporaryDirectories = [];

    /// <summary>
    /// The temporary directories archives were extracted into, one per archive. Each one is a
    /// source root in its own right: the files under it came out of a single archive and their
    /// position relative to it is the structure the archive shipped, which is the only structure
    /// worth reproducing at the destination. The batch as a whole has no such structure once it
    /// mixes these with the files that stayed in the download directory.
    /// </summary>
    public IReadOnlyList<string> ExtractionRoots =>
        [.. _temporaryDirectories.Select(directory => FileUtils.NormalizeStoredPath(directory.Path))];

    public async Task<List<string>> ExtractAsync(IEnumerable<string> archives)
    {
        List<string> files = [];
        foreach (var archive in archives)
        {
            try
            {
                var archiveDirectory = await archiveExtractor.ExtractArchiveToTempDirAsync(archive);
                if (archiveDirectory == null)
                {
                    continue;
                }

                _temporaryDirectories.Add(archiveDirectory);
                files.AddRange(fileSystem
                    .GetFiles(archiveDirectory.Path, "*", SearchOption.AllDirectories)
                    .Select(file => FileUtils.NormalizeStoredPath(file)));
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                throw new IOException($"Unable to extract {archive}");
            }
        }

        return files;
    }

    public void DisposeTemporaryDirectories()
    {
        foreach (var directory in _temporaryDirectories)
        {
            directory.Dispose();
        }

        _temporaryDirectories.Clear();
    }
}
