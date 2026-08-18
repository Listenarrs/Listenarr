using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Scanning;

[Trait("Area", "Library")]
[Trait("Name", "ScanPathAuthorizationServiceTests")]
[Trait("Category", "Infrastructure")]
public sealed class ScanPathAuthorizationServiceTests : BaseTests
{
    [DirectoryLinkFact]
    public async Task AuthorizeAsync_LinkedAncestorOutsideConfiguredRoot_IsRejected()
    {
        var parent = FileService.GetTempDirectory("scan-authorization-linked-ancestor");
        var configuredRoot = Path.Join(parent, "library");
        var outsideRoot = Path.Join(parent, "outside");
        var outsideBook = Path.Join(outsideRoot, "Book");
        var linkedAncestor = Path.Join(configuredRoot, "alias");
        Directory.CreateDirectory(configuredRoot);
        Directory.CreateDirectory(outsideBook);
        Directory.CreateSymbolicLink(linkedAncestor, outsideRoot);
        var hostSemantics = FileSystemPathSemantics.CurrentHostDefault;
        await AddAuthorizedRootAsync(
            configuredRoot,
            caseSensitivityMode: hostSemantics.CaseSensitivity
                == FileSystemCaseSensitivity.Sensitive
                    ? FileSystemCaseSensitivityMode.Sensitive
                    : FileSystemCaseSensitivityMode.Insensitive);
        var service = _provider.GetRequiredService<IScanPathAuthorizationService>();

        var result = await service.AuthorizeAsync(
            Path.Join(linkedAncestor, "Book"));

        Assert.False(result.IsAuthorized);
        Assert.Equal(
            ScanPathAuthorizationFailure.IdentityUnavailable,
            result.Failure);
        Assert.Null(result.PhysicalIdentity);
        Assert.True(Directory.Exists(outsideBook));
    }

