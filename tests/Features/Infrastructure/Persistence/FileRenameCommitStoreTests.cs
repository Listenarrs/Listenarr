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
