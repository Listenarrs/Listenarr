namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class MoveSourceManifestService
{
    public Task<MoveSourceManifest> BuildAsync(
        Audiobook audiobook,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audiobook);
        return BuildCoreAsync(
            audiobook.Id,
            audiobook.BasePath,
            includeContentHashes: true,
            cancellationToken);
    }

    public Task<MoveSourceManifest> BuildPlanAsync(
        AudiobookPathReferenceSnapshot audiobook,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audiobook);
        return BuildCoreAsync(
            audiobook.AudiobookId,
            audiobook.BasePath,
            includeContentHashes: false,
            cancellationToken);
    }
}
