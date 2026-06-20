using Listenarr.Application.Common;
using Listenarr.Domain.Common;

namespace Listenarr.Application.Downloads.Import;

public sealed class ArchiveImportExtractor(
    IArchiveExtractor archiveExtractor,
    IFileSystem fileSystem)
{
    private readonly List<TempDirectory> _temporaryDirectories = [];

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
