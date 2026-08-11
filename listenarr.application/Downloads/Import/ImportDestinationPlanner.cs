using Listenarr.Domain.Common;

namespace Listenarr.Application.Downloads.Import;

public sealed class ImportDestinationPlanner(IFileSystem fileSystem)
{
    public bool TryResolve(
        string? basePath,
        string candidatePath,
        FileSystemPathSemantics destinationSemantics,
        out string destination)
    {
        destination = string.Empty;
        if (string.IsNullOrWhiteSpace(basePath) || string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        return FileSystemPathIdentity.TryResolveRelativePathWithinBase(
            basePath,
            candidatePath,
            destinationSemantics,
            out destination);
    }

    public async Task<ImportDestinationReservation> PlanIdempotentOrUniqueAsync(
        string sourcePath,
        string destination,
        ISet<string> usedDestinations,
        FileSystemPathSemantics destinationSemantics,
        CancellationToken cancellationToken = default)
    {
        if (destinationSemantics.CaseSensitivity == FileSystemCaseSensitivity.Unknown)
        {
            throw new InvalidOperationException("Destination filesystem case sensitivity must be resolved before import planning.");
        }

        var directory = Path.GetDirectoryName(destination) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(destination);
        var extension = Path.GetExtension(destination);
        var existingFamily = fileSystem.DirectoryExists(directory)
            ? fileSystem.EnumerateFiles(directory)
                .Select(path => new
                {
                    Path = path,
                    Suffix = TryGetDestinationSuffix(
                        path,
                        name,
                        extension,
                        destinationSemantics.CaseSensitivity == FileSystemCaseSensitivity.Sensitive
                            ? StringComparison.Ordinal
                            : StringComparison.OrdinalIgnoreCase)
                })
                .Where(candidate => candidate.Suffix.HasValue)
                .OrderBy(candidate => candidate.Suffix)
                .ToArray()
            : [];

        foreach (var existing in existingFamily)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (usedDestinations.Contains(existing.Path))
            {
                continue;
            }
            if (await fileSystem.FilesHaveSameContentAsync(
                    sourcePath,
                    existing.Path,
                    cancellationToken))
            {
                // Repeated processing of a completed multi-file download must
                // reuse the prior suffix even when an earlier numeric slot was removed.
                return new ImportDestinationReservation(
                    existing.Path,
                    ReusesExistingFile: true);
            }
        }

        var occupiedSuffixes = existingFamily
            .Select(candidate => candidate.Suffix!.Value)
            .ToHashSet();
        for (var suffix = 0; ; suffix++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = suffix == 0
                ? destination
                : Path.Join(directory, $"{name} ({suffix}){extension}");
            if (!occupiedSuffixes.Contains(suffix)
                && !usedDestinations.Contains(candidate))
            {
                return new ImportDestinationReservation(candidate);
            }
        }
    }

    private static int? TryGetDestinationSuffix(
        string path,
        string expectedName,
        string expectedExtension,
        StringComparison comparison)
    {
        var extension = Path.GetExtension(path);
        if (!string.Equals(extension, expectedExtension, comparison))
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        if (string.Equals(name, expectedName, comparison))
        {
            return 0;
        }

        var prefix = expectedName + " (";
        if (name.Length <= prefix.Length + 1
            || !name.EndsWith(')')
            || !name.StartsWith(prefix, comparison))
        {
            return null;
        }

        var suffixText = name[prefix.Length..^1];
        return int.TryParse(suffixText, out var suffix) && suffix > 0
            ? suffix
            : null;
    }

    public static void Commit(
        ImportDestinationReservation reservation,
        ISet<string> usedDestinations)
    {
        usedDestinations.Add(reservation.Path);
    }

    public Task<bool> IsExistingEquivalentAsync(
        string sourcePath,
        string destination,
        CancellationToken cancellationToken = default) =>
        fileSystem.FileExists(destination)
            ? fileSystem.FilesHaveSameContentAsync(sourcePath, destination, cancellationToken)
            : Task.FromResult(false);
}

public sealed record ImportDestinationReservation(
    string Path,
    bool ReusesExistingFile = false);
