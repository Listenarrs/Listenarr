namespace Listenarr.Tests.Common;

public static class WindowsPathTestFixture
{
    public static string CreateRootRelativeAliasCompatibleDirectory(string name)
    {
        var directory = GetRootRelativeAliasCompatiblePath(name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    public static string GetRootRelativeAliasCompatiblePath(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The root-relative Windows path fixture is available only on Windows.");
        }

        var rootRelativeDrive = Path.GetFullPath(
            Path.DirectorySeparatorChar.ToString());
        var fixtureBase = Path.GetFullPath(AppContext.BaseDirectory);
        if (!HasSameDrive(fixtureBase, rootRelativeDrive))
        {
            fixtureBase = Path.GetFullPath(Environment.CurrentDirectory);
        }

        if (!HasSameDrive(fixtureBase, rootRelativeDrive))
        {
            throw new InvalidOperationException(
                "No test location was found on the drive used by Windows root-relative path resolution.");
        }

        var safeName = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "fixture";
        }

        return Path.Join(
            fixtureBase,
            $".listenarr-root-relative-{safeName}-{Guid.NewGuid():N}");
    }

    public static string GetRootRelativeForeignAlias(string nativePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The root-relative Windows path fixture is available only on Windows.");
        }

        var nativeFullPath = Path.GetFullPath(nativePath);
        var driveRoot = Path.GetPathRoot(nativeFullPath)
            ?? throw new InvalidOperationException(
                "The native Windows fixture path has no drive root.");
        var foreignPath = "/" + nativeFullPath[driveRoot.Length..]
            .Replace('\\', '/');
        var resolvedForeignPath = Path.GetFullPath(foreignPath);
        if (!string.Equals(
                nativeFullPath,
                resolvedForeignPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The Windows foreign-path fixture is invalid: native '{nativeFullPath}' resolves separately from '{resolvedForeignPath}'.");
        }

        return foreignPath;
    }

    private static bool HasSameDrive(string left, string right) =>
        string.Equals(
            Path.GetPathRoot(left),
            Path.GetPathRoot(right),
            StringComparison.OrdinalIgnoreCase);
}
