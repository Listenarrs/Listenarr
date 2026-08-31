using System.Security.Cryptography;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "DockerWeakStorageImportContractTests")]
[Trait("Category", "Infrastructure")]
public sealed class DockerWeakStorageImportContractTests : BaseTests
{
    [NativeWeakStorageRemountFact]
    public async Task ManifestedDownloadClientCleanup_RecoversAfterWeakCifsRemount()
    {
        var mountPath = Environment.GetEnvironmentVariable(
            NativeStorageRemountFactAttribute.PathEnvironmentVariable)!;
        var databasePath = Environment.GetEnvironmentVariable(
            NativeStorageRemountFactAttribute.StatePathEnvironmentVariable)!;
        var phase = Environment.GetEnvironmentVariable(
            NativeStorageRemountFactAttribute.PhaseEnvironmentVariable)!;

        switch (phase.Trim().ToLowerInvariant())
        {
            case "capture":
                await CaptureManifestedRecoveryStateAsync(mountPath, databasePath);
                break;
            case "verify":
                await VerifyManifestedRecoveryStateAsync(databasePath);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown native storage recovery phase '{phase}'.");
        }
    }

    [NativeWeakStorageFact]
    public async Task VerifiedDownloadImport_MultiFileAndCompanion_SucceedsOnWeakCifs()
    {
        var mountPath = Environment.GetEnvironmentVariable(
            NativeStorageIdentityFactAttribute.PathEnvironmentVariable)!;
        var rootPath = Path.Join(
            mountPath,
            "listenarr-native-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);
        var root = new RootFolderBuilder()
            .WithName("Native Weak CIFS")
            .WithPath(rootPath)
            .WithCaseSensitivityMode(FileSystemCaseSensitivityMode.Sensitive)
            .Build();
        root.ResolvedCaseSensitivity = FileSystemCaseSensitivity.Sensitive;
        root.PathIdentityState = PathIdentityState.Valid;
        root.PathIdentityKey = FileSystemPathIdentity.CreateKey(
            "root",
            rootPath,
            semantics);
        root.WeakStorageSourceCleanupPolicy =
            WeakStorageSourceCleanupPolicy.DeleteSourceAfterVerifiedCopy;
        root.WeakStoragePolicyRevision = 4;
        root.StorageContractRevision = 6;
        await _rootFolderRepository.AddAsync(root);

        var health = await _provider
            .GetRequiredService<IRootFolderStorageHealthResolver>()
            .ResolveAsync(root);
        Assert.Equal(RootFolderStorageState.Limited, health.State);
        Assert.Equal(RootFolderStorageReason.IdentityUnsupported, health.Reason);
        Assert.True(health.CanPublishAdditively);
        Assert.False(health.CanMutateFilesystem);

        var sourceDirectory = Path.Join(
            mountPath,
            "download-native-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDirectory);
        var part1 = await FileService.GetFileAsync(
            sourceDirectory,
            "Part 1.mp3",
            "chapter-one");
        var part2 = await FileService.GetFileAsync(
            sourceDirectory,
            "Part 2.mp3",
            "chapter-two");
        var companion = await FileService.GetFileAsync(
            sourceDirectory,
            "cover.jpg",
            "cover-bytes");
        var audiobook = await _audiobookRepository.AddAsync(
            new AudiobookBuilder()
                .WithTitle("Native Weak CIFS")
                .WithBasePath(rootPath)
                .Build());
        await _applicationSettingsRepository.SaveAsync(
            new ApplicationSettingsBuilder()
                .WithOutputPath(rootPath)
                .WithMoveFileOnCompleted()
                .WithoutMetadataProcessing()
                .WithFolderNamingPattern("")
                .WithFileNamingPattern("{Title}")
                .WithMultiFileNamingPattern("{Title}-{DiskNumber:00}")
                .Build());
        var batchId = Guid.NewGuid();

        var results = await _provider
            .GetRequiredService<IDownloadImportService>()
            .ImportDownloadFilesAsync(
                audiobook,
                [part1, part2, companion],
                options: new DownloadImportOptions(
                    CompatibilityBatchId: batchId));

        Assert.Equal(3, results.Count);
        Assert.All(results, result => Assert.True(result.Success, result.Message));
        Assert.All(results, result => Assert.Equal(FileAction.Move, result.RequestedAction));
        Assert.All(results, result => Assert.Equal(FileAction.Copy, result.EffectiveAction));
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var journals = await db.CompatibilityFilePublicationJournals
            .AsNoTracking()
            .Where(journal => journal.BatchId == batchId)
            .OrderBy(journal => journal.SourcePath)
            .ToListAsync();
        Assert.Equal(3, journals.Count);
        var manifest = CompatibilityBatchManifest.Create([part1, part2, companion]);
        Assert.All(journals, journal =>
        {
            Assert.Equal(
                CompatibilityFilePublicationState.Completed,
                journal.State);
            Assert.True(
                journal.SourceDisposition
                    == CompatibilitySourceDisposition.DeferredToDownloadClient,
                $"Expected download-client-owned cleanup, got {journal.SourceDisposition}: {journal.Error}");
            Assert.Equal(
                manifest.ExpectedMemberCount,
                journal.ExpectedBatchMemberCount);
            Assert.Equal(
                manifest.SourceManifestSha256,
                journal.ExpectedBatchSourceManifestSha256);
            Assert.True(File.Exists(journal.DestinationPath));
        });
        Assert.All(results, result =>
            Assert.Equal(ImportSourceDisposition.Retired, result.SourceDisposition));
        Assert.All(results, result => Assert.Equal(
            "source_cleanup_deferred_to_download_client",
            result.WarningCode));
        Assert.True(File.Exists(part1));
        Assert.True(File.Exists(part2));
        Assert.True(File.Exists(companion));
    }

    private static async Task CaptureManifestedRecoveryStateAsync(
        string mountPath,
        string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        if (File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }

        await using var provider = BuildSqliteProvider(databasePath);
        var factory = provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();

        var token = Guid.NewGuid().ToString("N");
        var sourceDirectory = Path.Join(mountPath, "compat-remount-source-" + token);
        var destinationRoot = Path.Join(mountPath, "library-remount-" + token);
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationRoot);
        var source1 = Path.Join(sourceDirectory, "Part 1.mp3");
        var source2 = Path.Join(sourceDirectory, "Part 2.mp3");
        var destination1 = Path.Join(destinationRoot, "Part 1.mp3");
        var destination2 = Path.Join(destinationRoot, "Part 2.mp3");
        await File.WriteAllTextAsync(source1, "remount-one");
        await File.WriteAllTextAsync(source2, "remount-two");
        File.Copy(source1, destination1);
        File.Copy(source2, destination2);

        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);
        var root = new RootFolder
        {
            Name = "Native Remount Weak CIFS",
            Path = destinationRoot,
            CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
            ResolvedCaseSensitivity = FileSystemCaseSensitivity.Sensitive,
            PathIdentityState = PathIdentityState.Valid,
            PathIdentityKey = FileSystemPathIdentity.CreateKey(
                "root",
                destinationRoot,
                semantics),
            WeakStorageSourceCleanupPolicy =
                WeakStorageSourceCleanupPolicy.DeleteSourceAfterVerifiedCopy,
            WeakStoragePolicyRevision = 7,
            StorageContractRevision = 9
        };
        db.RootFolders.Add(root);
        var audiobook = new AudiobookBuilder()
            .WithTitle("Native Remount Weak CIFS")
            .WithBasePath(destinationRoot)
            .Build();
        db.Audiobooks.Add(audiobook);
        await db.SaveChangesAsync();

        var batchId = Guid.NewGuid();
        var manifest = CompatibilityBatchManifest.Create([source1, source2]);
        foreach (var pair in new[]
        {
            (Source: source1, Destination: destination1),
            (Source: source2, Destination: destination2)
        })
        {
            var bytes = await File.ReadAllBytesAsync(pair.Source);
            var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
            db.AudiobookFiles.Add(
                new AudiobookFileBuilder()
                    .WithAudiobook(audiobook)
                    .WithPath(pair.Destination)
                    .WithSize(bytes.Length)
                    .Build());
            db.CompatibilityFilePublicationJournals.Add(
                new CompatibilityFilePublicationJournal
                {
                    OperationId = Guid.NewGuid(),
                    BatchId = batchId,
                    ProtocolVersion = CompatibilityFilePublicationProtocol.Current,
                    RequestedAction = FileAction.Move,
                    EffectiveAction = FileAction.Copy,
                    SourceDisposition = CompatibilitySourceDisposition.Retained,
                    CleanupOwner = CompatibilityCleanupOwner.DownloadClient,
                    DestinationRootFolderId = root.Id,
                    DestinationPolicyRevision = root.WeakStoragePolicyRevision,
                    DestinationStorageContractRevision = root.StorageContractRevision,
                    SourcePath = pair.Source,
                    DestinationPath = pair.Destination,
                    SourceLength = bytes.Length,
                    SourceSha256 = sha256,
                    TargetLength = bytes.Length,
                    TargetSha256 = sha256,
                    AudiobookId = audiobook.Id,
                    IsCompanionFile = false,
                    State = CompatibilityFilePublicationState.RegistrationCommitted,
                    ExpectedBatchMemberCount = manifest.ExpectedMemberCount,
                    ExpectedBatchSourceManifestSha256 = manifest.SourceManifestSha256
                });
        }
        await db.SaveChangesAsync();

        var health = await CreateNativeStorageHealthResolver().ResolveAsync(root);
        Assert.Equal(RootFolderStorageState.Limited, health.State);
        Assert.Equal(RootFolderStorageReason.IdentityUnsupported, health.Reason);
        Assert.All(
            db.CompatibilityFilePublicationJournals.Where(journal => journal.BatchId == batchId),
            journal => Assert.True(File.Exists(journal.SourcePath)));
    }

