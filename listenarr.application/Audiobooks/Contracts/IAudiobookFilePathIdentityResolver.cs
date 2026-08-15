namespace Listenarr.Application.Audiobooks.Contracts;

public interface IAudiobookFilePathIdentityResolver
{
    ValueTask<AudiobookFilePathIdentity> ResolveAsync(
        Audiobook audiobook,
        string path,
        CancellationToken cancellationToken = default);
}
