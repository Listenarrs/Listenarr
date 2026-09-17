using Listenarr.Tests.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

[Trait("Name", "FileRenameCommitStoreTests")]
[Trait("Category", "Infrastructure")]
public sealed class FileRenameCommitStoreTests : BaseTests
{
    [Fact]
    public async Task CommitOwnerMetadataAsync_PersistsAudiobookAndJournalTerminalStateTogether()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ListenArrDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var audiobook = new Audiobook
        {
            Title = "Rename Commit",
            BasePath = "/library/old"
        };
        db.Audiobooks.Add(audiobook);
        await db.SaveChangesAsync();
        var root = FileService.GetTempDirectory("rename-commit-success");
        var sourcePath = Path.Join(root, "old.m4b");
        var destinationPath = Path.Join(root, "new.m4b");
        await File.WriteAllTextAsync(destinationPath, "audio");
        var targetIdentity = GetFileIdentity(destinationPath);
        var operationId = Guid.NewGuid();
        var journal = CreateCompletedJournal(operationId, audiobook.Id);
        journal.SourcePath = sourcePath;
        journal.DestinationPath = destinationPath;
        journal.SourcePhysicalObjectIdentity = targetIdentity;
        journal.SourceLength = new FileInfo(destinationPath).Length;
        journal.TargetPhysicalObjectIdentity = targetIdentity;
        db.FileMutationJournals.Add(journal);
        await db.SaveChangesAsync();

        audiobook.BasePath = "/library/new";
        var store = new FileRenameCommitStore(db, TimeProvider.System);
        await store.CommitOwnerMetadataAsync(
            audiobook.Id,
            [operationId]);

