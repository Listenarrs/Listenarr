using Listenarr.Infrastructure.Persistence.Repositories;

namespace Listenarr.Tests.Common;

internal static class AudiobookFileRepositoryTestExtensions
{
    public static Task<AudiobookFile> AddAsync(
        this IAudiobookFileRepository repository,
        AudiobookFile file,
        CancellationToken cancellationToken = default)
    {
        if (repository is not EfAudiobookFileRepository efRepository)
        {
            throw new InvalidOperationException(
                "Legacy audiobook file test seeding requires EfAudiobookFileRepository.");
        }

        return efRepository.AddUnresolvedForTestingAsync(file, cancellationToken);
    }
}
