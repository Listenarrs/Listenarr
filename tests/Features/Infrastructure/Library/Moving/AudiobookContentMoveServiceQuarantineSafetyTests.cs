using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

public partial class AudiobookContentMoveServiceTests
{
    [Fact]
    public async Task ResumeSourceCleanup_UnmarkedQuarantine_PreservesMatchingFile()
    {
        var source = FileService.GetTempDirectory("content-move-unmarked-quarantine-src");
        var target = FileService.GetTempDirectory("content-move-unmarked-quarantine-dst");
        var jobId = Guid.NewGuid();
        var quarantineRoot = Path.Join(
            Path.GetDirectoryName(source)!,
            $".listenarr-quarantine-{jobId:N}");
        Directory.CreateDirectory(quarantineRoot);
        var destination = await FileService.GetFileAsync(target, "book.m4b", "verified audio");
        var quarantineFile = await FileService.GetFileAsync(
            quarantineRoot,
            "book.m4b",
            "verified audio");
        await PersistQuarantinedEntryAsync(jobId, source, target, "book.m4b", destination);
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();

        await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.ResumeSourceCleanupAsync(
                CreateCleanupRequest(source, target, jobId),
                CreateIncompleteCleanupResult(source, target, jobId),
                CancellationToken.None));

        Assert.True(File.Exists(quarantineFile));
        Assert.Equal("verified audio", await File.ReadAllTextAsync(quarantineFile));
    }

    [Theory]
    [InlineData("job")]
    [InlineData("source")]
    [InlineData("target")]
    [InlineData("version")]
    [InlineData("malformed")]
    public async Task ResumeSourceCleanup_InvalidQuarantineMarker_PreservesMatchingFile(
        string invalidField)
    {
        var source = FileService.GetTempDirectory($"content-move-invalid-quarantine-{invalidField}-src");
        var target = FileService.GetTempDirectory($"content-move-invalid-quarantine-{invalidField}-dst");
        var jobId = Guid.NewGuid();
        var quarantineRoot = Path.Join(
            Path.GetDirectoryName(source)!,
            $".listenarr-quarantine-{jobId:N}");
        Directory.CreateDirectory(quarantineRoot);
        await WriteInvalidQuarantineOwnershipMarkerAsync(
            quarantineRoot,
            invalidField,
            jobId,
            source,
            target);
        var destination = await FileService.GetFileAsync(target, "book.m4b", "verified audio");
        var quarantineFile = await FileService.GetFileAsync(
            quarantineRoot,
            "book.m4b",
            "verified audio");
        await PersistQuarantinedEntryAsync(jobId, source, target, "book.m4b", destination);
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();

        await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.ResumeSourceCleanupAsync(
                CreateCleanupRequest(source, target, jobId),
                CreateIncompleteCleanupResult(source, target, jobId),
                CancellationToken.None));

        Assert.True(File.Exists(quarantineFile));
        Assert.True(File.Exists(Path.Join(
            quarantineRoot,
            ".listenarr-quarantine-owner.json")));
    }

    [LinuxFact]
    public async Task ResumeSourceCleanup_AmbiguousPersistedQuarantineMarkerPath_PreservesMatchingFile()
    {
        var source = FileService.GetTempDirectory("content-move-ambiguous-quarantine-src");
        var target = FileService.GetTempDirectory("content-move-ambiguous-quarantine-dst");
        var jobId = Guid.NewGuid();
        var quarantineRoot = Path.Join(
            Path.GetDirectoryName(source)!,
            $".listenarr-quarantine-{jobId:N}");
        Directory.CreateDirectory(quarantineRoot);
        var ambiguousSource = "/" + Path.GetFullPath(source);
        Assert.False(FileSystemPathIdentity.TryDetectAbsoluteSyntax(
            ambiguousSource,
            out _));
        await File.WriteAllTextAsync(
            Path.Join(quarantineRoot, ".listenarr-quarantine-owner.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                Version = 1,
                ArtifactType = "quarantine-directory",
                JobId = jobId,
                Source = ambiguousSource,
                Target = Path.GetFullPath(target),
                DirectoryPath = Path.GetFullPath(quarantineRoot),
                OwnedArtifactType = (string?)null
            }));
        var destination = await FileService.GetFileAsync(
            target,
            "book.m4b",
            "verified audio");
        var quarantineFile = await FileService.GetFileAsync(
            quarantineRoot,
            "book.m4b",
            "verified audio");
        await PersistQuarantinedEntryAsync(
            jobId,
            source,
            target,
            "book.m4b",
            destination);

        await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            _provider.GetRequiredService<AudiobookContentMoveService>()
                .ResumeSourceCleanupAsync(
                    CreateCleanupRequest(source, target, jobId),
                    CreateIncompleteCleanupResult(source, target, jobId),
                    CancellationToken.None));

        Assert.True(File.Exists(quarantineFile));
        Assert.True(File.Exists(Path.Join(
            quarantineRoot,
            ".listenarr-quarantine-owner.json")));
    }

    [Fact]
    public async Task ResumeSourceCleanup_UnexpectedOwnedQuarantineContent_PreservesOwnershipEvidence()
    {
        var source = FileService.GetTempDirectory("content-move-unexpected-quarantine-src");
        var target = FileService.GetTempDirectory("content-move-unexpected-quarantine-dst");
        var jobId = Guid.NewGuid();
        var quarantineRoot = Path.Join(
            Path.GetDirectoryName(source)!,
            $".listenarr-quarantine-{jobId:N}");
        Directory.CreateDirectory(quarantineRoot);
        await WriteQuarantineOwnershipMarkerAsync(
            quarantineRoot,
            jobId,
            source,
            target);
        var destination = await FileService.GetFileAsync(target, "book.m4b", "verified audio");
        await FileService.GetFileAsync(quarantineRoot, "book.m4b", "verified audio");
        var unexpectedFile = await FileService.GetFileAsync(
            quarantineRoot,
            "operator-note.txt",
            "preserve me");
        await PersistQuarantinedEntryAsync(jobId, source, target, "book.m4b", destination);
        var markerPath = Path.Join(quarantineRoot, ".listenarr-quarantine-owner.json");
        var service = _provider.GetRequiredService<AudiobookContentMoveService>();

        await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
            service.ResumeSourceCleanupAsync(
                CreateCleanupRequest(source, target, jobId),
                CreateIncompleteCleanupResult(source, target, jobId),
                CancellationToken.None));

        Assert.True(File.Exists(markerPath));
        Assert.True(File.Exists(unexpectedFile));
        Assert.Equal("preserve me", await File.ReadAllTextAsync(unexpectedFile));
    }

    [DirectoryLinkFact]
    public async Task ResumeSourceCleanup_LinkedQuarantineRoot_PreservesExternalFile()
    {
        var source = FileService.GetTempDirectory("content-move-linked-quarantine-root-src");
        var target = FileService.GetTempDirectory("content-move-linked-quarantine-root-dst");
        var jobId = Guid.NewGuid();
        var quarantineRoot = Path.Join(
            Path.GetDirectoryName(source)!,
            $".listenarr-quarantine-{jobId:N}");
        var external = FileService.GetTempDirectory("content-move-linked-quarantine-root-external");
        await WriteQuarantineOwnershipMarkerAsync(
            external,
            jobId,
            source,
            target);
        var externalFile = await FileService.GetFileAsync(
            external,
            "book.m4b",
            "verified audio");
        Assert.True(
            TryCreateDirectoryLink(quarantineRoot, external),
            "The required directory link could not be created.");

        try
        {
            var destination = await FileService.GetFileAsync(
                target,
                "book.m4b",
                "verified audio");
            await PersistQuarantinedEntryAsync(
                jobId,
                source,
                target,
                "book.m4b",
                destination);
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();

            await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.ResumeSourceCleanupAsync(
                    CreateCleanupRequest(source, target, jobId),
                    CreateIncompleteCleanupResult(source, target, jobId),
                    CancellationToken.None));

            Assert.True(File.Exists(externalFile));
            Assert.Equal("verified audio", await File.ReadAllTextAsync(externalFile));
        }
        finally
        {
            TryRemoveDirectoryLink(quarantineRoot);
        }
    }

    [DirectoryLinkFact]
    public async Task ResumeSourceCleanup_LinkedQuarantineEntry_PreservesExternalFile()
    {
        var source = FileService.GetTempDirectory("content-move-linked-quarantine-src");
        var target = FileService.GetTempDirectory("content-move-linked-quarantine-dst");
        var jobId = Guid.NewGuid();
        var quarantineRoot = Path.Join(
            Path.GetDirectoryName(source)!,
            $".listenarr-quarantine-{jobId:N}");
        Directory.CreateDirectory(quarantineRoot);
        await WriteQuarantineOwnershipMarkerAsync(
            quarantineRoot,
            jobId,
            source,
            target);
        var external = FileService.GetTempDirectory("content-move-linked-quarantine-external");
        var externalFile = await FileService.GetFileAsync(external, "book.m4b", "verified audio");
        var linkedDirectory = Path.Join(quarantineRoot, "nested");
        Assert.True(
            TryCreateDirectoryLink(linkedDirectory, external),
            "The required directory link could not be created.");

        try
        {
            var destinationDirectory = Path.Join(target, "nested");
            Directory.CreateDirectory(destinationDirectory);
            var destination = await FileService.GetFileAsync(
                destinationDirectory,
                "book.m4b",
                "verified audio");
            await PersistQuarantinedEntryAsync(
                jobId,
                source,
                target,
                Path.Join("nested", "book.m4b"),
                destination);
            var service = _provider.GetRequiredService<AudiobookContentMoveService>();

            await Assert.ThrowsAsync<MoveNeedsAttentionException>(() =>
                service.ResumeSourceCleanupAsync(
                    CreateCleanupRequest(source, target, jobId),
                    CreateIncompleteCleanupResult(source, target, jobId),
                    CancellationToken.None));

            Assert.True(File.Exists(externalFile));
            Assert.Equal("verified audio", await File.ReadAllTextAsync(externalFile));
        }
        finally
        {
            TryRemoveDirectoryLink(linkedDirectory);
        }
    }

    private static Task WriteInvalidQuarantineOwnershipMarkerAsync(
        string quarantineRoot,
        string invalidField,
        Guid jobId,
        string source,
        string target)
    {
        var markerPath = Path.Join(
            quarantineRoot,
            ".listenarr-quarantine-owner.json");
        if (invalidField == "malformed")
        {
            return File.WriteAllTextAsync(markerPath, "{invalid-json");
        }

        var marker = System.Text.Json.JsonSerializer.Serialize(new
        {
            Version = invalidField == "version" ? 2 : 1,
            ArtifactType = "quarantine-directory",
            JobId = invalidField == "job" ? Guid.NewGuid() : jobId,
            Source = invalidField == "source"
                ? Path.Join(Path.GetDirectoryName(source)!, "other-source")
                : Path.GetFullPath(source),
            Target = invalidField == "target"
                ? Path.Join(Path.GetDirectoryName(target)!, "other-target")
                : Path.GetFullPath(target),
            DirectoryPath = Path.GetFullPath(quarantineRoot),
            OwnedArtifactType = (string?)null
        });
        return File.WriteAllTextAsync(markerPath, marker);
    }

    private async Task PersistQuarantinedEntryAsync(
        Guid jobId,
        string source,
        string target,
        string relativePath,
        string destination)
    {
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                await File.ReadAllBytesAsync(destination)));
        var factory = _provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        db.MoveJobs.Add(new MoveJob
        {
            Id = jobId,
            AudiobookId = 1,
            RequestedPath = target,
            SourcePath = source,
            Status = MoveJobStatus.Running,
            LeaseOwner = TestLeaseOwner,
            LeaseGeneration = 1,
            LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
            ActiveDeduplicationKey = $"test:{jobId:N}"
        });
        db.MoveJobEntries.Add(new MoveJobEntry
        {
            MoveJobId = jobId,
            RelativePath = relativePath,
            EntryType = MoveJobEntryType.File,
            Length = new FileInfo(destination).Length,
            Sha256 = hash,
            CopyState = MoveJobEntryCopyState.Verified,
            CleanupState = MoveJobEntryCleanupState.Quarantined
        });
        await db.SaveChangesAsync();
        await AuthorizeExistingMoveJobTargetAsync(jobId, target);
    }

    private static AudiobookContentMoveRequest CreateCleanupRequest(
        string source,
        string target,
        Guid jobId) =>
        new(
            source,
            target,
            jobId,
            true,
            FileSystemPathSemantics.CurrentHostDefault,
            FileSystemPathSemantics.CurrentHostDefault,
            LeaseToken(1));

    private static AudiobookContentMoveResult CreateIncompleteCleanupResult(
        string source,
        string target,
        Guid jobId) =>
        new(
            source,
            target,
            false,
            false,
            Path.Join(target, $".listenarr-move-{jobId:N}.pending"),
            false,
            new Dictionary<string, string>());
}
