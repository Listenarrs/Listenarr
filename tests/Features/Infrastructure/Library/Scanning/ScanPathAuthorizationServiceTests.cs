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
        await AddAuthorizedRootAsync(configuredRoot);
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
            logger);

        var result = await service.AuthorizeAsync(scanRoot);

        Assert.True(result.IsAuthorized, result.Error);
        Assert.DoesNotContain(logger.Entries, log =>
            log.Level == LogLevel.Warning
            && log.Message.Contains("Audiobooks", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, log =>
            log.Level == LogLevel.Debug
            && log.Message.Contains("Audiobooks", StringComparison.Ordinal));
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
