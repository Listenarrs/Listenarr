namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    internal Action? BeforeEmptyRelocationAtomicCommitForTest { get; set; }
    internal Action<Guid>? BeforeCompletedRelocationAtomicCommitForTest { get; set; }
}
