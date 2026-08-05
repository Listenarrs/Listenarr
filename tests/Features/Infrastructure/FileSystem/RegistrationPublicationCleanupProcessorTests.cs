using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "RegistrationPublicationCleanupProcessorTests")]
[Trait("Category", "Infrastructure")]
public sealed class RegistrationPublicationCleanupProcessorTests : BaseTests
{
    [WindowsFact]
    public async Task RunCycleAsync_ForeignPersistedRoot_DoesNotProcessWindowsAliasCleanup()
    {
        var root = FileService.GetWindowsRootRelativeTempDirectory(
            "registration-cleanup-foreign-root");
        var pending = await CreatePendingCleanupAsync(root, audiobookId: 40);
        var foreignRoot = TempFileService
            .GetWindowsRootRelativeForeignAlias(root);
        using var provider = BuildProvider(
            foreignRoot,
            pending,
            registeredPhysicalIdentity: null);
        var processor = CreateProcessor(provider);

        await processor.RunCycleAsync(CancellationToken.None);

        Assert.True(Directory.Exists(pending.StateDirectoryPath));
        Assert.Equal("audio", await File.ReadAllTextAsync(pending.DestinationPath));
        Assert.Equal("audio", await File.ReadAllTextAsync(pending.SourcePath));
    }

    [WindowsFact]
    public async Task CleanupIntent_ForeignPersistedSourceAlias_IsRejectedBeforeRecovery()
    {
        var root = FileService.GetTempDirectory("registration-cleanup-foreign-intent-source");
        var pending = await CreatePendingCleanupAsync(root, audiobookId: 49);
        var intentPath = Path.Join(
            pending.StateDirectoryPath,
            "registration.cleanup.json");
        var intent = System.Text.Json.Nodes.JsonNode.Parse(
            await File.ReadAllTextAsync(intentPath))!;
        var sourceFullPath = Path.GetFullPath(pending.SourcePath);
        var foreignSource = "//?/" + sourceFullPath.Replace('\\', '/');
        Assert.True(File.Exists(foreignSource));
        Assert.Equal("audio", await File.ReadAllTextAsync(foreignSource));
        intent["SourcePath"] = foreignSource;
        await File.WriteAllTextAsync(intentPath, intent.ToJsonString());
        var mover = new FileMover(
            NullLogger<FileMover>.Instance,
            semanticsResolver: new FileSystemSemanticsResolver());

        var candidate = mover.TryReadRegistrationPublicationCleanupCandidate(
            pending.StateDirectoryPath);

        Assert.Null(candidate);
        Assert.True(Directory.Exists(pending.StateDirectoryPath));
        Assert.Equal("audio", await File.ReadAllTextAsync(pending.SourcePath));
        Assert.Equal("audio", await File.ReadAllTextAsync(pending.DestinationPath));
    }

    [WindowsFact]
    public async Task RunCycleAsync_ForeignRegisteredPathAlias_DoesNotProveCommittedRegistration()
    {
        var root = FileService.GetWindowsRootRelativeTempDirectory(
            "registration-cleanup-foreign-registration");
        var pending = await CreatePendingCleanupAsync(root, audiobookId: 48);
        var foreignDestination = TempFileService
            .GetWindowsRootRelativeForeignAlias(pending.DestinationPath);
        using var provider = BuildProvider(
            root,
            pending,
            registeredPhysicalIdentity: pending.PhysicalObjectIdentity,
            registeredPath: foreignDestination);
        var processor = CreateProcessor(provider);

        await processor.RunCycleAsync(CancellationToken.None);

        Assert.False(Directory.Exists(pending.StateDirectoryPath));
        Assert.False(File.Exists(pending.DestinationPath));
        Assert.Equal("audio", await File.ReadAllTextAsync(pending.SourcePath));
    }

    [Fact]
    public async Task RunCycleAsync_ExactCommittedGeneration_RetiresPendingCleanup()
    {
        var root = FileService.GetTempDirectory("registration-cleanup-committed");
        var pending = await CreatePendingCleanupAsync(root, audiobookId: 41);
        using var provider = BuildProvider(
            root,
            pending,
            registeredPhysicalIdentity: pending.PhysicalObjectIdentity);
        var processor = CreateProcessor(provider);

        await processor.RunCycleAsync(CancellationToken.None);

        Assert.False(Directory.Exists(pending.StateDirectoryPath));
        Assert.Equal("audio", await File.ReadAllTextAsync(pending.DestinationPath));
        Assert.Equal("audio", await File.ReadAllTextAsync(pending.SourcePath));
    }

