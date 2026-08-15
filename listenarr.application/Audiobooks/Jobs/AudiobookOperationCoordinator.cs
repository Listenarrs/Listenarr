using Listenarr.Application.Common;

namespace Listenarr.Application.Audiobooks.Jobs;

public sealed class AudiobookOperationCoordinator : IAudiobookOperationCoordinator, IDisposable
{
    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int References;
    }

    private readonly object _sync = new();
    private readonly Dictionary<int, Entry> _entries = [];
    private readonly AsyncLocal<
        ReentrantOperationFrame<IReadOnlyDictionary<int, Entry>>?> _held = new();
    private bool _disposed;

    public Task ExecuteExclusiveAsync(
        int audiobookId,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default) =>
        ExecuteExclusiveAsync([audiobookId], operation, cancellationToken);

    public Task<T> ExecuteExclusiveAsync<T>(
        int audiobookId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default) =>
        ExecuteExclusiveAsync([audiobookId], operation, cancellationToken);

    public Task ExecuteExclusiveAsync(
        IEnumerable<int> audiobookIds,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default) =>
        ExecuteExclusiveAsync<object?>(
            audiobookIds,
            async token =>
            {
                await operation(token);
                return null;
            },
            cancellationToken);

    public async Task<T> ExecuteExclusiveAsync<T>(
        IEnumerable<int> audiobookIds,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audiobookIds);
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var orderedIds = audiobookIds
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        var parent = _held.Value;
        if (orderedIds.Length == 0 && parent == null)
        {
            return await operation(cancellationToken);
        }

        if (parent is { State.Count: > 0 })
        {
            var highestHeldId = parent.State.Keys.Max();
            var lowerUnheldId = orderedIds
                .Where(id => !parent.State.ContainsKey(id) && id < highestHeldId)
                .Select(id => (int?)id)
                .FirstOrDefault();
            if (lowerUnheldId.HasValue)
            {
                throw new InvalidOperationException(
                    $"Cannot acquire audiobook operation key {lowerUnheldId.Value} after higher key {highestHeldId} is already held.");
            }
        }

        var enteredParent = false;
        var acquiredParentGate = false;
        var acquired = new List<(int Id, Entry Entry)>();
        ReentrantOperationFrame<IReadOnlyDictionary<int, Entry>>? frame = null;
        try
        {
            if (parent != null)
            {
                await parent.ChildGate.WaitAsync(cancellationToken);
                acquiredParentGate = true;
                if (!parent.Lifetime.TryEnter())
                {
                    throw new InvalidOperationException(
                        "An audiobook operation escaped its owning scope.");
                }

                enteredParent = true;
            }

            var held = parent == null
                ? new Dictionary<int, Entry>()
                : new Dictionary<int, Entry>(parent.State);
            foreach (var audiobookId in orderedIds)
            {
                if (held.ContainsKey(audiobookId))
                {
                    continue;
                }

                var entry = AddReference(audiobookId);
                try
                {
                    await entry.Gate.WaitAsync(cancellationToken);
                }
                catch
                {
                    ReleaseReference(audiobookId, entry, releaseGate: false);
                    throw;
                }

                acquired.Add((audiobookId, entry));
                held.Add(audiobookId, entry);
            }

            frame = new ReentrantOperationFrame<IReadOnlyDictionary<int, Entry>>(
                held);
            _held.Value = frame;
            return await operation(cancellationToken);
        }
        finally
        {
            if (frame != null)
            {
                await frame.Lifetime.CloseAsync();
                _held.Value = parent;
            }

            for (var index = acquired.Count - 1; index >= 0; index--)
            {
                var (audiobookId, entry) = acquired[index];
                ReleaseReference(audiobookId, entry, releaseGate: true);
            }

            if (enteredParent)
            {
                parent!.Lifetime.Exit();
            }

            if (acquiredParentGate)
            {
                parent!.ChildGate.Release();
            }
        }
    }

    private Entry AddReference(int audiobookId)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(audiobookId, out var entry))
            {
                entry = new Entry();
                _entries.Add(audiobookId, entry);
            }

            entry.References++;
            return entry;
        }
    }

    private void ReleaseReference(
        int audiobookId,
        Entry entry,
        bool releaseGate)
    {
        if (releaseGate)
        {
            entry.Gate.Release();
        }

        lock (_sync)
        {
            entry.References--;
            if (entry.References == 0
                && _entries.TryGetValue(audiobookId, out var current)
                && ReferenceEquals(current, entry))
            {
                _entries.Remove(audiobookId);
                entry.Gate.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var pair in _entries
                .Where(pair => pair.Value.References == 0)
                .ToList())
            {
                pair.Value.Gate.Dispose();
                _entries.Remove(pair.Key);
            }
        }
    }
}