    [Fact]
    public async Task AuthorizeAsync_DurableIdentityUnsupported_UsesPinnedPathOnlyProof()
    {
        var configuredRoot = FileService.GetTempDirectory(
            "scan-authorization-limited-root");
        var scanRoot = Path.Join(configuredRoot, "Book");
        Directory.CreateDirectory(scanRoot);
        var root = await AddAuthorizedRootAsync(configuredRoot);
        var rootFolderService = new Mock<IRootFolderService>(MockBehavior.Strict);
        rootFolderService.Setup(service => service.GetAllAsync())
            .ReturnsAsync([root]);
        var configurationService = new Mock<IConfigurationService>(MockBehavior.Strict);
        configurationService.Setup(service => service.GetApplicationSettingsAsync())
            .ReturnsAsync(new ApplicationSettings());
        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        identityResolver
            .Setup(resolver => resolver.ResolveExistingAsync(
                configuredRoot,
                root.DirectoryObjectIdentityVersion!.Value,
                root.DirectoryObjectIdentity!,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DirectoryObjectIdentityResolution.Unavailable(
                "The filesystem does not expose a durable file handle or inode generation.",
                DirectoryObjectIdentityFailureKind.IdentityUnsupported));
        identityResolver
            .Setup(resolver => resolver.ResolveAsync(
                configuredRoot,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DirectoryObjectIdentityResolution.Unavailable(
                "The filesystem does not expose a durable file handle or inode generation.",
                DirectoryObjectIdentityFailureKind.IdentityUnsupported));
        identityResolver
            .Setup(resolver => resolver.ResolveAsync(
                scanRoot,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DirectoryObjectIdentityResolution.Unavailable(
                "The filesystem does not expose a durable file handle or inode generation.",
                DirectoryObjectIdentityFailureKind.IdentityUnsupported));
        var service = new ScanPathAuthorizationService(
            configurationService.Object,
            rootFolderService.Object,
            _provider.GetRequiredService<IFileSystemSemanticsResolver>(),
            identityResolver.Object,
            new CapturingScanAuthorizationLogger());

        var result = await service.AuthorizeAsync(scanRoot);

        Assert.True(result.IsAuthorized, result.Error);
        Assert.True(result.PhysicalIdentity.HasValue);
        Assert.Equal(
            ScanPathPhysicalProofKind.PinnedPathOnly,
            result.PhysicalIdentity.Value.ProofKind);
        Assert.False(result.PhysicalIdentity.Value.HasDurableGenerationProof);
        rootFolderService.VerifyAll();
        identityResolver.VerifyAll();
    }

    [Fact]
    public async Task AuthorizeAsync_UnsupportedPersistedIdentityWithCurrentStrongIdentity_RequiresReconfirmation()
    {
        var configuredRoot = FileService.GetTempDirectory(
            "scan-authorization-unsupported-persisted-root");
        var scanRoot = Path.Join(configuredRoot, "Book");
        Directory.CreateDirectory(scanRoot);
        var root = await AddAuthorizedRootAsync(configuredRoot);
        var rootFolderService = new Mock<IRootFolderService>(MockBehavior.Strict);
        rootFolderService.Setup(service => service.GetAllAsync())
            .ReturnsAsync([root]);
        var configurationService = new Mock<IConfigurationService>(MockBehavior.Strict);
        configurationService.Setup(service => service.GetApplicationSettingsAsync())
            .ReturnsAsync(new ApplicationSettings());
        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        identityResolver
            .Setup(resolver => resolver.ResolveExistingAsync(
                configuredRoot,
                root.DirectoryObjectIdentityVersion!.Value,
                root.DirectoryObjectIdentity!,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DirectoryObjectIdentityResolution.Unavailable(
                "Directory identity version is unsupported.",
                DirectoryObjectIdentityFailureKind.IdentityUnsupported));
        identityResolver
            .Setup(resolver => resolver.ResolveAsync(
                configuredRoot,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DirectoryObjectIdentityResolution(
                ManagedDirectoryIdentity.CurrentVersion,
                "current-strong-root",
                null));
        var service = new ScanPathAuthorizationService(
            configurationService.Object,
            rootFolderService.Object,
            _provider.GetRequiredService<IFileSystemSemanticsResolver>(),
            identityResolver.Object,
            new CapturingScanAuthorizationLogger());

        var result = await service.AuthorizeAsync(scanRoot);

        Assert.False(result.IsAuthorized);
        Assert.Equal(ScanPathAuthorizationFailure.IdentityUnavailable, result.Failure);
        Assert.Null(result.PhysicalIdentity);
        rootFolderService.VerifyAll();
        identityResolver.VerifyAll();
    }

    [Fact]
    public async Task AuthorizeAsync_LegacyWeakRootIdentity_UsesPinnedPathOnlyProof()
    {
        var configuredRoot = FileService.GetTempDirectory(
            "scan-authorization-legacy-weak-root");
        var scanRoot = Path.Join(configuredRoot, "Book");
        Directory.CreateDirectory(scanRoot);
        var root = await AddAuthorizedRootAsync(configuredRoot);
        var rootFolderService = new Mock<IRootFolderService>(MockBehavior.Strict);
        rootFolderService.Setup(service => service.GetAllAsync())
            .ReturnsAsync([root]);
        var configurationService = new Mock<IConfigurationService>(MockBehavior.Strict);
        configurationService.Setup(service => service.GetApplicationSettingsAsync())
            .ReturnsAsync(new ApplicationSettings());
        var liveIdentityResolver = _provider
            .GetRequiredService<IDirectoryObjectIdentityResolver>();
        var liveRootIdentity = await liveIdentityResolver.ResolveAsync(configuredRoot);
        var liveScanRootIdentity = await liveIdentityResolver.ResolveAsync(scanRoot);
        Assert.True(liveRootIdentity.IsAvailable, liveRootIdentity.UnavailableReason);
        Assert.True(liveScanRootIdentity.IsAvailable, liveScanRootIdentity.UnavailableReason);
        var identityResolver = new Mock<IDirectoryObjectIdentityResolver>(MockBehavior.Strict);
        identityResolver
            .Setup(resolver => resolver.ResolveExistingAsync(
                configuredRoot,
                root.DirectoryObjectIdentityVersion!.Value,
                root.DirectoryObjectIdentity!,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DirectoryObjectIdentityResolution.Unavailable(
                "Legacy Linux identity requires upgrade.",
                DirectoryObjectIdentityFailureKind.LegacyWeakIdentity));
        identityResolver
            .Setup(resolver => resolver.ResolveAsync(
                configuredRoot,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(liveRootIdentity);
        identityResolver
            .Setup(resolver => resolver.ResolveAsync(
                scanRoot,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(liveScanRootIdentity);
        var service = new ScanPathAuthorizationService(
            configurationService.Object,
            rootFolderService.Object,
            _provider.GetRequiredService<IFileSystemSemanticsResolver>(),
            identityResolver.Object,
            new CapturingScanAuthorizationLogger());

        var result = await service.AuthorizeAsync(scanRoot);

        Assert.True(result.IsAuthorized, result.Error);
        Assert.True(result.PhysicalIdentity.HasValue);
        Assert.Equal(
            ScanPathPhysicalProofKind.PinnedPathOnly,
            result.PhysicalIdentity.Value.ProofKind);
        Assert.False(result.PhysicalIdentity.Value.HasDurableGenerationProof);
        rootFolderService.VerifyAll();
        identityResolver.VerifyAll();
    }

    [LinuxFact]
    public async Task AuthorizeAsync_AmbiguousNestedManagedRoot_DoesNotFallBackToBroaderRootAuthority()
    {
        var outerRootPath = FileService.GetTempDirectory(
            "scan-authorization-ambiguous-outer");
        var innerRootPath = Path.Join(outerRootPath, "Managed Inner");
        var scanRoot = Path.Join(innerRootPath, "Book");
        Directory.CreateDirectory(scanRoot);
        var outerRoot = await AddAuthorizedRootAsync(
            outerRootPath,
            caseSensitivityMode: FileSystemCaseSensitivityMode.Sensitive);
        var ambiguousInnerPath = "/" + innerRootPath;
        Assert.False(FileSystemPathIdentity.TryDetectAbsoluteSyntax(
            ambiguousInnerPath,
            out _));
        var innerRoot = new RootFolder
        {
            Id = 999,
            Name = "Ambiguous Nested Root",
            Path = ambiguousInnerPath,
            CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive
        };
        var rootFolderService = new Mock<IRootFolderService>(MockBehavior.Strict);
        rootFolderService.Setup(service => service.GetAllAsync())
            .ReturnsAsync([outerRoot, innerRoot]);
        var configurationService = new Mock<IConfigurationService>(MockBehavior.Strict);
        configurationService.Setup(service => service.GetApplicationSettingsAsync())
            .ReturnsAsync(new ApplicationSettings());
        var service = new ScanPathAuthorizationService(
            configurationService.Object,
            rootFolderService.Object,
            _provider.GetRequiredService<IFileSystemSemanticsResolver>(),
            _provider.GetRequiredService<IDirectoryObjectIdentityResolver>(),
            new CapturingScanAuthorizationLogger());

        var result = await service.AuthorizeAsync(scanRoot);

        Assert.False(result.IsAuthorized);
        Assert.Equal(
            ScanPathAuthorizationFailure.ConfigurationUnavailable,
            result.Failure);
        Assert.Null(result.PhysicalIdentity);
    }

    [Fact]
    public async Task AuthorizeAsync_ConfiguredRootsExist_LegacyOutputPathDoesNotGrantIndependentScanAuthority()
    {
        var configuredRoot = FileService.GetTempDirectory(
            "scan-authorization-managed-root");
        var legacyOutputPath = FileService.GetTempDirectory(
            "scan-authorization-legacy-output");
        var scanRoot = Path.Join(legacyOutputPath, "Book");
        Directory.CreateDirectory(scanRoot);
        var root = await AddAuthorizedRootAsync(configuredRoot);
        var rootFolderService = new Mock<IRootFolderService>(MockBehavior.Strict);
        rootFolderService.Setup(service => service.GetAllAsync())
            .ReturnsAsync([root]);
        var configurationService = new Mock<IConfigurationService>(MockBehavior.Strict);
        configurationService.Setup(service => service.GetApplicationSettingsAsync())
            .ReturnsAsync(new ApplicationSettings
            {
                OutputPath = legacyOutputPath
            });
        var service = new ScanPathAuthorizationService(
            configurationService.Object,
            rootFolderService.Object,
            _provider.GetRequiredService<IFileSystemSemanticsResolver>(),
            _provider.GetRequiredService<IDirectoryObjectIdentityResolver>(),
            new CapturingScanAuthorizationLogger());

        var result = await service.AuthorizeAsync(scanRoot);

        Assert.False(result.IsAuthorized);
        Assert.Equal(
            ScanPathAuthorizationFailure.OutsideConfiguredRoots,
            result.Failure);
        Assert.Null(result.PhysicalIdentity);
    }

    [Fact]
    public async Task AuthorizeAsync_ConfiguredRoot_DoesNotRequireLegacySettingsRead()
    {
        var configuredRoot = FileService.GetTempDirectory(
            "scan-authorization-settings-independent-root");
        var scanRoot = Path.Join(configuredRoot, "Book");
        Directory.CreateDirectory(scanRoot);
        var root = await AddAuthorizedRootAsync(configuredRoot);
        var rootFolderService = new Mock<IRootFolderService>(MockBehavior.Strict);
        rootFolderService.Setup(service => service.GetAllAsync())
            .ReturnsAsync([root]);
        var configurationService = new Mock<IConfigurationService>(MockBehavior.Strict);
        configurationService.Setup(service => service.GetApplicationSettingsAsync())
            .ThrowsAsync(new InvalidOperationException("Injected legacy settings outage."));
        var service = new ScanPathAuthorizationService(
            configurationService.Object,
            rootFolderService.Object,
            _provider.GetRequiredService<IFileSystemSemanticsResolver>(),
            _provider.GetRequiredService<IDirectoryObjectIdentityResolver>(),
            new CapturingScanAuthorizationLogger());

        var result = await service.AuthorizeAsync(scanRoot);

        Assert.True(result.IsAuthorized, result.Error);
        Assert.Equal(
            FileUtils.NormalizeStoredPath(scanRoot),
            result.Path);
    }

    [Fact]
    public async Task ResolveDefaultAsync_ConfiguredDefaultRootTakesPrecedenceOverLegacyOutputPath()
    {
        var configuredRoot = FileService.GetTempDirectory(
            "scan-authorization-default-root");
        var legacyOutputPath = FileService.GetTempDirectory(
            "scan-authorization-default-legacy-output");
        var root = await AddAuthorizedRootAsync(configuredRoot);
        root.IsDefault = true;
        var rootFolderService = new Mock<IRootFolderService>(MockBehavior.Strict);
        rootFolderService.Setup(service => service.GetDefaultAsync())
            .ReturnsAsync(root);
        rootFolderService.Setup(service => service.GetAllAsync())
            .ReturnsAsync([root]);
        var configurationService = new Mock<IConfigurationService>(MockBehavior.Strict);
        configurationService.Setup(service => service.GetApplicationSettingsAsync())
            .ReturnsAsync(new ApplicationSettings
            {
                OutputPath = legacyOutputPath
            });
        var service = new ScanPathAuthorizationService(
            configurationService.Object,
            rootFolderService.Object,
            _provider.GetRequiredService<IFileSystemSemanticsResolver>(),
            _provider.GetRequiredService<IDirectoryObjectIdentityResolver>(),
            new CapturingScanAuthorizationLogger());

        var result = await service.ResolveDefaultAsync(preferredPath: null);

        Assert.True(result.IsAuthorized, result.Error);
        Assert.Equal(
            FileUtils.NormalizeStoredPath(configuredRoot),
            result.Path);
        Assert.False(FileSystemPathIdentity.AreEquivalent(
            result.Path!,
            legacyOutputPath,
            result.Identity!.Value.Semantics));
    }

    [WindowsFact]
    public async Task AuthorizeAsync_ForeignPersistedRootSyntax_CannotAliasWindowsRoot()
    {
        var configuredRoot = FileService.GetTempDirectory(
            "scan-authorization-foreign-root");
        var scanRoot = Path.Join(configuredRoot, "Book");
        Directory.CreateDirectory(scanRoot);
        await AddAuthorizedRootAsync(configuredRoot);
        var foreignRoot = "/" + Path.GetRelativePath(
                Path.GetPathRoot(configuredRoot)!,
                configuredRoot)
            .Replace('\\', '/');
        var factory = _provider.GetRequiredService<
            IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var root = await db.RootFolders.SingleAsync();
            root.Path = foreignRoot;
            await db.SaveChangesAsync();
        }
        var foreignScanRoot = foreignRoot + "/Book";

        var result = await _provider
            .GetRequiredService<IScanPathAuthorizationService>()
            .AuthorizeAsync(foreignScanRoot);

        Assert.False(result.IsAuthorized);
        Assert.NotEqual(
            ScanPathAuthorizationFailure.None,
            result.Failure);
        Assert.True(Directory.Exists(scanRoot));
    }

    [WindowsFact]
    public async Task AuthorizeAsync_ForeignFallbackOutputPath_DoesNotEmitWarning()
    {
        var configuredRoot = FileService.GetTempDirectory(
            "scan-authorization-valid-windows-root");
        var scanRoot = Path.Join(configuredRoot, "Book");
        Directory.CreateDirectory(scanRoot);
        var root = await AddAuthorizedRootAsync(configuredRoot);
        var rootFolderService = new Mock<IRootFolderService>();
        rootFolderService.Setup(service => service.GetAllAsync())
            .ReturnsAsync([root]);
        var configurationService = new Mock<IConfigurationService>();
        configurationService.Setup(service => service.GetApplicationSettingsAsync())
            .ReturnsAsync(new ApplicationSettings
            {
                OutputPath = "/server/mnt/drive/Audiobooks"
            });
        var logger = new CapturingScanAuthorizationLogger();
        var service = new ScanPathAuthorizationService(
            configurationService.Object,
            rootFolderService.Object,
            _provider.GetRequiredService<IFileSystemSemanticsResolver>(),
            _provider.GetRequiredService<IDirectoryObjectIdentityResolver>(),
            logger);

        var result = await service.AuthorizeAsync(scanRoot);

        Assert.True(result.IsAuthorized, result.Error);
        Assert.DoesNotContain(logger.Entries, log =>
            log.Message.Contains("Audiobooks", StringComparison.Ordinal));
    }

    private sealed class CapturingScanAuthorizationLogger
        : ILogger<ScanPathAuthorizationService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task AuthorizeAsync_AuthorizedRootWithChangedFilesystemSemantics_IsRejectedUntilRepaired()
    {
        var configuredRoot = FileService.GetTempDirectory(
            "scan-authorization-semantics-changed-root");
        var scanRoot = Path.Join(configuredRoot, "Book");
        Directory.CreateDirectory(scanRoot);
        await AddAuthorizedRootAsync(configuredRoot);
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var root = await db.RootFolders.SingleAsync();
            var actual = FileSystemPathSemantics.CurrentHostDefault;
            var persistedSensitivity = actual.CaseSensitivity
                == FileSystemCaseSensitivity.Sensitive
                    ? FileSystemCaseSensitivity.Insensitive
                    : FileSystemCaseSensitivity.Sensitive;
            var persisted = new FileSystemPathSemantics(actual.Syntax, persistedSensitivity);
            root.CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto;
            root.ResolvedCaseSensitivity = persistedSensitivity;
            root.PathIdentityState = PathIdentityState.Valid;
            root.PathIdentityKey = FileSystemPathIdentity.CreateKey(
                "root",
                configuredRoot,
                persisted);
            await db.SaveChangesAsync();
        }
        var service = _provider.GetRequiredService<IScanPathAuthorizationService>();

        var result = await service.AuthorizeAsync(scanRoot);

        Assert.False(result.IsAuthorized);
        Assert.NotEqual(ScanPathAuthorizationFailure.None, result.Failure);
        Assert.Null(result.PhysicalIdentity);
    }

    [Fact]
    public async Task AuthorizeAsync_AuthorizedRootReturnsAfterTransientFailure_UsesLiveGeneration()
    {
        var configuredRoot = FileService.GetTempDirectory(
            "scan-authorization-transient-root-failure");
        var scanRoot = Path.Join(configuredRoot, "Book");
        Directory.CreateDirectory(scanRoot);
        await AddAuthorizedRootAsync(configuredRoot);
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var root = await db.RootFolders.SingleAsync();
            root.DirectoryObjectIdentityUnavailableReason =
                "The directory was unavailable during startup.";
            await db.SaveChangesAsync();
        }
        var service = _provider.GetRequiredService<IScanPathAuthorizationService>();

        var result = await service.AuthorizeAsync(scanRoot);

        Assert.True(result.IsAuthorized, result.Error);
        Assert.NotNull(result.PhysicalIdentity);
    }

    [Fact]
    public async Task AuthorizeAsync_EnrolledRootReplacedAfterIdentityCheck_IsRejected()
    {
        var parent = FileService.GetTempDirectory(
            "scan-authorization-root-race");
        var configuredRoot = Path.Join(parent, "library");
        var scanRoot = Path.Join(configuredRoot, "Book");
        var displacedRoot = Path.Join(parent, "library-original");
        Directory.CreateDirectory(scanRoot);
        await AddAuthorizedRootAsync(configuredRoot);
        var service = _provider.GetRequiredService<IScanPathAuthorizationService>();
        var rootOpenCount = 0;
        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        using var hook = ExclusiveDirectoryCreator.PushBeforeOpenParentHook(path =>
        {
            if (!semantics.Comparer.Equals(
                    Path.GetFullPath(path),
                    Path.GetFullPath(configuredRoot)))
            {
                return;
            }

            rootOpenCount++;
            if (rootOpenCount != 2)
            {
                return;
            }

            Directory.Move(configuredRoot, displacedRoot);
            Directory.CreateDirectory(scanRoot);
        });

        var result = await service.AuthorizeAsync(scanRoot);

        Assert.False(result.IsAuthorized);
        Assert.Equal(
            ScanPathAuthorizationFailure.IdentityUnavailable,
            result.Failure);
        Assert.Null(result.PhysicalIdentity);
        Assert.True(Directory.Exists(Path.Join(displacedRoot, "Book")));
        Assert.True(Directory.Exists(scanRoot));
    }

    [Fact]
    public async Task AuthorizeAsync_ReplacedEnrolledRoot_IsRejected()
    {
        var parent = FileService.GetTempDirectory("scan-authorization-root-replacement");
        var configuredRoot = Path.Join(parent, "library");
        var scanRoot = Path.Join(configuredRoot, "Book");
        var displacedRoot = Path.Join(parent, "library-original");
        Directory.CreateDirectory(scanRoot);
        await AddAuthorizedRootAsync(configuredRoot);
        var service = _provider.GetRequiredService<IScanPathAuthorizationService>();
        var original = await service.AuthorizeAsync(scanRoot);
        Assert.True(original.IsAuthorized, original.Error);

        Directory.Move(configuredRoot, displacedRoot);
        Directory.CreateDirectory(scanRoot);
        var replacement = await service.AuthorizeAsync(scanRoot);

        Assert.False(replacement.IsAuthorized);
        Assert.Equal(
            ScanPathAuthorizationFailure.IdentityUnavailable,
            replacement.Failure);
        Assert.Null(replacement.PhysicalIdentity);
        Assert.True(Directory.Exists(Path.Join(displacedRoot, "Book")));
        Assert.True(Directory.Exists(scanRoot));
    }
}