        await using var verification = new ListenArrDbContext(options);
        Assert.Equal(
            "/library/new",
            (await verification.Audiobooks.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == audiobook.Id)).BasePath);
        Assert.Equal(
            FileMutationJournalState.OwnerMetadataReconciled,
            (await verification.FileMutationJournals.AsNoTracking()
                .SingleAsync(candidate => candidate.OperationId == operationId)).State);
    }

    [LinuxFact]
    public async Task CommitOwnerMetadataAsync_TargetReplacedAfterSave_RollsBackTrackedPathChange()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ListenArrDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var audiobook = new Audiobook
        {
            Title = "Rename Commit Post-Save Replacement",
            BasePath = "/library/old"
        };
        db.Audiobooks.Add(audiobook);
        await db.SaveChangesAsync();

        var root = FileService.GetTempDirectory("rename-commit-post-save-replaced-target");
        var sourcePath = Path.Join(root, "old.m4b");
        var destinationPath = Path.Join(root, "new.m4b");
        await File.WriteAllTextAsync(destinationPath, "owned");
        var targetIdentity = GetFileIdentity(destinationPath);
        var operationId = Guid.NewGuid();
        db.FileMutationJournals.Add(new FileMutationJournal
        {
            OperationId = operationId,
            Action = FileAction.Move,
            SourcePath = sourcePath,
            DestinationPath = destinationPath,
            SourcePhysicalObjectIdentity = targetIdentity,
            SourceLength = new FileInfo(destinationPath).Length,
            TargetPhysicalObjectIdentity = targetIdentity,
            AudiobookId = audiobook.Id,
            AudiobookFileId = 0,
            State = FileMutationJournalState.Completed
        });
        await db.SaveChangesAsync();

        audiobook.BasePath = "/library/new";
        var store = new FileRenameCommitStore(db, TimeProvider.System)
        {
            AfterSaveBeforeTargetRevalidationForTest = () =>
            {
                File.Delete(destinationPath);
                File.WriteAllText(destinationPath, "foreign");
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CommitOwnerMetadataAsync(audiobook.Id, [operationId]));

        await using var verification = new ListenArrDbContext(options);
        Assert.Equal(
            "/library/old",
            (await verification.Audiobooks.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == audiobook.Id)).BasePath);
        Assert.Equal(
            FileMutationJournalState.Completed,
            (await verification.FileMutationJournals.AsNoTracking()
                .SingleAsync(candidate => candidate.OperationId == operationId)).State);
        Assert.Equal("foreign", await File.ReadAllTextAsync(destinationPath));
    }

    [Fact]
    public async Task CommitOwnerMetadataAsync_ReplacedCompletedTargetDoesNotPersistTrackedPathChange()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ListenArrDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var audiobook = new Audiobook
        {
            Title = "Rename Commit Replaced Target",
            BasePath = "/library/old"
        };
        db.Audiobooks.Add(audiobook);
        await db.SaveChangesAsync();

        var root = FileService.GetTempDirectory("rename-commit-replaced-target");
        var sourcePath = Path.Join(root, "old.m4b");
        var destinationPath = Path.Join(root, "new.m4b");
        await File.WriteAllTextAsync(destinationPath, "owned");
        var targetIdentity = GetFileIdentity(destinationPath);
        var operationId = Guid.NewGuid();
        db.FileMutationJournals.Add(new FileMutationJournal
        {
            OperationId = operationId,
            Action = FileAction.Move,
            SourcePath = sourcePath,
            DestinationPath = destinationPath,
            SourcePhysicalObjectIdentity = targetIdentity,
            SourceLength = new FileInfo(destinationPath).Length,
            TargetPhysicalObjectIdentity = targetIdentity,
            AudiobookId = audiobook.Id,
            AudiobookFileId = 0,
            State = FileMutationJournalState.Completed
        });
        await db.SaveChangesAsync();

        File.Delete(destinationPath);
        await File.WriteAllTextAsync(destinationPath, "foreign");
        Assert.NotEqual(targetIdentity, GetFileIdentity(destinationPath));
        audiobook.BasePath = "/library/new";
        var store = new FileRenameCommitStore(db, TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CommitOwnerMetadataAsync(audiobook.Id, [operationId]));

        await using var verification = new ListenArrDbContext(options);
        Assert.Equal(
            "/library/old",
            (await verification.Audiobooks.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == audiobook.Id)).BasePath);
        Assert.Equal(
            FileMutationJournalState.Completed,
            (await verification.FileMutationJournals.AsNoTracking()
                .SingleAsync(candidate => candidate.OperationId == operationId)).State);
        Assert.Equal("foreign", await File.ReadAllTextAsync(destinationPath));
    }

    [Fact]
    public async Task CommitOwnerMetadataAsync_NonMoveJournalDoesNotPersistTrackedPathChange()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ListenArrDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var audiobook = new Audiobook
        {
            Title = "Rename Commit Wrong Action",
            BasePath = "/library/old"
        };
        db.Audiobooks.Add(audiobook);
        await db.SaveChangesAsync();
        var operationId = Guid.NewGuid();
        var journal = CreateCompletedJournal(operationId, audiobook.Id);
        journal.Action = FileAction.Copy;
        db.FileMutationJournals.Add(journal);
        await db.SaveChangesAsync();

        audiobook.BasePath = "/library/new";
        var store = new FileRenameCommitStore(db, TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CommitOwnerMetadataAsync(audiobook.Id, [operationId]));

        await using var verification = new ListenArrDbContext(options);
        Assert.Equal(
            "/library/old",
            (await verification.Audiobooks.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == audiobook.Id)).BasePath);
        Assert.Equal(
            FileMutationJournalState.Completed,
            (await verification.FileMutationJournals.AsNoTracking()
                .SingleAsync(candidate => candidate.OperationId == operationId)).State);
    }

    [Fact]
    public async Task CommitOwnerMetadataAsync_MissingJournalDoesNotPersistTrackedPathChange()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ListenArrDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var audiobook = new Audiobook
        {
            Title = "Rename Commit Missing Journal",
            BasePath = "/library/old"
        };
        db.Audiobooks.Add(audiobook);
        await db.SaveChangesAsync();

        audiobook.BasePath = "/library/new";
        var store = new FileRenameCommitStore(db, TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CommitOwnerMetadataAsync(
                audiobook.Id,
                [Guid.NewGuid()]));

        await using var verification = new ListenArrDbContext(options);
        Assert.Equal(
            "/library/old",
            (await verification.Audiobooks.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == audiobook.Id)).BasePath);
    }

    [Fact]
    public async Task CommitOwnerMetadataAsync_VerifiedBatch_CommitsOwnerAndJournalTogether()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ListenArrDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var rootPath = FileService.GetTempDirectory("verified-rename-commit");
        var sourcePath = Path.Join(rootPath, "old.m4b");
        var destinationPath = Path.Join(rootPath, "new.m4b");
        await File.WriteAllTextAsync(sourcePath, "verified-owner-commit");
        File.Copy(sourcePath, destinationPath);
        var root = new RootFolder
        {
            Name = "Verified Commit Root",
            Path = rootPath,
            StorageContractRevision = 7
        };
        var audiobook = new Audiobook
        {
            Title = "Verified Commit",
            BasePath = rootPath,
            FilePath = sourcePath
        };
        db.RootFolders.Add(root);
        db.Audiobooks.Add(audiobook);
        await db.SaveChangesAsync();

        var operationId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var manifest = VerifiedFileRenameBatchManifest.Create(
        [
            new VerifiedFileRenameBatchMember(0, sourcePath, destinationPath)
        ]);
        var bytes = await File.ReadAllBytesAsync(sourcePath);
        db.VerifiedFileRenameJournals.Add(new VerifiedFileRenameJournal
        {
            OperationId = operationId,
            BatchId = batchId,
            AudiobookId = audiobook.Id,
            AudiobookFileId = 0,
            ExpectedBatchMemberCount = manifest.ExpectedMemberCount,
            ExpectedBatchManifestSha256 = manifest.ManifestSha256,
            SourcePath = sourcePath,
            DestinationPath = destinationPath,
            StagingPath = Path.Join(rootPath, ".listenarr-organize-test.partial"),
            RetirementPath = Path.Join(
                rootPath,
                ".listenarr-organize-" + operationId.ToString("N") + ".source"),
            SourceLength = bytes.LongLength,
            SourceSha256 = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(bytes)),
            SourceRootFolderId = root.Id,
            SourceStorageContractRevision = root.StorageContractRevision,
            DestinationRootFolderId = root.Id,
            DestinationStorageContractRevision = root.StorageContractRevision,
            State = VerifiedFileRenameState.TargetVerified
        });
        await db.SaveChangesAsync();

        audiobook.FilePath = destinationPath;
        var store = new FileRenameCommitStore(db, TimeProvider.System);
        await store.CommitOwnerMetadataAsync(audiobook.Id, [operationId]);

        await using var verification = new ListenArrDbContext(options);
        Assert.Equal(
            destinationPath,
            (await verification.Audiobooks.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == audiobook.Id)).FilePath);
        Assert.Equal(
            VerifiedFileRenameState.OwnerMetadataReconciled,
            (await verification.VerifiedFileRenameJournals.AsNoTracking()
                .SingleAsync(candidate => candidate.OperationId == operationId)).State);
        Assert.True(File.Exists(sourcePath));
        Assert.True(File.Exists(destinationPath));
    }

    [Fact]
    public async Task CommitOwnerMetadataAsync_IncompleteVerifiedBatch_RollsBackOwnerChange()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ListenArrDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var rootPath = FileService.GetTempDirectory("verified-rename-incomplete-commit");
        var sourcePath = Path.Join(rootPath, "old.m4b");
        var destinationPath = Path.Join(rootPath, "new.m4b");
        var missingSource = Path.Join(rootPath, "part2-old.m4b");
        var missingDestination = Path.Join(rootPath, "part2-new.m4b");
        await File.WriteAllTextAsync(sourcePath, "verified-owner-incomplete");
        File.Copy(sourcePath, destinationPath);
        var root = new RootFolder
        {
            Name = "Verified Incomplete Root",
            Path = rootPath,
            StorageContractRevision = 4
        };
        var audiobook = new Audiobook
        {
            Title = "Verified Incomplete Commit",
            BasePath = rootPath,
            FilePath = sourcePath
        };
        db.RootFolders.Add(root);
        db.Audiobooks.Add(audiobook);
        await db.SaveChangesAsync();

        var operationId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var manifest = VerifiedFileRenameBatchManifest.Create(
        [
            new VerifiedFileRenameBatchMember(0, sourcePath, destinationPath),
            new VerifiedFileRenameBatchMember(2, missingSource, missingDestination)
        ]);
        var bytes = await File.ReadAllBytesAsync(sourcePath);
        db.VerifiedFileRenameJournals.Add(new VerifiedFileRenameJournal
        {
            OperationId = operationId,
            BatchId = batchId,
            AudiobookId = audiobook.Id,
            AudiobookFileId = 0,
            ExpectedBatchMemberCount = manifest.ExpectedMemberCount,
            ExpectedBatchManifestSha256 = manifest.ManifestSha256,
            SourcePath = sourcePath,
            DestinationPath = destinationPath,
            StagingPath = Path.Join(rootPath, ".listenarr-organize-incomplete.partial"),
            RetirementPath = Path.Join(
                rootPath,
                ".listenarr-organize-" + operationId.ToString("N") + ".source"),
            SourceLength = bytes.LongLength,
            SourceSha256 = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(bytes)),
            SourceRootFolderId = root.Id,
            SourceStorageContractRevision = root.StorageContractRevision,
            DestinationRootFolderId = root.Id,
            DestinationStorageContractRevision = root.StorageContractRevision,
            State = VerifiedFileRenameState.TargetVerified
        });
        await db.SaveChangesAsync();

        audiobook.FilePath = destinationPath;
        var store = new FileRenameCommitStore(db, TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CommitOwnerMetadataAsync(audiobook.Id, [operationId]));

        await using var verification = new ListenArrDbContext(options);
        Assert.Equal(
            sourcePath,
            (await verification.Audiobooks.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == audiobook.Id)).FilePath);
        Assert.Equal(
            VerifiedFileRenameState.TargetVerified,
            (await verification.VerifiedFileRenameJournals.AsNoTracking()
                .SingleAsync(candidate => candidate.OperationId == operationId)).State);
    }

    [Fact]
    public async Task CommitOwnerMetadataAsync_VerifiedRollbackFromSeparateContext_RefreshesTrackedJournalState()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new ListenArrDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var rootPath = FileService.GetTempDirectory("verified-rename-refresh-rollback");
        var sourcePath = Path.Join(rootPath, "old.m4b");
        var destinationPath = Path.Join(rootPath, "new.m4b");
        await File.WriteAllTextAsync(sourcePath, "verified-refresh-rollback");
        File.Copy(sourcePath, destinationPath);
        var root = new RootFolder
        {
            Name = "Verified Refresh Root",
            Path = rootPath,
            StorageContractRevision = 9
        };
        var audiobook = new Audiobook
        {
            Title = "Verified Refresh",
            BasePath = rootPath,
            FilePath = sourcePath
        };
        db.RootFolders.Add(root);
        db.Audiobooks.Add(audiobook);
        await db.SaveChangesAsync();

        var operationId = Guid.NewGuid();
        var batchId = Guid.NewGuid();
        var manifest = VerifiedFileRenameBatchManifest.Create(
        [
            new VerifiedFileRenameBatchMember(0, sourcePath, destinationPath)
        ]);
        var bytes = await File.ReadAllBytesAsync(sourcePath);
        db.VerifiedFileRenameJournals.Add(new VerifiedFileRenameJournal
        {
            OperationId = operationId,
            BatchId = batchId,
            AudiobookId = audiobook.Id,
            AudiobookFileId = 0,
            ExpectedBatchMemberCount = manifest.ExpectedMemberCount,
            ExpectedBatchManifestSha256 = manifest.ManifestSha256,
            SourcePath = sourcePath,
            DestinationPath = destinationPath,
            StagingPath = Path.Join(rootPath, ".listenarr-organize-refresh.partial"),
            RetirementPath = Path.Join(
                rootPath,
                ".listenarr-organize-" + operationId.ToString("N") + ".source"),
            SourceLength = bytes.LongLength,
            SourceSha256 = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(bytes)),
            SourceRootFolderId = root.Id,
            SourceStorageContractRevision = root.StorageContractRevision,
            DestinationRootFolderId = root.Id,
            DestinationStorageContractRevision = root.StorageContractRevision,
            State = VerifiedFileRenameState.TargetVerified
        });
        await db.SaveChangesAsync();

        // Simulate the live verified lease rolling the filesystem/journal back
        // through its independent DbContext after this scoped commit context has
        // already tracked the pre-rollback journal state.
        await using (var rollbackDb = new ListenArrDbContext(options))
        {
            var rolledBack = await rollbackDb.VerifiedFileRenameJournals
                .SingleAsync(candidate => candidate.OperationId == operationId);
            rolledBack.State = VerifiedFileRenameState.RolledBack;
            await rollbackDb.SaveChangesAsync();
        }

        var store = new FileRenameCommitStore(db, TimeProvider.System);
        await store.CommitOwnerMetadataAsync(audiobook.Id, [operationId]);

        await using var verification = new ListenArrDbContext(options);
        Assert.Equal(
            VerifiedFileRenameState.RolledBack,
            (await verification.VerifiedFileRenameJournals.AsNoTracking()
                .SingleAsync(candidate => candidate.OperationId == operationId)).State);
        Assert.Equal(
            sourcePath,
            (await verification.Audiobooks.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == audiobook.Id)).FilePath);
    }

    private static string GetFileIdentity(string path)
    {
        using var lease = PinnedAudiobookFileRegistrationLease.Open(path);
        return lease.PhysicalObjectIdentity;
    }

    private static FileMutationJournal CreateCompletedJournal(
        Guid operationId,
        int audiobookId) =>
        new()
        {
            OperationId = operationId,
            Action = FileAction.Move,
            SourcePath = "/library/old/book.m4b",
            DestinationPath = "/library/new/book.m4b",
            SourcePhysicalObjectIdentity = "source-generation",
            SourceLength = 1,
            TargetPhysicalObjectIdentity = "source-generation",
            AudiobookId = audiobookId,
            AudiobookFileId = 0,
            State = FileMutationJournalState.Completed
        };
}
