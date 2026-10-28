namespace Listenarr.Application.Common;

internal sealed class ReentrantOperationScope
{
    private readonly object _sync = new();
    private int _scopes = 1;
    private bool _closing;
    private TaskCompletionSource? _drained;

    public bool TryEnter()
    {
        lock (_sync)
        {
            if (_closing)
            {
                return false;
            }

            _scopes++;
            return true;
        }
    }

    public void Exit()
    {
        TaskCompletionSource? drained = null;
        lock (_sync)
        {
            if (_scopes <= 1)
            {
                throw new InvalidOperationException(
                    "An operation scope was released without a matching reentrant acquisition.");
            }

            _scopes--;
            if (_closing && _scopes == 1)
            {
                drained = _drained;
            }
        }

        drained?.TrySetResult();
    }

    public Task CloseAsync()
    {
        lock (_sync)
        {
            _closing = true;
            if (_scopes == 1)
            {
                return Task.CompletedTask;
            }

            _drained ??= new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return _drained.Task;
        }
    }
}

internal sealed class ReentrantOperationFrame<TState>(TState state)
{
    public TState State { get; } = state;
    public ReentrantOperationScope Lifetime { get; } = new();
    public SemaphoreSlim ChildGate { get; } = new(1, 1);
}