    [Fact]
    public async Task RunCycleAsync_CommittedGenerationWithUnavailableDestinationSemantics_PreservesPublication()
    {
        var root = FileService.GetTempDirectory("registration-cleanup-unavailable-semantics");
        var destinationParent = Path.Join(root, "published");
        var pending = await CreatePendingCleanupAsync(
            root,
            audiobookId: 50,
            destinationParent);
        using var provider = BuildProvider(
            root,
            pending,
            registeredPhysicalIdentity: pending.PhysicalObjectIdentity);
        var resolver = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        resolver.Setup(candidate => candidate.ResolveAsync(
                It.IsAny<string>(),
                It.IsAny<FileSystemCaseSensitivityMode>(),
                It.IsAny<CancellationToken>()))
            .Returns((string path, FileSystemCaseSensitivityMode _, CancellationToken _) =>
                ValueTask.FromResult(string.Equals(
                    Path.GetFullPath(path),
                    Path.GetFullPath(destinationParent),
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal)
                    ? new FileSystemSemanticsResolution(
                        FileSystemPathSemantics.CurrentHostDefault,
                        PathIdentityState.Unavailable,
                        path,
                        "simulated transient semantics failure")
                    : new FileSystemSemanticsResolution(
                        FileSystemPathSemantics.CurrentHostDefault,
                        PathIdentityState.Valid,
                        path,
                        CanonicalPath: path)));
        var processor = CreateProcessor(provider, resolver.Object);

        await processor.RunCycleAsync(CancellationToken.None);

        Assert.True(Directory.Exists(pending.StateDirectoryPath));
        Assert.Equal("audio", await File.ReadAllTextAsync(pending.DestinationPath));
        Assert.Equal("audio", await File.ReadAllTextAsync(pending.SourcePath));
    }

    [Fact]
    public async Task RunCycleAsync_MissingCommittedGeneration_RollsBackPublishedAlias()
    {
        var root = FileService.GetTempDirectory("registration-cleanup-missing");
        var pending = await CreatePendingCleanupAsync(root, audiobookId: 42);
        using var provider = BuildProvider(
            root,
            pending,
            registeredPhysicalIdentity: null);
        var processor = CreateProcessor(provider);

        await processor.RunCycleAsync(CancellationToken.None);

        Assert.False(Directory.Exists(pending.StateDirectoryPath));
        Assert.False(File.Exists(pending.DestinationPath));
        Assert.Equal("audio", await File.ReadAllTextAsync(pending.SourcePath));
    }

    [Fact]
    public async Task RunCycleAsync_MissingAudiobook_RollsBackAbandonedPublication()
    {
        var root = FileService.GetTempDirectory("registration-cleanup-missing-audiobook");
        var pending = await CreatePendingCleanupAsync(root, audiobookId: 43);
        using var provider = BuildProvider(
            root,
            pending,
            registeredPhysicalIdentity: null,
            audiobookExists: false);
        var processor = CreateProcessor(provider);

        await processor.RunCycleAsync(CancellationToken.None);

        Assert.False(Directory.Exists(pending.StateDirectoryPath));
        Assert.False(File.Exists(pending.DestinationPath));
        Assert.Equal("audio", await File.ReadAllTextAsync(pending.SourcePath));
    }

    [Fact]
    public async Task RunCycleAsync_ConflictingRegisteredGeneration_PreservesPendingCleanup()
    {
        var root = FileService.GetTempDirectory("registration-cleanup-conflicting");
        var pending = await CreatePendingCleanupAsync(root, audiobookId: 44);
        using var provider = BuildProvider(
            root,
            pending,
            registeredPhysicalIdentity: "different-generation");
        var processor = CreateProcessor(provider);

        await processor.RunCycleAsync(CancellationToken.None);

        Assert.True(Directory.Exists(pending.StateDirectoryPath));
        Assert.Equal("audio", await File.ReadAllTextAsync(pending.DestinationPath));
        Assert.Equal("audio", await File.ReadAllTextAsync(pending.SourcePath));
    }

