namespace Listenarr.Infrastructure.FileSystem;

internal static partial class FileSystemSafety
{
    private static readonly AsyncLocal<Action<string>?>
        BeforeEmptyDirectoryCandidatePinHook = new();
    private static readonly AsyncLocal<Action<string>?>
        AfterEmptyDirectoryCandidatePinHook = new();

    internal static IDisposable PushBeforeEmptyDirectoryCandidatePinHook(
        Action<string> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        var previous = BeforeEmptyDirectoryCandidatePinHook.Value;
        BeforeEmptyDirectoryCandidatePinHook.Value = hook;
        return new EmptyDirectoryCleanupHookScope(
            () => BeforeEmptyDirectoryCandidatePinHook.Value = previous);
    }

    internal static IDisposable PushAfterEmptyDirectoryCandidatePinHook(
        Action<string> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        var previous = AfterEmptyDirectoryCandidatePinHook.Value;
        AfterEmptyDirectoryCandidatePinHook.Value = hook;
        return new EmptyDirectoryCleanupHookScope(
            () => AfterEmptyDirectoryCandidatePinHook.Value = previous);
    }

    private static void InvokeBeforeEmptyDirectoryCandidatePinHook(string path) =>
        BeforeEmptyDirectoryCandidatePinHook.Value?.Invoke(path);

    private static void InvokeAfterEmptyDirectoryCandidatePinHook(string path) =>
        AfterEmptyDirectoryCandidatePinHook.Value?.Invoke(path);

    private sealed class EmptyDirectoryCleanupHookScope(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() =>
            Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
