namespace Listenarr.Infrastructure.Persistence.Repositories;

public sealed partial class EfMoveQueuePersistence
{
    private static bool HasDurableExecutionEvidence(
        MoveJob job,
        IReadOnlySet<Guid> manifestEvidence,
        IReadOnlySet<Guid> scaffoldEvidence) =>
        job.Phase > MoveJobPhase.Planned
        || manifestEvidence.Contains(job.Id)
        || scaffoldEvidence.Contains(job.Id);
}
