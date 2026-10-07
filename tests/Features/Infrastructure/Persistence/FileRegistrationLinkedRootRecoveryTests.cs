using System.Security.Cryptography;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

[Trait("Name", "FileRegistrationLinkedRootRecoveryTests")]
[Trait("Category", "Infrastructure")]
public sealed class FileRegistrationLinkedRootRecoveryTests : BaseTests
{
    [DirectoryLinkTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ReconcileAsync_LinkedConfiguredBoundary_VerifiesOnlyAuthorizedDescendants(
        bool linkedDescendant, bool unavailableNestedRoot)
    {
        var directory = FileService.GetTempDirectory("registration-linked-root-restart");
        var physical = Directory.CreateDirectory(Path.Join(directory, "physical")).FullName;
        var linked = Path.Join(directory, "library");
        Directory.CreateSymbolicLink(linked, physical);
        await AddAuthorizedRootAsync(linked, "Linked registration target");
        var parent = Path.Join(linked, "Book");
        if (linkedDescendant)
        {
            var foreign = Directory.CreateDirectory(Path.Join(directory, "foreign")).FullName;
            Directory.CreateSymbolicLink(parent, foreign);
        }
        else Directory.CreateDirectory(parent);
        var source = Path.Join(directory, "source.m4b");
        var destination = Path.Join(parent, "book.m4b");
        await File.WriteAllTextAsync(source, "audio");
        await File.WriteAllTextAsync(destination, "audio");
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            if (unavailableNestedRoot)
                db.RootFolders.Add(new RootFolder
                {
                    Name = "Unavailable nested boundary",
                    Path = parent,
                    CaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
                    PathIdentityState = PathIdentityState.Unavailable,
                    ResolvedCaseSensitivity = FileSystemCaseSensitivity.Unknown
                });
            var audiobook = new Audiobook { Title = "Linked restart", BasePath = parent };
            db.Audiobooks.Add(audiobook);
            await db.SaveChangesAsync();
            var file = AudiobookFile.CreateUnresolved(destination);
            file.AudiobookId = audiobook.Id;
            file.ApplyPathIdentity(destination, AudiobookFilePathIdentity.CreateValid(
                destination, FileSystemPathSemantics.CurrentHostDefault,
                FileSystemCaseSensitivityMode.Auto, linked));
            db.AudiobookFiles.Add(file);
            db.FileMutationJournals.Add(new FileMutationJournal
            {
                OperationId = Guid.NewGuid(),
                ProtocolVersion = FileMutationProtocol.Current,
                Action = FileAction.Move,
                SourcePath = source,
                DestinationPath = destination,
                SourceLength = 5,
                SourceSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source))),
                State = FileMutationJournalState.RegistrationCommitted,
                AudiobookId = audiobook.Id
            });
            await db.SaveChangesAsync();
        }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var recovery = new FileRegistrationRecoveryService(factory,
                new FileMover(NullLogger<FileMover>.Instance, dbContextFactory: factory),
                TimeProvider.System, NullLogger<FileRegistrationRecoveryService>.Instance);
            await recovery.ReconcileAsync();
            await using var db = await factory.CreateDbContextAsync();
            var journal = await db.FileMutationJournals.AsNoTracking().SingleAsync();
            Assert.Equal(unavailableNestedRoot ? FileMutationJournalState.RegistrationCommitted
                : linkedDescendant ? FileMutationJournalState.NeedsAttention
                : FileMutationJournalState.CompletedSourceRetained, journal.State);
            Assert.Equal(destination, (await db.AudiobookFiles.AsNoTracking().SingleAsync()).Path);
            Assert.Equal("audio", await File.ReadAllTextAsync(source));
            Assert.Equal("audio", await File.ReadAllTextAsync(destination));
        }
    }
}
