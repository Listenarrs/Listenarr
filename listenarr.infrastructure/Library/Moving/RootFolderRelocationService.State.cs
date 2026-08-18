namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private readonly SemaphoreSlim _rootIdentityGate = new(1, 1);
    private bool _rootIdentitiesReconciled;
}
