using System.Security.Cryptography;
using Listenarr.Infrastructure.Library.Files;
using Listenarr.Infrastructure.Persistence.Repositories;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.FileSystem;

[Trait("Name", "DockerWeakStorageOrganiseContractTests")]
[Trait("Category", "Infrastructure")]
public sealed class DockerWeakStorageOrganiseContractTests : BaseTests
{
    [NativeWeakStorageRemountFact]
    public async Task OwnerCommittedVerifiedOrganise_AfterWeakCifsRemount_RetainsOldSourceSafely()
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
                await CaptureOwnerCommittedRecoveryStateAsync(mountPath, databasePath);
                break;
            case "verify":
                await VerifyOwnerCommittedRecoveryStateAsync(databasePath);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown native verified-organise recovery phase '{phase}'.");
        }
    }

    [NativeWeakStorageRemountFact]
    public async Task SourceQuarantinedVerifiedOrganise_AfterWeakCifsRemount_PreservesRetirementArtifact()
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
                await CaptureSourceQuarantinedRecoveryStateAsync(mountPath, databasePath);
                break;
            case "verify":
                await VerifySourceQuarantinedRecoveryStateAsync(databasePath);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown native verified-organise quarantine recovery phase '{phase}'.");
        }
    }

    [NativeWeakStorageFact]
    public async Task VerifiedOrganise_TrackedFile_SucceedsOnWeakCifs()
    {
        var mountPath = Environment.GetEnvironmentVariable(
            NativeStorageIdentityFactAttribute.PathEnvironmentVariable)!;
        var rootPath = Path.Join(
            mountPath,
            "listenarr-native-organise-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);
        var root = new RootFolderBuilder()
            .WithName("Native Weak CIFS Organise")
            .WithPath(rootPath)
            .WithCaseSensitivityMode(FileSystemCaseSensitivityMode.Sensitive)
            .Build();
        root.ResolvedCaseSensitivity = FileSystemCaseSensitivity.Sensitive;
        root.PathIdentityState = PathIdentityState.Valid;
        root.PathIdentityKey = FileSystemPathIdentity.CreateKey(
            "root",
            rootPath,
            semantics);
        root.StorageContractRevision = 12;
        await _rootFolderRepository.AddAsync(root);

        var health = await _provider
            .GetRequiredService<IRootFolderStorageHealthResolver>()
            .ResolveAsync(root);
        Assert.Equal(RootFolderStorageState.Limited, health.State);
        Assert.Equal(RootFolderStorageReason.IdentityUnsupported, health.Reason);
        Assert.True(health.CanPublishAdditively);
        Assert.True(health.CanRetireVerifiedSource);
        Assert.False(health.CanMutateFilesystem);

        var sourceFolder = Path.Join(rootPath, "Old");
        Directory.CreateDirectory(sourceFolder);
        var sourcePath = Path.Join(sourceFolder, "old-name.m4b");
        await File.WriteAllTextAsync(sourcePath, "native-verified-organise-audio");
        var sourceCapability = await _provider
            .GetRequiredService<IFilePublicationSourceCapability>()
            .CheckAsync(sourcePath);
        Assert.True(sourceCapability.IsSupported, sourceCapability.Reason);
        Assert.True(sourceCapability.SourceProof.HasValue);
        Assert.False(sourceCapability.SourceProof.Value.HasDurablePhysicalObjectIdentity);

        var audiobook = await _audiobookRepository.AddAsync(new Audiobook
        {
            Title = "Native Organise Book",
            Authors = ["Native Author"],
            BasePath = sourceFolder
        });
        var identityResolver = _provider
            .GetRequiredService<IAudiobookFilePathIdentityResolver>();
        var sourceIdentity = await identityResolver.ResolveAsync(
            audiobook,
            sourcePath);
        Assert.Equal(PathIdentityState.Valid, sourceIdentity.State);
        var trackedFile = new AudiobookFile
        {
            AudiobookId = audiobook.Id,
            Path = sourcePath,
            Format = "m4b",
            Size = new FileInfo(sourcePath).Length
        };
        trackedFile.ApplyPathIdentity(sourcePath, sourceIdentity);
        trackedFile.ClearPhysicalObjectIdentity();
        trackedFile = await _audiobookFileRepository.AddAsync(trackedFile);

        await _applicationSettingsRepository.SaveAsync(
            new ApplicationSettingsBuilder()
                .WithOutputPath(rootPath)
                .WithFolderNamingPattern("{Author}/{Title}")
                .WithFileNamingPattern("{Title}")
                .Build());

        var renameService = _provider.GetRequiredService<IRenameService>();
        var preview = Assert.Single(await renameService.PreviewRenameAsync([audiobook.Id]));
        var filePreview = Assert.Single(preview.FileRenames);
        Assert.True(preview.HasChanges);
        Assert.NotNull(preview.CurrentFolderSemantics);
        Assert.NotNull(preview.NewFolderPath);
        Assert.NotNull(filePreview.NewPath);
        Assert.False(Directory.Exists(preview.NewFolderPath));

        var result = Assert.Single(await renameService.ExecuteRenameAsync(
        [
            new RenameOperation
            {
                AudiobookId = audiobook.Id,
                CurrentFolderPath = preview.CurrentFolderPath,
                CurrentFolderSemantics = preview.CurrentFolderSemantics,
                NewFolderPath = preview.NewFolderPath,
                FileRenames =
                [
                    new FileRenameOperation
                    {
                        FileId = trackedFile.Id,
                        CurrentPath = filePreview.CurrentPath!,
                        NewPath = filePreview.NewPath!
                    }
                ]
            }
        ]));

        Assert.True(result.Success, result.Error);
        Assert.False(File.Exists(sourcePath));
        Assert.True(File.Exists(filePreview.NewPath));
        Assert.True(Directory.Exists(preview.NewFolderPath));

        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var journal = await db.VerifiedFileRenameJournals
            .AsNoTracking()
            .SingleAsync(candidate => candidate.AudiobookId == audiobook.Id);
        Assert.Equal(VerifiedFileRenameState.Completed, journal.State);
        Assert.Equal(trackedFile.Id, journal.AudiobookFileId);
        Assert.Equal(1, journal.ExpectedBatchMemberCount);
        Assert.False(File.Exists(journal.StagingPath));

        var saved = await db.Audiobooks
            .AsNoTracking()
            .Include(candidate => candidate.Files)
            .SingleAsync(candidate => candidate.Id == audiobook.Id);
        var savedFile = Assert.Single(saved.Files!);
        Assert.Equal(
            Path.GetFullPath(filePreview.NewPath),
            Path.GetFullPath(savedFile.Path));
        Assert.Null(savedFile.PhysicalObjectIdentity);
    }

    private static async Task CaptureOwnerCommittedRecoveryStateAsync(
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
        var rootPath = Path.Join(mountPath, "organise-remount-" + token);
        var sourceDirectory = Path.Join(rootPath, "Old");
        var destinationDirectory = Path.Join(rootPath, "New");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var source = Path.Join(sourceDirectory, "old.m4b");
        var destination = Path.Join(destinationDirectory, "new.m4b");
        await File.WriteAllTextAsync(source, "native-organise-remount-audio");
        File.Copy(source, destination);

        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);
        var root = new RootFolder
        {
            Name = "Native Organise Remount Weak CIFS",
            Path = rootPath,
            CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
            ResolvedCaseSensitivity = FileSystemCaseSensitivity.Sensitive,
            PathIdentityState = PathIdentityState.Valid,
            PathIdentityKey = FileSystemPathIdentity.CreateKey(
                "root",
                rootPath,
                semantics),
            StorageContractRevision = 15
        };
        db.RootFolders.Add(root);
        var audiobook = new Audiobook
        {
            Title = "Native Organise Remount",
            BasePath = destinationDirectory
        };
        db.Audiobooks.Add(audiobook);
        await db.SaveChangesAsync();

        var destinationIdentity = AudiobookFilePathIdentity.CreateValid(
            destination,
            semantics,
            FileSystemCaseSensitivityMode.Sensitive,
            rootPath);
        var trackedFile = new AudiobookFile
        {
            AudiobookId = audiobook.Id,
            Path = destination,
            Format = "m4b",
            Size = new FileInfo(destination).Length
        };
        trackedFile.ApplyPathIdentity(destination, destinationIdentity);
        trackedFile.ClearPhysicalObjectIdentity();
        db.AudiobookFiles.Add(trackedFile);
        await db.SaveChangesAsync();

        var operationId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var manifest = VerifiedFileRenameBatchManifest.Create(
        [
            new VerifiedFileRenameBatchMember(
                trackedFile.Id,
                source,
                destination)
        ]);
        var bytes = await File.ReadAllBytesAsync(source);
        db.VerifiedFileRenameJournals.Add(new VerifiedFileRenameJournal
        {
            OperationId = operationId,
            BatchId = batchId,
            AudiobookId = audiobook.Id,
            AudiobookFileId = trackedFile.Id,
            ExpectedBatchMemberCount = manifest.ExpectedMemberCount,
            ExpectedBatchManifestSha256 = manifest.ManifestSha256,
            SourcePath = source,
            DestinationPath = destination,
            StagingPath = Path.Join(
                destinationDirectory,
                ".listenarr-organize-" + operationId.ToString("N") + ".partial"),
            RetirementPath = Path.Join(
                Path.GetDirectoryName(source)!,
                ".listenarr-organize-" + operationId.ToString("N") + ".source"),
            SourceLength = bytes.LongLength,
            SourceSha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            SourceRootFolderId = root.Id,
            SourceStorageContractRevision = root.StorageContractRevision,
            DestinationRootFolderId = root.Id,
            DestinationStorageContractRevision = root.StorageContractRevision,
            State = VerifiedFileRenameState.OwnerMetadataReconciled
        });
        await db.SaveChangesAsync();

        var health = await CreateNativeStorageHealthResolver().ResolveAsync(root);
        Assert.Equal(RootFolderStorageState.Limited, health.State);
        Assert.Equal(RootFolderStorageReason.IdentityUnsupported, health.Reason);
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(destination));
    }

    private static async Task VerifyOwnerCommittedRecoveryStateAsync(
        string databasePath)
    {
        Assert.True(
            File.Exists(databasePath),
            "The capture phase did not persist verified-organise SQLite state.");
        await using var provider = BuildSqliteProvider(databasePath);
        var factory = provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        var rootRepository = new EfRootFolderRepository(
            factory,
            NullLogger<EfRootFolderRepository>.Instance);
        var identityResolver = new AudiobookFilePathIdentityResolver(
            rootRepository,
            new FileSystemSemanticsResolver());
        var recovery = new VerifiedFileRenameRecoveryService(
            factory,
            identityResolver,
            TimeProvider.System,
            NullLogger<VerifiedFileRenameRecoveryService>.Instance);

        await recovery.ReconcileAsync();

        await using var db = await factory.CreateDbContextAsync();
        var journal = await db.VerifiedFileRenameJournals
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(
            VerifiedFileRenameState.CompletedSourceRetained,
            journal.State);
        Assert.True(File.Exists(journal.SourcePath));
        Assert.True(File.Exists(journal.DestinationPath));
        Assert.Contains("retained", journal.Error, StringComparison.OrdinalIgnoreCase);

        var trackedFile = await db.AudiobookFiles
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == journal.AudiobookFileId);
        Assert.Equal(
            Path.GetFullPath(journal.DestinationPath),
            Path.GetFullPath(trackedFile.Path));
        Assert.Null(trackedFile.PhysicalObjectIdentity);
        var root = await db.RootFolders.AsNoTracking().SingleAsync();
        var health = await CreateNativeStorageHealthResolver().ResolveAsync(root);
        Assert.Equal(RootFolderStorageState.Limited, health.State);
        Assert.Equal(RootFolderStorageReason.IdentityUnsupported, health.Reason);
    }

    private static async Task CaptureSourceQuarantinedRecoveryStateAsync(
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
        var rootPath = Path.Join(mountPath, "organise-quarantine-remount-" + token);
        var sourceDirectory = Path.Join(rootPath, "Old");
        var destinationDirectory = Path.Join(rootPath, "New");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var source = Path.Join(sourceDirectory, "old.m4b");
        var destination = Path.Join(destinationDirectory, "new.m4b");
        await File.WriteAllTextAsync(source, "native-organise-quarantine-audio");
        File.Copy(source, destination);

        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);
        var root = new RootFolder
        {
            Name = "Native Organise Quarantine Remount",
            Path = rootPath,
            CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
            ResolvedCaseSensitivity = FileSystemCaseSensitivity.Sensitive,
            PathIdentityState = PathIdentityState.Valid,
            PathIdentityKey = FileSystemPathIdentity.CreateKey("root", rootPath, semantics),
            StorageContractRevision = 16
        };
        db.RootFolders.Add(root);
        var audiobook = new Audiobook
        {
            Title = "Native Organise Quarantine Remount",
            BasePath = destinationDirectory
        };
        db.Audiobooks.Add(audiobook);
        await db.SaveChangesAsync();

        var trackedFile = new AudiobookFile
        {
            AudiobookId = audiobook.Id,
            Path = destination,
            Format = "m4b",
            Size = new FileInfo(destination).Length
        };
        trackedFile.ApplyPathIdentity(
            destination,
            AudiobookFilePathIdentity.CreateValid(
                destination,
                semantics,
                FileSystemCaseSensitivityMode.Sensitive,
                rootPath));
        trackedFile.ClearPhysicalObjectIdentity();
        db.AudiobookFiles.Add(trackedFile);
        await db.SaveChangesAsync();

        var operationId = Guid.NewGuid();
        var retirementPath = Path.Join(
            sourceDirectory,
            ".listenarr-organize-" + operationId.ToString("N") + ".source");
        var bytes = await File.ReadAllBytesAsync(source);
        File.Move(source, retirementPath);
        var manifest = VerifiedFileRenameBatchManifest.Create(
        [
            new VerifiedFileRenameBatchMember(trackedFile.Id, source, destination)
        ]);
        db.VerifiedFileRenameJournals.Add(new VerifiedFileRenameJournal
        {
            OperationId = operationId,
            BatchId = Guid.NewGuid(),
            AudiobookId = audiobook.Id,
            AudiobookFileId = trackedFile.Id,
            ExpectedBatchMemberCount = manifest.ExpectedMemberCount,
            ExpectedBatchManifestSha256 = manifest.ManifestSha256,
            SourcePath = source,
            DestinationPath = destination,
            StagingPath = Path.Join(
                destinationDirectory,
                ".listenarr-organize-" + operationId.ToString("N") + ".partial"),
            RetirementPath = retirementPath,
            SourceLength = bytes.LongLength,
            SourceSha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            SourceRootFolderId = root.Id,
            SourceStorageContractRevision = root.StorageContractRevision,
            DestinationRootFolderId = root.Id,
            DestinationStorageContractRevision = root.StorageContractRevision,
            State = VerifiedFileRenameState.SourceQuarantined
        });
        await db.SaveChangesAsync();

        Assert.False(File.Exists(source));
        Assert.True(File.Exists(retirementPath));
        Assert.True(File.Exists(destination));
    }

    private static async Task VerifySourceQuarantinedRecoveryStateAsync(
        string databasePath)
    {
        Assert.True(File.Exists(databasePath));
        await using var provider = BuildSqliteProvider(databasePath);
        var factory = provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        var rootRepository = new EfRootFolderRepository(
            factory,
            NullLogger<EfRootFolderRepository>.Instance);
        var recovery = new VerifiedFileRenameRecoveryService(
            factory,
            new AudiobookFilePathIdentityResolver(
                rootRepository,
                new FileSystemSemanticsResolver()),
            TimeProvider.System,
            NullLogger<VerifiedFileRenameRecoveryService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            recovery.ReconcileAsync());

        await using var db = await factory.CreateDbContextAsync();
        var journal = await db.VerifiedFileRenameJournals.AsNoTracking().SingleAsync();
        Assert.Equal(VerifiedFileRenameState.NeedsAttention, journal.State);
        Assert.False(File.Exists(journal.SourcePath));
        Assert.True(File.Exists(journal.RetirementPath));
        Assert.Equal(
            "native-organise-quarantine-audio",
            await File.ReadAllTextAsync(journal.RetirementPath));
        Assert.True(File.Exists(journal.DestinationPath));
        Assert.True(await new FileRenameRecoveryProbe(factory)
            .HasBlockingAsync(journal.AudiobookId));
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
