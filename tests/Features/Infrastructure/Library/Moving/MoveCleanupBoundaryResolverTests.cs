using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

[Trait("Name", "MoveCleanupBoundaryResolverTests")]
[Trait("Category", "Library")]
public sealed class MoveCleanupBoundaryResolverTests : BaseTests
{
    [Fact]
    public async Task ResolveAsync_ConfiguredSourceRoot_TakesPrecedenceOverCommonAncestor()
    {
        var root = FileService.GetTempDirectory("move-boundary-configured-root");
        var series = Path.Join(root, "Author", "Series");
        var source = Path.Join(series, "Old Title", "test");
        var target = Path.Join(series, "New Title", "test");
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(
            source,
            target,
            [new RootFolder { Name = "Library", Path = root }]);

        Assert.True(result.IsAvailable);
        Assert.Equal(MoveCleanupBoundaryKind.ConfiguredRoot, result.Kind);
        Assert.Equal(Path.GetFullPath(root), result.Boundary);
    }

    [Fact]
    public async Task ResolveAsync_ExplicitConfiguredRootMode_DoesNotRequireAutoSourceProbe()
    {
        var root = FileService.GetTempDirectory("move-boundary-explicit-root-mode");
        var source = Path.Join(root, "Author", "Old Title", "test");
        var target = Path.Join(root, "Author", "New Title", "test");
        var explicitMode = OperatingSystem.IsWindows()
            ? FileSystemCaseSensitivityMode.Insensitive
            : FileSystemCaseSensitivityMode.Sensitive;
        var explicitSensitivity = explicitMode == FileSystemCaseSensitivityMode.Insensitive
            ? FileSystemCaseSensitivity.Insensitive
            : FileSystemCaseSensitivity.Sensitive;
        var resolver = CreateResolver((path, mode) =>
            mode == FileSystemCaseSensitivityMode.Auto
                ? new FileSystemSemanticsResolution(
                    new FileSystemPathSemantics(
                        FileSystemPathSemantics.CurrentHostDefault.Syntax,
                        FileSystemCaseSensitivity.Unknown),
                    PathIdentityState.Unavailable,
                    path,
                    "Injected Auto probe failure.")
                : new FileSystemSemanticsResolution(
                    new FileSystemPathSemantics(
                        FileSystemPathSemantics.CurrentHostDefault.Syntax,
                        explicitSensitivity),
                    PathIdentityState.Valid,
                    root));

        var result = await resolver.ResolveAsync(
            source,
            target,
            [new RootFolder
            {
                Name = "Explicit Root",
                Path = root,
                CaseSensitivityMode = explicitMode
            }]);

        Assert.True(result.IsAvailable, result.Reason);
        Assert.Equal(MoveCleanupBoundaryKind.ConfiguredRoot, result.Kind);
        Assert.Equal(Path.GetFullPath(root), result.Boundary);
    }

    [Fact]
    public async Task ResolveAsync_CustomSiblingMove_UsesCommonSeriesAncestor()
    {
        var customRoot = FileService.GetTempDirectory("move-boundary-custom-root");
        var series = Path.Join(customRoot, "Matt Dinniman", "Dungeon Crawler Carl");
        var source = Path.Join(series, "A Parade of Horribles (20262)", "test");
        var target = Path.Join(series, "A Parade of Horribles (2026)", "test");
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(source, target, []);

        Assert.True(result.IsAvailable);
        Assert.Equal(MoveCleanupBoundaryKind.CommonAncestor, result.Kind);
        Assert.Equal(Path.GetFullPath(series), result.Boundary);
    }

