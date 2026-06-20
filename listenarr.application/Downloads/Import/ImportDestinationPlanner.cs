using Listenarr.Domain.Common;

namespace Listenarr.Application.Downloads.Import;

public sealed class ImportDestinationPlanner(IFileSystem fileSystem)
{
    public bool TryResolve(string? basePath, string candidatePath, out string destination)
    {
        destination = string.Empty;
        if (string.IsNullOrWhiteSpace(basePath) || string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        return FileUtils.TryResolveRelativePathWithinBase(basePath, candidatePath.Trim(), out destination);
    }

    public async Task<string> ResolveIdempotentOrUniqueAsync(
        string sourcePath,
        string destination,
        ISet<string> usedDestinations,
        CancellationToken cancellationToken = default)
    {
        if (fileSystem.FileExists(destination)
            && await fileSystem.FilesHaveSameContentAsync(sourcePath, destination, cancellationToken))
        {
            usedDestinations.Add(destination);
            return destination;
        }

        var uniqueDestination = FileUtils.GetUniqueDestinationPath(
            destination,
            fileSystem.FileExists,
            usedDestinations);
        usedDestinations.Add(uniqueDestination);
        return uniqueDestination;
    }

    public Task<bool> IsExistingEquivalentAsync(
        string sourcePath,
        string destination,
        CancellationToken cancellationToken = default) =>
        fileSystem.FileExists(destination)
            ? fileSystem.FilesHaveSameContentAsync(sourcePath, destination, cancellationToken)
            : Task.FromResult(false);
}
