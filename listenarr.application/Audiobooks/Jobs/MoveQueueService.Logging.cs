using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Jobs;

public partial class MoveQueueService
{
    private void LogStatusChange(Guid id, MoveJobStatus status, string? error)
    {
        if (status == MoveJobStatus.Failed && !string.IsNullOrWhiteSpace(error))
        {
            _logger.LogError("Move job {JobId} FAILED with error: {Error}", id, error);
        }
        else
        {
            _logger.LogInformation("Updated move job {JobId} status to {Status}", id, status);
        }
    }

    private PublicationGateEntry AcquirePublicationGate(Guid id)
    {
        lock (_publicationGateSync)
        {
            if (!_publicationGates.TryGetValue(id, out var entry))
            {
                entry = new PublicationGateEntry();
                _publicationGates.Add(id, entry);
            }

            entry.References++;
            return entry;
        }
    }

    private void ReleasePublicationGate(Guid id, PublicationGateEntry entry)
    {
        lock (_publicationGateSync)
        {
            entry.References--;
            if (entry.References == 0
                && _publicationGates.TryGetValue(id, out var current)
                && ReferenceEquals(current, entry))
            {
                _publicationGates.Remove(id);
                entry.Gate.Dispose();
            }
        }
    }

    internal int PublicationGateCount
    {
        get
        {
            lock (_publicationGateSync)
            {
                return _publicationGates.Count;
            }
        }
    }

    internal int GetPublicationGateReferenceCount(Guid id)
    {
        lock (_publicationGateSync)
        {
            return _publicationGates.TryGetValue(id, out var entry)
                ? entry.References
                : 0;
        }
    }

    private sealed class PublicationGateEntry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int References { get; set; }
    }
}