    [Fact]
    public async Task ResolveAsync_PersistedBoundaryOutsideSource_IsUnavailable()
    {
        var sourceRoot = FileService.GetTempDirectory("move-boundary-source");
        var targetRoot = FileService.GetTempDirectory("move-boundary-target");
        var source = Path.Join(sourceRoot, "Author", "Title", "test");
        var target = Path.Join(targetRoot, "Author", "Title", "test");
        var unrelatedBoundary = FileService.GetTempDirectory("move-boundary-unrelated");
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(
            source,
            target,
            [],
            unrelatedBoundary);

        Assert.False(result.IsAvailable);
        Assert.Equal(MoveCleanupBoundaryKind.Unavailable, result.Kind);
        Assert.Contains("no longer contains", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [WindowsFact]
    public async Task ResolveAsync_AmbiguousPersistedBoundaryAlias_IsUnavailable()
    {
        var configuredRoot = FileService.GetTempDirectory("move-boundary-foreign-persisted");
        var source = Path.Join(configuredRoot, "Author", "Title", "test");
        var target = Path.Join(
            FileService.GetTempDirectory("move-boundary-foreign-persisted-target"),
            "Author",
            "Title",
            "test");
        var persistedBoundary = Path.Join(configuredRoot, "Author");
        var ambiguousBoundary = "//?/" + persistedBoundary.Replace('\\', '/');
        Assert.True(Directory.Exists("//?/" + configuredRoot.Replace('\\', '/')));
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(
            source,
            target,
            [],
            ambiguousBoundary);

        Assert.False(result.IsAvailable);
        Assert.Equal(MoveCleanupBoundaryKind.Unavailable, result.Kind);
        Assert.Contains("unambiguous", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_BroadPersistedBoundary_IsNarrowedToConfiguredRoot()
    {
        var configuredRoot = FileService.GetTempDirectory("move-boundary-persisted-broad");
        var source = Path.Join(configuredRoot, "Author", "Title", "test");
        var target = Path.Join(
            FileService.GetTempDirectory("move-boundary-persisted-broad-target"),
            "Author",
            "Title",
            "test");
        var broadBoundary = Path.GetDirectoryName(configuredRoot)!;
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(
            source,
            target,
            [new RootFolder { Name = "Library", Path = configuredRoot }],
            broadBoundary);

        Assert.True(result.IsAvailable, result.Reason);
        Assert.Equal(MoveCleanupBoundaryKind.ConfiguredRoot, result.Kind);
        Assert.Equal(configuredRoot, result.Boundary);
    }

    [Fact]
    public async Task ResolveAsync_PersistedConfiguredRoot_IsRevalidatedAsConfiguredRoot()
    {
        var configuredRoot = FileService.GetTempDirectory("move-boundary-persisted-configured-root");
        var source = Path.Join(configuredRoot, "Author", "Title", "test");
        var target = Path.Join(
            FileService.GetTempDirectory("move-boundary-persisted-configured-target"),
            "Author",
            "Title",
            "test");
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(
            source,
            target,
            [new RootFolder { Name = "Library", Path = configuredRoot }],
            configuredRoot);

        Assert.True(result.IsAvailable, result.Reason);
        Assert.Equal(MoveCleanupBoundaryKind.ConfiguredRoot, result.Kind);
        Assert.Equal(configuredRoot, result.Boundary);
    }

    [Fact]
    public async Task ResolveAsync_NarrowPersistedBoundary_IsPreservedWithinConfiguredRoot()
    {
        var configuredRoot = FileService.GetTempDirectory("move-boundary-persisted-narrow");
        var persistedBoundary = Path.Join(configuredRoot, "Author");
        var source = Path.Join(persistedBoundary, "Title", "test");
        var target = Path.Join(
            FileService.GetTempDirectory("move-boundary-persisted-narrow-target"),
            "Author",
            "Title",
            "test");
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(
            source,
            target,
            [new RootFolder { Name = "Library", Path = configuredRoot }],
            persistedBoundary);

        Assert.True(result.IsAvailable, result.Reason);
        Assert.Equal(MoveCleanupBoundaryKind.Persisted, result.Kind);
        Assert.Equal(persistedBoundary, result.Boundary);
    }

    [WindowsFact]
    public async Task ResolveAsync_CrossRootWindowsMove_UsesSourceVolumeAnchor()
    {

        var driveRoot = Path.GetPathRoot(FileService.GetTempPath())!;
        var sourceAnchor = Path.Join(driveRoot, "Listenarr Downloads");
        var source = Path.Join(
            sourceAnchor,
            "Matt Dinniman",
            "Dungeon Crawler Carl",
            "A Parade of Horribles (20262)",
            "test");
        var target = Path.Join(
            driveRoot,
            "Listenarr Test",
            "Matt Dinniman",
            "Dungeon Crawler Carl",
            "A Parade of Horribles (2026)",
            "test");
        var resolver = CreateResolver(new FileSystemPathSemantics(
            FileSystemPathSyntax.Windows,
            FileSystemCaseSensitivity.Insensitive));

        var result = await resolver.ResolveAsync(source, target, []);

        Assert.True(result.IsAvailable);
        Assert.Equal(MoveCleanupBoundaryKind.VolumeAnchor, result.Kind);
        Assert.Equal(sourceAnchor, result.Boundary);
    }

    [LinuxFact]
    public async Task ResolveAsync_UnixConfiguredSourceRoot_PreservesRootAcrossCrossRootMove()
    {

        var sourceRoot = Path.Join(
            Path.GetTempPath(),
            $"listenarr-unix-source-{Guid.NewGuid():N}");
        var targetRoot = Path.Join(
            Path.GetTempPath(),
            $"listenarr-unix-target-{Guid.NewGuid():N}");
        var source = Path.Join(sourceRoot, "Author", "Series", "Old Title", "test");
        var target = Path.Join(targetRoot, "Author", "Series", "New Title", "test");
        var resolver = CreateResolver(new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive));

        var result = await resolver.ResolveAsync(
            source,
            target,
            [new RootFolder { Name = "Unix Source", Path = sourceRoot }]);

        Assert.True(result.IsAvailable);
        Assert.Equal(MoveCleanupBoundaryKind.ConfiguredRoot, result.Kind);
        Assert.Equal(Path.GetFullPath(sourceRoot), result.Boundary);
    }

    [LinuxFact]
    public async Task ResolveAsync_UnixCustomSiblingMove_UsesCommonSeriesAncestor()
    {

        var series = Path.Join(
            Path.GetTempPath(),
            $"listenarr-unix-sibling-{Guid.NewGuid():N}",
            "Matt Dinniman",
            "Dungeon Crawler Carl");
        var source = Path.Join(series, "A Parade of Horribles (20262)", "test");
        var target = Path.Join(series, "A Parade of Horribles (2026)", "test");
        var resolver = CreateResolver(new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive));

        var result = await resolver.ResolveAsync(source, target, []);

        Assert.True(result.IsAvailable);
        Assert.Equal(MoveCleanupBoundaryKind.CommonAncestor, result.Kind);
        Assert.Equal(Path.GetFullPath(series), result.Boundary);
    }

    [LinuxFact]
    public async Task ResolveAsync_CommonAncestorUsesTargetFilesystemSemantics()
    {

        var parent = FileService.GetTempDirectory("move-boundary-endpoint-semantics");
        var sourceRoot = Path.Join(parent, "Library");
        var targetRoot = Path.Join(parent, "library");
        var source = Path.Join(sourceRoot, "Old Title", "test");
        var target = Path.Join(targetRoot, "New Title", "test");
        var resolver = CreateResolver((path, _) => new FileSystemSemanticsResolution(
            new FileSystemPathSemantics(
                FileSystemPathSyntax.Unix,
                string.Equals(path, target, StringComparison.Ordinal)
                    ? FileSystemCaseSensitivity.Insensitive
                    : FileSystemCaseSensitivity.Sensitive),
            PathIdentityState.Valid,
            parent));

        var result = await resolver.ResolveAsync(source, target, []);

        Assert.True(result.IsAvailable, result.Reason);
        Assert.Equal(MoveCleanupBoundaryKind.CommonAncestor, result.Kind);
        Assert.Equal(sourceRoot, result.Boundary);
    }

    [LinuxFact]
    public async Task ResolveAsync_UnavailableTargetIdentityDoesNotUseSourceSemanticsForCommonAncestor()
    {

        var parent = FileService.GetTempDirectory("move-boundary-target-unavailable");
        var source = Path.Join(parent, "Old Title", "test");
        var target = Path.Join(parent, "New Title", "test");
        var resolver = CreateResolver((path, _) =>
            string.Equals(path, target, StringComparison.Ordinal)
                ? new FileSystemSemanticsResolution(
                    new FileSystemPathSemantics(
                        FileSystemPathSyntax.Unix,
                        FileSystemCaseSensitivity.Unknown),
                    PathIdentityState.Unavailable,
                    parent,
                    "target probe failed")
                : new FileSystemSemanticsResolution(
                    new FileSystemPathSemantics(
                        FileSystemPathSyntax.Unix,
                        FileSystemCaseSensitivity.Sensitive),
                    PathIdentityState.Valid,
                    parent));

        var result = await resolver.ResolveAsync(source, target, []);

        Assert.False(result.IsAvailable);
        Assert.Equal(MoveCleanupBoundaryKind.Unavailable, result.Kind);
        Assert.Contains("target probe failed", result.Reason, StringComparison.Ordinal);
    }

    [LinuxFact]
    public async Task ResolveAsync_UnixCrossRootWithoutConfiguredRoot_DoesNotUseFilesystemRoot()
    {

        var source = "/listenarr-downloads/Author/Series/Old Title/test";
        var target = "/listenarr-library/Author/Series/New Title/test";
        var resolver = CreateResolver(new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive));

        var result = await resolver.ResolveAsync(source, target, []);

        Assert.False(result.IsAvailable);
        Assert.Equal(MoveCleanupBoundaryKind.Unavailable, result.Kind);
        Assert.Contains("No configured source root", result.Reason, StringComparison.Ordinal);
    }

    [LinuxFact]
    public async Task ResolveAsync_InsensitiveConfiguredRootWithPersistedLogicalCase_UsesPhysicalAlias()
    {

        var parent = FileService.GetTempDirectory("move-boundary-case-alias");
        var physicalRoot = Path.Join(parent, "library");
        var configuredRoot = Path.Join(parent, "Library");
        var source = Path.Join(physicalRoot, "Author", "Book", "test");
        var target = Path.Join(parent, "other", "Author", "Book", "test");
        Directory.CreateDirectory(source);
        var resolver = CreateResolver((path, mode) =>
            string.Equals(path, configuredRoot, StringComparison.Ordinal)
                ? new FileSystemSemanticsResolution(
                    new FileSystemPathSemantics(
                        FileSystemPathSyntax.Unix,
                        FileSystemCaseSensitivity.Insensitive),
                    PathIdentityState.Valid,
                    parent)
                : new FileSystemSemanticsResolution(
                    new FileSystemPathSemantics(
                        FileSystemPathSyntax.Unix,
                        FileSystemCaseSensitivity.Sensitive),
                    PathIdentityState.Valid,
                    Path.GetPathRoot(path) ?? path));

        var result = await resolver.ResolveAsync(
            source,
            target,
            [new RootFolder
            {
                Name = "Library",
                Path = configuredRoot,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive
            }],
            configuredRoot);

        Assert.True(result.IsAvailable, result.Reason);
        Assert.Equal(MoveCleanupBoundaryKind.ConfiguredRoot, result.Kind);
        Assert.Equal(physicalRoot, result.Boundary);
        Assert.NotEqual(configuredRoot, result.Boundary);
    }

    [Fact]
    public async Task ResolveAsync_AutoConfiguredRootThatResolvesSensitive_DoesNotClaimCaseDistinctSource()
    {
        var configuredRoot = FileService.GetTempDirectory("move-boundary-auto-sensitive-root");
        var caseDistinctRoot = configuredRoot.ToUpperInvariant();
        Assert.NotEqual(configuredRoot, caseDistinctRoot);
        var source = Path.Join(caseDistinctRoot, "Author", "Book", "test");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"move-boundary-auto-sensitive-target-{Guid.NewGuid():N}");
        var resolver = CreateResolver((path, mode) =>
            string.Equals(path, configuredRoot, StringComparison.Ordinal)
                ? new FileSystemSemanticsResolution(
                    new FileSystemPathSemantics(
                        FileSystemPathSemantics.CurrentHostDefault.Syntax,
                        FileSystemCaseSensitivity.Sensitive),
                    PathIdentityState.Valid,
                    configuredRoot)
                : new FileSystemSemanticsResolution(
                    FileSystemPathSemantics.CurrentHostDefault,
                    PathIdentityState.Valid,
                    Path.GetPathRoot(path) ?? path));

        var result = await resolver.ResolveAsync(
            source,
            target,
            [new RootFolder
            {
                Name = "Auto Root",
                Path = configuredRoot,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto
            }]);

        Assert.NotEqual(MoveCleanupBoundaryKind.ConfiguredRoot, result.Kind);
        Assert.NotEqual(configuredRoot, result.Boundary);
    }

