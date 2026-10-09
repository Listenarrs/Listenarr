namespace Listenarr.Tests.Builders;

internal sealed class ReleasedStorageRecoveryScenarioBuilder(
    string rootPath,
    int protocol,
    MoveJobStatus jobState,
    LibraryDirectoryOwnershipState ownershipState)
{
    public ReleasedStorageRecoveryScenario Build()
    {
        var semantics = FileSystemPathSemantics.CurrentHostDefault;
        var source = Path.Join(rootPath, "source");
        var target = Path.Join(rootPath, "target");
        var root = new RootFolderBuilder().WithPath(rootPath)
            .WithName("Released storage root").Build();
        root.ResolvedCaseSensitivity = semantics.CaseSensitivity;
        root.PathIdentityState = PathIdentityState.Valid;
        root.PathIdentityKey = FileSystemPathIdentity.CreateKey("root", rootPath, semantics);
        root.DirectoryObjectIdentityVersion = ManagedDirectoryIdentity.CurrentVersion;
        root.DirectoryObjectIdentity = "released-other-client-root";
        var book = new AudiobookBuilder().WithTitle("Released move owner")
            .WithBasePath(source).WithFilePath(Path.Join(source, "book.m4b")).Build();
        var file = new AudiobookFileBuilder().WithAudiobook(book)
            .WithPath(book.FilePath!).WithSize(5).Build();
        file.ApplyPathIdentity(book.FilePath!, AudiobookFilePathIdentity.CreateValid(
            book.FilePath!, semantics, FileSystemCaseSensitivityMode.Auto, rootPath));
        book.Files = [file];
        var ownership = new LibraryDirectoryOwnership
        {
            Path = source,
            CanonicalPath = source,
            PathSyntax = semantics.Syntax,
            PathCaseSensitivity = semantics.CaseSensitivity,
            PathCaseSensitivityMode = FileSystemCaseSensitivityMode.Auto,
            PathIdentityBoundary = rootPath,
            PathIdentityLookupKey = FileSystemPathIdentity.CreateLookupKey(
                "library-directory", source, semantics.Syntax),
            PathOwnershipKey = FileSystemPathIdentity.CreateKey(
                "library-directory", source, semantics),
            OwnershipToken = Guid.NewGuid().ToString("N"),
            CreationWorkflow = "Move",
            AudiobookId = book.Id,
            ManagedRootFolderId = root.Id,
            State = ownershipState,
            DirectoryObjectIdentityVersion = ManagedDirectoryIdentity.CurrentVersion,
            DirectoryObjectIdentity = "released-other-client-directory"
        };
        var job = new MoveJob
        {
            AudiobookId = book.Id,
            SourcePath = source,
            RequestedPath = target,
            Status = jobState,
            Phase = MoveJobPhase.CleaningSource,
            ExecutionProtocolVersion = protocol,
            SourceDirectoryObjectIdentity = "released-other-client-source",
            TargetDirectoryObjectIdentity = "released-other-client-target",
            ActiveDeduplicationKey = jobState.IsActive() ? Guid.NewGuid().ToString("N") : null,
            Entries =
            [
                new MoveJobEntry
                {
                    RelativePath = "book.m4b",
                    EntryType = MoveJobEntryType.File,
                    Length = 5,
                    Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes("audio"))),
                    CopyState = MoveJobEntryCopyState.Verified,
                    CleanupState = MoveJobEntryCleanupState.DeleteAuthorized,
                    SourcePhysicalObjectIdentity = "released-source-file",
                    TargetPhysicalObjectIdentity = "released-target-file"
                }
            ]
        };
        var identity = new PathIdentitySnapshot(semantics.Syntax, semantics.CaseSensitivity,
            FileSystemCaseSensitivityMode.Auto, rootPath);
        job.SetSourceIdentity(identity);
        job.SetTargetIdentity(identity);
        return new ReleasedStorageRecoveryScenario(book, root, ownership, job);
    }
}

internal sealed record ReleasedStorageRecoveryScenario(
    Audiobook Audiobook,
    RootFolder Root,
    LibraryDirectoryOwnership Ownership,
    MoveJob Job);
