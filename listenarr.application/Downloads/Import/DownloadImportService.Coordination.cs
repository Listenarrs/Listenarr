namespace Listenarr.Application.Downloads.Import;

public partial class DownloadImportService
{
    public Task<List<ImportResult>> ImportDownloadFilesAsync(
        Audiobook audiobook,
        List<string> files,
        CancellationToken ct = default,
        DownloadImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(audiobook);
        return filesystemMutationCoordinator.ExecuteExclusiveAsync(
            globalToken => audiobookOperationCoordinator.ExecuteExclusiveAsync(
                audiobook.Id,
                async token =>
                {
                    await moveQueueService.EnsureFilesystemMutationAllowedAsync(
                        audiobook.Id,
                        token);
                    var currentAudiobook = await audiobookRepository.GetByIdSnapshotAsync(
                        audiobook.Id,
                        token)
                        ?? throw new InvalidOperationException(
                            $"Audiobook {audiobook.Id} no longer exists");
                    return await ImportDownloadFilesCoreAsync(
                        currentAudiobook,
                        files,
                        token,
                        options);
                },
                globalToken),
            ct);
    }
}