    [LinuxFact]
    public async Task ResolveAsync_AmbiguousConfiguredRootThatMayContainSource_FailsClosed()
    {
        var root = FileService.GetTempDirectory("move-boundary-ambiguous-root");
        var ambiguousRoot = "/" + root;
        Assert.False(FileSystemPathIdentity.TryDetectAbsoluteSyntax(
            ambiguousRoot,
            out _));
        var source = Path.Join(root, "Author", "Book", "test");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"move-boundary-ambiguous-target-{Guid.NewGuid():N}");
        var resolver = CreateResolver((path, mode) => new FileSystemSemanticsResolution(
            new FileSystemPathSemantics(
                FileSystemPathSyntax.Unix,
                FileSystemCaseSensitivity.Sensitive),
            PathIdentityState.Valid,
            Path.GetPathRoot(path) ?? path));

        var result = await resolver.ResolveAsync(
            source,
            target,
            [new RootFolder
            {
                Name = "Legacy Ambiguous Root",
                Path = ambiguousRoot,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive
            }]);

        Assert.False(result.IsAvailable);
        Assert.Equal(MoveCleanupBoundaryKind.Unavailable, result.Kind);
        Assert.Contains("ambiguous", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [WindowsFact]
    public async Task ResolveAsync_DeviceAliasConfiguredRootThatContainsSource_FailsClosed()
    {
        var root = FileService.GetTempDirectory("move-boundary-device-root");
        var source = Path.Join(root, "Author", "Book", "test");
        var target = Path.Join(
            FileService.GetTempPath(),
            $"move-boundary-device-target-{Guid.NewGuid():N}");
        var deviceAliasRoot = @"\\?\" + root;
        var resolver = CreateResolver();

        var result = await resolver.ResolveAsync(
            source,
            target,
            [new RootFolder
            {
                Name = "Legacy Device Root",
                Path = deviceAliasRoot,
                CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive
            }]);

        Assert.False(result.IsAvailable);
        Assert.Equal(MoveCleanupBoundaryKind.Unavailable, result.Kind);
        Assert.Contains("configured source root", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_PotentialConfiguredRootWithUnavailableIdentity_FailsClosed()
    {
        var root = FileService.GetTempDirectory("move-boundary-unavailable-root");
        var source = Path.Join(root, "Author", "Book", "test");
        var target = Path.Join(FileService.GetTempPath(), $"move-boundary-unavailable-target-{Guid.NewGuid():N}");
        var resolver = CreateResolver((path, mode) =>
            string.Equals(path, root, StringComparison.Ordinal)
                ? new FileSystemSemanticsResolution(
                    new FileSystemPathSemantics(
                        FileSystemPathSemantics.CurrentHostDefault.Syntax,
                        FileSystemCaseSensitivity.Unknown),
                    PathIdentityState.Unavailable,
                    root,
                    "configured root probe failed")
                : new FileSystemSemanticsResolution(
                    FileSystemPathSemantics.CurrentHostDefault,
                    PathIdentityState.Valid,
                    Path.GetPathRoot(path) ?? path));

        var result = await resolver.ResolveAsync(
            source,
            target,
            [new RootFolder { Name = "Library", Path = root }]);

        Assert.False(result.IsAvailable);
        Assert.Equal(MoveCleanupBoundaryKind.Unavailable, result.Kind);
        Assert.Contains("configured root probe failed", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_NestedConfiguredRoots_UsesMostSpecificRootSemantics()
    {
        var parentRoot = FileService.GetTempDirectory("move-boundary-nested-parent");
        var nestedRoot = Path.Join(parentRoot, "Nested");
        var source = Path.Join(nestedRoot, "Author", "Book", "test");
        var target = Path.Join(FileService.GetTempPath(), $"move-boundary-nested-target-{Guid.NewGuid():N}");
        var resolver = CreateResolver((path, mode) => new FileSystemSemanticsResolution(
            new FileSystemPathSemantics(
                FileSystemPathSemantics.CurrentHostDefault.Syntax,
                mode == FileSystemCaseSensitivityMode.Insensitive
                    ? FileSystemCaseSensitivity.Insensitive
                    : FileSystemCaseSensitivity.Sensitive),
            PathIdentityState.Valid,
            Path.GetPathRoot(path) ?? path));

        var result = await resolver.ResolveAsync(
            source,
            target,
            [
                new RootFolder
                {
                    Name = "Parent",
                    Path = parentRoot,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive
                },
                new RootFolder
                {
                    Name = "Nested",
                    Path = nestedRoot,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive
                }
            ]);

        Assert.True(result.IsAvailable, result.Reason);
        Assert.Equal(MoveCleanupBoundaryKind.ConfiguredRoot, result.Kind);
        Assert.Equal(nestedRoot, result.Boundary);
    }

    [Fact]
    public async Task ResolveAsync_UnavailableParent_DoesNotBlockValidMoreSpecificRoot()
    {
        var parentRoot = FileService.GetTempDirectory("move-boundary-unavailable-parent");
        var nestedRoot = Path.Join(parentRoot, "Nested");
        var source = Path.Join(nestedRoot, "Author", "Book", "test");
        var target = Path.Join(FileService.GetTempPath(), $"move-boundary-unavailable-parent-target-{Guid.NewGuid():N}");
        var resolver = CreateResolver((path, _) =>
            string.Equals(path, parentRoot, StringComparison.Ordinal)
                ? new FileSystemSemanticsResolution(
                    new FileSystemPathSemantics(
                        FileSystemPathSemantics.CurrentHostDefault.Syntax,
                        FileSystemCaseSensitivity.Unknown),
                    PathIdentityState.Unavailable,
                    parentRoot,
                    "parent probe failed")
                : new FileSystemSemanticsResolution(
                    FileSystemPathSemantics.CurrentHostDefault,
                    PathIdentityState.Valid,
                    Path.GetPathRoot(path) ?? path));

        var result = await resolver.ResolveAsync(
            source,
            target,
            [
                new RootFolder { Name = "Parent", Path = parentRoot },
                new RootFolder { Name = "Nested", Path = nestedRoot }
            ]);

        Assert.True(result.IsAvailable, result.Reason);
        Assert.Equal(MoveCleanupBoundaryKind.ConfiguredRoot, result.Kind);
        Assert.Equal(nestedRoot, result.Boundary);
    }

    [Fact]
    public async Task ResolveAsync_UnavailableMoreSpecificRoot_BlocksBroaderFallback()
    {
        var parentRoot = FileService.GetTempDirectory("move-boundary-unavailable-nested");
        var nestedRoot = Path.Join(parentRoot, "Nested");
        var source = Path.Join(nestedRoot, "Author", "Book", "test");
        var target = Path.Join(FileService.GetTempPath(), $"move-boundary-unavailable-nested-target-{Guid.NewGuid():N}");
        var resolver = CreateResolver((path, _) =>
            string.Equals(path, nestedRoot, StringComparison.Ordinal)
                ? new FileSystemSemanticsResolution(
                    new FileSystemPathSemantics(
                        FileSystemPathSemantics.CurrentHostDefault.Syntax,
                        FileSystemCaseSensitivity.Unknown),
                    PathIdentityState.Unavailable,
                    nestedRoot,
                    "nested probe failed")
                : new FileSystemSemanticsResolution(
                    FileSystemPathSemantics.CurrentHostDefault,
                    PathIdentityState.Valid,
                    Path.GetPathRoot(path) ?? path));

        var result = await resolver.ResolveAsync(
            source,
            target,
            [
                new RootFolder { Name = "Parent", Path = parentRoot },
                new RootFolder { Name = "Nested", Path = nestedRoot }
            ]);

        Assert.False(result.IsAvailable);
        Assert.Equal(MoveCleanupBoundaryKind.Unavailable, result.Kind);
        Assert.Contains("nested probe failed", result.Reason, StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task ResolveAsync_UncConfiguredSourceRoot_TakesPrecedence()
    {

        const string sourceRoot = @"\\server\downloads\Listenarr Downloads";
        const string source = @"\\server\downloads\Listenarr Downloads\Author\Series\Old Title\test";
        const string target = @"\\server\library\Audiobooks\Author\Series\New Title\test";
        var resolver = CreateResolver(new FileSystemPathSemantics(
            FileSystemPathSyntax.Windows,
            FileSystemCaseSensitivity.Insensitive));

        var result = await resolver.ResolveAsync(
            source,
            target,
            [new RootFolder { Name = "UNC Source", Path = sourceRoot }]);

        Assert.True(result.IsAvailable);
        Assert.Equal(MoveCleanupBoundaryKind.ConfiguredRoot, result.Kind);
        Assert.Equal(sourceRoot, result.Boundary);
    }

    [WindowsFact]
    public async Task ResolveAsync_UncSameShareSiblingMove_UsesCommonSeriesAncestor()
    {

        const string series = @"\\server\share\Listenarr Downloads\Matt Dinniman\Dungeon Crawler Carl";
        const string source = @"\\server\share\Listenarr Downloads\Matt Dinniman\Dungeon Crawler Carl\A Parade of Horribles (20262)\test";
        const string target = @"\\server\share\Listenarr Downloads\Matt Dinniman\Dungeon Crawler Carl\A Parade of Horribles (2026)\test";
        var resolver = CreateResolver(new FileSystemPathSemantics(
            FileSystemPathSyntax.Windows,
            FileSystemCaseSensitivity.Insensitive));

        var result = await resolver.ResolveAsync(source, target, []);

        Assert.True(result.IsAvailable);
        Assert.Equal(MoveCleanupBoundaryKind.CommonAncestor, result.Kind);
        Assert.Equal(series, result.Boundary);
    }

    [WindowsFact]
    public async Task ResolveAsync_UncCrossShareMove_UsesTopLevelSourceAnchor()
    {

        const string sourceAnchor = @"\\server\downloads\Listenarr Downloads";
        const string source = @"\\server\downloads\Listenarr Downloads\Author\Series\Old Title\test";
        const string target = @"\\server\library\Audiobooks\Author\Series\New Title\test";
        var resolver = CreateResolver(new FileSystemPathSemantics(
            FileSystemPathSyntax.Windows,
            FileSystemCaseSensitivity.Insensitive));

        var result = await resolver.ResolveAsync(source, target, []);

        Assert.True(result.IsAvailable);
        Assert.Equal(MoveCleanupBoundaryKind.VolumeAnchor, result.Kind);
        Assert.Equal(sourceAnchor, result.Boundary);
        Assert.NotEqual(@"\\server\downloads", result.Boundary);
    }

    private static MoveCleanupBoundaryResolver CreateResolver(
        FileSystemPathSemantics? semantics = null)
    {
        var resolvedSemantics = semantics ?? FileSystemPathSemantics.CurrentHostDefault;
        return CreateResolver((path, _) => new FileSystemSemanticsResolution(
            resolvedSemantics,
            PathIdentityState.Valid,
            Path.GetPathRoot(path) ?? path));
    }

    private static MoveCleanupBoundaryResolver CreateResolver(
        Func<string, FileSystemCaseSensitivityMode, FileSystemSemanticsResolution> resolve)
    {
        var semanticsResolver = new Mock<IFileSystemSemanticsResolver>();
        semanticsResolver
            .Setup(resolver => resolver.ResolveAsync(
                It.IsAny<string>(),
                It.IsAny<FileSystemCaseSensitivityMode>(),
                It.IsAny<CancellationToken>()))
            .Returns((string path, FileSystemCaseSensitivityMode mode, CancellationToken _) =>
                ValueTask.FromResult(resolve(path, mode)));
        return new MoveCleanupBoundaryResolver(semanticsResolver.Object);
    }
}
