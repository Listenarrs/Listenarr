using Microsoft.Extensions.DependencyInjection;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private readonly IFilesystemMutationCoordinator _mutationCoordinator =
        mutationCoordinator ?? throw new ArgumentNullException(nameof(mutationCoordinator));
    private readonly IAudiobookOperationCoordinator _audiobookOperationCoordinator =
        audiobookOperationCoordinator ?? throw new ArgumentNullException(nameof(audiobookOperationCoordinator));
    private readonly IServiceScopeFactory _manifestScopeFactory =
        manifestScopeFactory ?? throw new ArgumentNullException(nameof(manifestScopeFactory));
    private readonly IDirectoryObjectIdentityResolver? _directoryObjectIdentityResolver =
        directoryObjectIdentityResolver;
    private readonly IFileRegistrationRecoveryProbe? _fileRegistrationRecoveryProbe =
        fileRegistrationRecoveryProbe;
    private readonly SemaphoreSlim _rootIdentityGate = new(1, 1);
    private bool _rootIdentitiesReconciled;
}
