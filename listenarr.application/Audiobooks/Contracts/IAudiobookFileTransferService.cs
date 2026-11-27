namespace Listenarr.Application.Audiobooks.Contracts;

/// <summary>
/// Moves tracked audio files between two library records. Ownership is re-homed
/// through the audiobook-file identity contract and the physical relocation runs as
/// an organize of the destination, so a transfer can never bypass ownership
/// uniqueness or race concurrent scan, import, organize or move work.
/// </summary>
public interface IAudiobookFileTransferService
{
    Task<AudiobookFileTransferResult> TransferAsync(
        AudiobookFileTransferRequest request,
        CancellationToken cancellationToken = default);
}