    [Fact]
    public async Task RunCycleAsync_CommittedGenerationAfterSourceRetirement_RetiresPendingCleanup()
    {
        var root = FileService.GetTempDirectory("registration-cleanup-source-retired");
        var pending = await CreatePendingCleanupAsync(root, audiobookId: 51);
        File.Delete(pending.SourcePath);
        using var provider = BuildProvider(
            root,
            pending,
            registeredPhysicalIdentity: pending.PhysicalObjectIdentity);
        var processor = CreateProcessor(provider);

        await processor.RunCycleAsync(CancellationToken.None);

        Assert.False(Directory.Exists(pending.StateDirectoryPath));
        Assert.Equal("audio", await File.ReadAllTextAsync(pending.DestinationPath));
        Assert.False(File.Exists(pending.SourcePath));
    }

    [Fact]
    public async Task RunCycleAsync_SourceGenerationChanged_PreservesPublishedAliasAndCleanupState()
    {
        var root = FileService.GetTempDirectory("registration-cleanup-source-replaced");
        var pending = await CreatePendingCleanupAsync(root, audiobookId: 45);
        File.Delete(pending.SourcePath);
        await File.WriteAllTextAsync(pending.SourcePath, "replacement source");
        using var provider = BuildProvider(
            root,
            pending,
            registeredPhysicalIdentity: null);
        var processor = CreateProcessor(provider);

        await processor.RunCycleAsync(CancellationToken.None);

        Assert.True(Directory.Exists(pending.StateDirectoryPath));
        Assert.Equal("audio", await File.ReadAllTextAsync(pending.DestinationPath));
        Assert.Equal(
            "replacement source",
            await File.ReadAllTextAsync(pending.SourcePath));
    }

    [Fact]
    public async Task RunCycleAsync_RollbackCrashAfterDestinationRetirement_ResumesCleanup()
    {
        var root = FileService.GetTempDirectory("registration-cleanup-rollback-retry");
        var pending = await CreatePendingCleanupAsync(root, audiobookId: 46);
        var crashingMover = new FileMover(
            NullLogger<FileMover>.Instance,
            semanticsResolver: new FileSystemSemanticsResolver())
        {
            AfterUncommittedRegistrationDestinationRetiredForTest = () =>
                throw new InvalidOperationException(
                    "simulated rollback crash")
        };
        using (var crashingProvider = BuildProvider(
            root,
            pending,
            registeredPhysicalIdentity: null,
            recoveryMover: crashingMover))
        {
            await CreateProcessor(crashingProvider)
                .RunCycleAsync(CancellationToken.None);
        }

        Assert.False(File.Exists(pending.DestinationPath));
        Assert.True(Directory.Exists(pending.StateDirectoryPath));
        Assert.Equal("audio", await File.ReadAllTextAsync(pending.SourcePath));

        using var recoveryProvider = BuildProvider(
            root,
            pending,
            registeredPhysicalIdentity: null);
        await CreateProcessor(recoveryProvider)
            .RunCycleAsync(CancellationToken.None);

        Assert.False(Directory.Exists(pending.StateDirectoryPath));
        Assert.False(File.Exists(pending.DestinationPath));
        Assert.Equal("audio", await File.ReadAllTextAsync(pending.SourcePath));
    }

    [Fact]
    public async Task RunCycleAsync_DestinationGenerationReplaced_PreservesReplacementAndCleanupState()
    {
        var root = FileService.GetTempDirectory("registration-cleanup-replaced");
        var pending = await CreatePendingCleanupAsync(root, audiobookId: 47);
        File.Delete(pending.DestinationPath);
        await File.WriteAllTextAsync(pending.DestinationPath, "replacement");
        using var provider = BuildProvider(
            root,
            pending,
            registeredPhysicalIdentity: pending.PhysicalObjectIdentity);
        var processor = CreateProcessor(provider);

        await processor.RunCycleAsync(CancellationToken.None);

        Assert.True(Directory.Exists(pending.StateDirectoryPath));
        Assert.Equal(
            "replacement",
            await File.ReadAllTextAsync(pending.DestinationPath));
        Assert.Equal("audio", await File.ReadAllTextAsync(pending.SourcePath));
    }

