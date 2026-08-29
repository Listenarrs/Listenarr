#pragma warning disable IDE0130 // Test-only compatibility extension intentionally lives in the globally imported contracts namespace.
namespace Listenarr.Application.Common.Contracts;
#pragma warning restore IDE0130

internal static class FileSystemSemanticsResolverTestExtensions
{
    public static ValueTask<FileSystemSemanticsResolution> ResolveAsync(
        this IFileSystemSemanticsResolver resolver,
        string path,
        CancellationToken cancellationToken = default) =>
        resolver.ResolveAsync(
            path,
            FileSystemCaseSensitivityMode.Auto,
            cancellationToken);
}
