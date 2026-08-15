namespace Listenarr.Application.Downloads.Contracts;

public readonly record struct FilePublicationSourceCapabilityResult(
    bool IsSupported,
    string? Reason = null)
{
    public static FilePublicationSourceCapabilityResult Supported => new(true);

    public static FilePublicationSourceCapabilityResult Unsupported(string reason) =>
        new(false, reason);
}

public interface IFilePublicationSourceCapability
{
    Task<FilePublicationSourceCapabilityResult> CheckAsync(
        string sourcePath,
        CancellationToken cancellationToken = default);
}
