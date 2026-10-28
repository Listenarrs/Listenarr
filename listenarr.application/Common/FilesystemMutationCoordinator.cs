namespace Listenarr.Application.Common;

/// <summary>
/// Serializes filesystem check-and-mutate workflows within one Listenarr process.
/// Deployments must run a single Listenarr process per database; this in-memory gate
/// does not coordinate independent processes.
/// </summary>
public sealed class FilesystemMutationCoordinator : IFilesystemMutationCoordinator, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AsyncLocal<ReentrantOperationFrame<object?>?> _held = new();

    public Task ExecuteExclusiveAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default) =>
        ExecuteExclusiveAsync<object?>(
            async token =>
            {
                await operation(token);
                return null;
            },
            cancellationToken);

    public async Task<T> ExecuteExclusiveAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        var parent = _held.Value;
        var enteredParent = false;
        var acquiredParentGate = false;
        var acquiredRootGate = false;
        ReentrantOperationFrame<object?>? frame = null;
        try
        {
            if (parent != null)
            {
                await parent.ChildGate.WaitAsync(cancellationToken);
                acquiredParentGate = true;
                if (!parent.Lifetime.TryEnter())
                {
                    throw new InvalidOperationException(
                        "A filesystem mutation operation escaped its owning scope.");
                }

                enteredParent = true;
            }
            else
            {
                await _gate.WaitAsync(cancellationToken);
                acquiredRootGate = true;
            }

            frame = new ReentrantOperationFrame<object?>(null);
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

            if (enteredParent)
            {
                parent!.Lifetime.Exit();
            }

            if (acquiredParentGate)
            {
                parent!.ChildGate.Release();
            }

            if (acquiredRootGate)
            {
                _gate.Release();
            }
        }
    }

    public void Dispose() => _gate.Dispose();
}