    private static async Task VerifyManifestedRecoveryStateAsync(string databasePath)
    {
        Assert.True(File.Exists(databasePath), "The capture phase did not persist its SQLite state.");
        await using var provider = BuildSqliteProvider(databasePath);
        var factory = provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        var healthResolver = CreateNativeStorageHealthResolver();
        var cleanupCoordinator = new CompatibilitySourceCleanupCoordinator(
            factory,
            healthResolver,
            TimeProvider.System,
            NullLogger<CompatibilitySourceCleanupCoordinator>.Instance);
        var recovery = new CompatibilityFilePublicationRecoveryService(
            factory,
            cleanupCoordinator,
            TimeProvider.System,
            NullLogger<CompatibilityFilePublicationRecoveryService>.Instance);

        await recovery.ReconcileAsync();

        await using var db = await factory.CreateDbContextAsync();
        var journals = await db.CompatibilityFilePublicationJournals
            .AsNoTracking()
            .OrderBy(journal => journal.SourcePath)
            .ToListAsync();
        Assert.NotEmpty(journals);
        Assert.All(journals, journal =>
        {
            Assert.True(
                journal.State == CompatibilityFilePublicationState.Completed,
                $"Expected completed recovery, got {journal.State}: {journal.Error}");
            Assert.True(
                journal.SourceDisposition
                    == CompatibilitySourceDisposition.DeferredToDownloadClient,
                $"Expected recovered download-client cleanup deferral, got {journal.SourceDisposition}: {journal.Error}");
            Assert.True(File.Exists(journal.SourcePath));
            Assert.True(File.Exists(journal.DestinationPath));
        });
    }

    private static ServiceProvider BuildSqliteProvider(string databasePath)
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<ListenArrDbContext>(options =>
            options.UseSqlite(
                $"Data Source={databasePath}",
                sqlite => sqlite.MigrationsAssembly(
                    typeof(ListenArrDbContext).Assembly.GetName().Name)));
        return services.BuildServiceProvider();
    }

    private static IRootFolderStorageHealthResolver CreateNativeStorageHealthResolver() =>
        new RootFolderStorageHealthResolver(
            new DirectoryObjectIdentityResolver(),
            new FileSystemSemanticsResolver());
}
