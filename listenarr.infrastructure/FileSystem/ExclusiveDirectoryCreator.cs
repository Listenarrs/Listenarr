namespace Listenarr.Infrastructure.FileSystem;

internal static class ExclusiveDirectoryCreator
{
    private static readonly AsyncLocal<Action<string>?> BeforeOpenParentHook = new();
    private static readonly AsyncLocal<Action<string>?> BeforeCreateHook = new();

    internal static IDisposable PushBeforeOpenParentHook(Action<string> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        var previous = BeforeOpenParentHook.Value;
        BeforeOpenParentHook.Value = hook;
        return new HookScope(() => BeforeOpenParentHook.Value = previous);
    }

    internal static IDisposable PushBeforeCreateHook(Action<string> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        var previous = BeforeCreateHook.Value;
        BeforeCreateHook.Value = hook;
        return new HookScope(() => BeforeCreateHook.Value = previous);
    }

    internal static void InvokeBeforeOpenParentHook(string path) =>
        BeforeOpenParentHook.Value?.Invoke(path);

    internal static void InvokeBeforeCreateHook(string path) =>
        BeforeCreateHook.Value?.Invoke(path);

    public static bool TryCreate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException(
                "The directory creation target has no parent directory.",
                nameof(path));
        var childName = Path.GetFileName(fullPath);
        using var creation = PinnedDirectoryCreation.TryCreate(parent, childName);
        return creation.Created;
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
