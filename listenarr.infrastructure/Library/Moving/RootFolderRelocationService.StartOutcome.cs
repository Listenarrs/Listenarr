namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private sealed record StartOutcome(
        RootFolderPathChangeResult Result,
        bool Broadcast);
}
