namespace Listenarr.Application.Common.Contracts;

public sealed record FileSystemVolumeComparison(
    bool IsAvailable,
    bool SameVolume,
    string? SourceBoundary,
    string? DestinationBoundary,
    string? Reason = null);

public interface IFileSystemVolumeResolver
{
    FileSystemVolumeComparison Compare(string sourcePath, string destinationPath);
}
