namespace Listenarr.Infrastructure.FileSystem;

internal static class PinnedFilesystemMutationHooks
{
    private static readonly AsyncLocal<Action<string>?>
        BeforeUnixFileDeleteRevalidation = new();
    private static readonly AsyncLocal<Action<string>?>
        BeforeUnixDirectoryDeleteRevalidation = new();
    private static readonly AsyncLocal<Action<string>?>
        AfterUnixDirectoryCreateBeforeOpen = new();

    internal static IDisposable PushBeforeUnixFileDeleteRevalidation(
        Action<string> hook) =>
        Push(BeforeUnixFileDeleteRevalidation, hook);

    internal static IDisposable PushBeforeUnixDirectoryDeleteRevalidation(
        Action<string> hook) =>
        Push(BeforeUnixDirectoryDeleteRevalidation, hook);

    internal static IDisposable PushAfterUnixDirectoryCreateBeforeOpen(
        Action<string> hook) =>
        Push(AfterUnixDirectoryCreateBeforeOpen, hook);

    internal static void InvokeBeforeUnixFileDeleteRevalidation(string path) =>
        BeforeUnixFileDeleteRevalidation.Value?.Invoke(path);

    internal static void InvokeBeforeUnixDirectoryDeleteRevalidation(string path) =>
        BeforeUnixDirectoryDeleteRevalidation.Value?.Invoke(path);

    internal static void InvokeAfterUnixDirectoryCreateBeforeOpen(string path) =>
        AfterUnixDirectoryCreateBeforeOpen.Value?.Invoke(path);

    private static IDisposable Push(
        AsyncLocal<Action<string>?> slot,
        Action<string> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        var previous = slot.Value;
        slot.Value = hook;
        return new HookScope(() => slot.Value = previous);
    }

    private sealed class HookScope(Action restore) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            restore();
            _disposed = true;
        }
    }
}
