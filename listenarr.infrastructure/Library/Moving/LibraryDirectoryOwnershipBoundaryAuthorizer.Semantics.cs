using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class LibraryDirectoryOwnershipBoundaryAuthorizer
{
    private enum ConfiguredRootSemanticsStatus
    {
        Current,
        Changed,
        Unavailable
    }

    private async Task<ConfiguredRootSemanticsStatus> GetConfiguredRootSemanticsStatusAsync(
        RootFolder root,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken)
    {
        try
        {
            var live = await _semanticsResolver.ResolveAsync(
                root.Path,
                root.CaseSensitivityMode,
                cancellationToken);
            if (live.State == PathIdentityState.Unavailable)
            {
                return ConfiguredRootSemanticsStatus.Unavailable;
            }

            return live.State == PathIdentityState.Valid
                && live.Semantics.Syntax == semantics.Syntax
                && live.Semantics.CaseSensitivity == semantics.CaseSensitivity
                    ? ConfiguredRootSemanticsStatus.Current
                    : ConfiguredRootSemanticsStatus.Changed;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception)
        {
            return ConfiguredRootSemanticsStatus.Unavailable;
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException
                or NotSupportedException or PathTooLongException)
        {
            return ConfiguredRootSemanticsStatus.Changed;
        }
    }

    private static void RequireCurrentConfiguredRootSemantics(
        ConfiguredRootSemanticsStatus status,
        string changedMessage)
    {
        if (status == ConfiguredRootSemanticsStatus.Unavailable)
        {
            throw new IOException(
                "The configured root filesystem semantics are temporarily unavailable.");
        }
        if (status != ConfiguredRootSemanticsStatus.Current)
        {
            throw new InvalidOperationException(changedMessage);
        }
    }

    private static FileSystemPathSemantics? TryGetPersistedRootSemantics(
        RootFolder root)
    {
        var persisted = RootFolderPathSemantics.ResolvePersisted(root);
        return persisted.HasValue
            && !persisted.Value.DetectAmbiguousCaseMatches
            ? persisted.Value.Semantics
            : null;
    }

    private static ConfiguredRootSelection SelectContainingConfiguredRoot(
        IReadOnlyCollection<RootFolder> roots,
        string canonicalPath,
        FileSystemPathSemantics requestedSemantics,
        string? requiredParentPath = null)
    {
        RootFolder? bestRoot = null;
        FileSystemPathSemantics? bestSemantics = null;
        var bestRootLength = -1;
        var unavailableRootLength = -1;

        foreach (var root in roots)
        {
            var persistedSemantics = TryGetPersistedRootSemantics(root);
            if (!persistedSemantics.HasValue)
            {
                if (FileSystemPathIdentity.StoredBoundaryMayContainPath(
                        root.Path,
                        canonicalPath,
                        requestedSemantics.Syntax,
                        root.CaseSensitivityMode)
                    && (requiredParentPath == null
                        || FileSystemPathIdentity.StoredBoundaryMayContainPath(
                            root.Path,
                            requiredParentPath,
                            requestedSemantics.Syntax,
                            root.CaseSensitivityMode)))
                {
                    unavailableRootLength = Math.Max(
                        unavailableRootLength,
                        root.Path.Length);
                }

                continue;
            }

            var semantics = persistedSemantics.Value;
            if (semantics.Syntax != requestedSemantics.Syntax)
            {
                continue;
            }

            bool containsPath;
            try
            {
                containsPath = FileSystemPathIdentity.IsSameOrInside(
                    canonicalPath,
                    root.Path,
                    semantics)
                    && (requiredParentPath == null
                        || FileSystemPathIdentity.IsSameOrInside(
                            requiredParentPath,
                            root.Path,
                            semantics));
            }
            catch (Exception exception) when (exception is
                ArgumentException or InvalidOperationException
                    or NotSupportedException or PathTooLongException
                    or System.Security.SecurityException)
            {
                if (FileSystemPathIdentity.StoredBoundaryMayContainPath(
                        root.Path,
                        canonicalPath,
                        requestedSemantics.Syntax,
                        root.CaseSensitivityMode))
                {
                    unavailableRootLength = Math.Max(
                        unavailableRootLength,
                        root.Path.Length);
                }
                continue;
            }

            if (!containsPath)
            {
                continue;
            }

            if (root.Path.Length > bestRootLength)
            {
                bestRoot = root;
                bestSemantics = semantics;
                bestRootLength = root.Path.Length;
            }
        }

        return unavailableRootLength >= bestRootLength
                && unavailableRootLength >= 0
            ? ConfiguredRootSelection.Blocked
            : new ConfiguredRootSelection(
                bestRoot,
                bestSemantics,
                IsBlocked: false);
    }

    private static bool HasCompatibleSyntax(
        string path,
        FileSystemPathSyntax expectedSyntax) =>
        FileSystemPathIdentity.TryDetectAbsoluteSyntax(
            path,
            out var syntax)
        && syntax == expectedSyntax;

    private sealed record ConfiguredRootSelection(
        RootFolder? Root,
        FileSystemPathSemantics? Semantics,
        bool IsBlocked)
    {
        public static ConfiguredRootSelection Blocked { get; } =
            new(null, null, IsBlocked: true);
    }
}