    private static RegistrationPublicationCleanupProcessor CreateProcessor(
        ServiceProvider provider,
        IFileSystemSemanticsResolver? semanticsResolver = null) =>
        new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            semanticsResolver ?? new FileSystemSemanticsResolver(),
            new Listenarr.Application.Common.FilesystemMutationCoordinator(),
            new Listenarr.Application.Audiobooks.Jobs.AudiobookOperationCoordinator(),
            NullLogger<RegistrationPublicationCleanupProcessor>.Instance);

    private static ServiceProvider BuildProvider(
        string root,
        PendingCleanup pending,
        string? registeredPhysicalIdentity,
        FileMover? recoveryMover = null,
        bool audiobookExists = true,
        string? registeredPath = null)
    {
        var rootRepository = new Mock<IRootFolderRepository>(MockBehavior.Strict);
        rootRepository.Setup(repository => repository.GetAllAsync())
            .ReturnsAsync([
                new RootFolder
                {
                    Id = 1,
                    Name = "Library",
                    Path = root,
                    IsDefault = true
                }
            ]);
        var configuration = new Mock<IConfigurationService>(MockBehavior.Strict);
        configuration.Setup(service => service.GetApplicationSettingsAsync())
            .ReturnsAsync(new ApplicationSettings { OutputPath = root });
        var audiobook = new Audiobook
        {
            Id = pending.AudiobookId,
            Title = "Cleanup Book",
            BasePath = root
        };
        var audiobookRepository = new Mock<IAudiobookRepository>(MockBehavior.Strict);
        audiobookRepository.Setup(repository => repository.GetByIdSnapshotAsync(
                pending.AudiobookId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(audiobookExists ? audiobook : null);
        var fileRepository = new Mock<IAudiobookFileRepository>(MockBehavior.Strict);
        var files = new List<AudiobookFile>();
        if (!string.IsNullOrWhiteSpace(registeredPhysicalIdentity))
        {
            var file = AudiobookFile.CreateUnresolved(
                registeredPath ?? pending.DestinationPath);
            file.Id = 1;
            file.AudiobookId = pending.AudiobookId;
            file.ApplyPhysicalObjectIdentity(
                registeredPhysicalIdentity,
                DateTime.UtcNow);
            files.Add(file);
        }
        fileRepository.Setup(repository => repository.GetByAudiobookIdAsync(
                pending.AudiobookId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(files);
        recoveryMover ??= new FileMover(
            NullLogger<FileMover>.Instance,
            semanticsResolver: new FileSystemSemanticsResolver());

        var services = new ServiceCollection();
        services.AddScoped(_ => rootRepository.Object);
        services.AddScoped(_ => configuration.Object);
        services.AddScoped(_ => audiobookRepository.Object);
        services.AddScoped(_ => fileRepository.Object);
        services.AddScoped(_ => recoveryMover);
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static async Task<PendingCleanup> CreatePendingCleanupAsync(
        string root,
        int audiobookId,
        string? destinationParent = null)
    {
        destinationParent ??= root;
        Directory.CreateDirectory(destinationParent);
        var source = Path.Join(root, "source.m4b");
        var destination = Path.Join(destinationParent, "destination.m4b");
        await File.WriteAllTextAsync(source, "audio");
        var crashingMover = new FileMover(
            NullLogger<FileMover>.Instance,
            semanticsResolver: new FileSystemSemanticsResolver())
        {
            AfterRegistrationPublicationClaimRetiredForTest = () =>
                throw new InvalidOperationException("simulated cleanup crash")
        };
        using var lease = await crashingMover.PrepareActionForRegistrationAsync(
            FileAction.HardlinkCopy,
            source,
            destination,
            Guid.NewGuid());
        Assert.NotNull(lease);
        Assert.True(lease.PrepareCleanupRecovery(audiobookId));
        var physicalObjectIdentity = lease.PhysicalObjectIdentity;
        Assert.Equal(
            RegistrationPublicationCompletion.CommittedCleanupPending,
            lease.CompletePublication());
        var stateDirectory = Assert.Single(
            Directory.EnumerateDirectories(
                destinationParent,
                ".listenarr-registration-publication-*.state"));
        Assert.True(File.Exists(Path.Join(
            stateDirectory,
            "registration.cleanup.json")));
        return new PendingCleanup(
            audiobookId,
            source,
            destination,
            stateDirectory,
            physicalObjectIdentity);
    }

    private sealed record PendingCleanup(
        int AudiobookId,
        string SourcePath,
        string DestinationPath,
        string StateDirectoryPath,
        string PhysicalObjectIdentity);
}
