using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Listenarr.Infrastructure.Library.Files;
using Listenarr.Infrastructure.Persistence.Repositories;

using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Persistence;

[Trait("Name", "AudiobookFileIdentityReconcilerTests")]
[Trait("Category", "Infrastructure")]
public sealed class AudiobookFileIdentityReconcilerTests : BaseTests
{
    [Fact]
    public async Task ReconcileAsync_DuplicatesUnavailableAndReplay_AreFailClosedAndIdempotent()
    {
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using (var setup = new ListenArrDbContext(options))
        {
            setup.Audiobooks.AddRange(
                BuildAudiobook(1, "/library/shared", "book.m4b"),
                BuildAudiobook(2, "/library/shared", "book.m4b"),
                BuildAudiobook(3, "/library/unique", "unique.m4b"),
                BuildAudiobook(4, "/offline/library", "offline.m4b"));
            await setup.SaveChangesAsync();
        }

        var identityResolver = new Mock<IAudiobookFilePathIdentityResolver>(MockBehavior.Strict);
        identityResolver.Setup(resolver => resolver.ResolveAsync(
                It.IsAny<Audiobook>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns<Audiobook, string, CancellationToken>((audiobook, path, _) =>
            {
                var semantics = new FileSystemPathSemantics(FileSystemPathSyntax.Unix, FileSystemCaseSensitivity.Sensitive);
                Assert.True(FileSystemPathIdentity.TryResolveRelativePathWithinBase(audiobook.BasePath!, path, semantics, out var absolutePath));
                return ValueTask.FromResult(absolutePath.StartsWith("/offline", StringComparison.Ordinal)
                    ? AudiobookFilePathIdentity.CreateUnavailable(absolutePath, FileSystemPathSyntax.Unix, FileSystemCaseSensitivityMode.Auto, audiobook.BasePath!, "Filesystem unavailable.")
                    : AudiobookFilePathIdentity.CreateValid(absolutePath, semantics, FileSystemCaseSensitivityMode.Sensitive, audiobook.BasePath!));
            });
        var reconciler = new AudiobookFileIdentityReconciler(new TestDbContextFactory(options), identityResolver.Object, NullLogger<AudiobookFileIdentityReconciler>.Instance);

        var first = await reconciler.ReconcileAsync();
        var firstState = await ReadStateAsync(options);
        var second = await reconciler.ReconcileAsync();
        var secondState = await ReadStateAsync(options);

        Assert.Equal(new AudiobookFileIdentityReconciliationResult(4, 1, 2, 1), first);
        Assert.Equal(first, second);
        Assert.Equal(firstState, secondState);
        Assert.All(firstState.Where(file => file.AudiobookId is 1 or 2), file => Assert.Equal(PathIdentityState.Conflict, file.State));
        Assert.Equal(PathIdentityState.Valid, Assert.Single(firstState, file => file.AudiobookId == 3).State);
        Assert.Equal(PathIdentityState.Unavailable, Assert.Single(firstState, file => file.AudiobookId == 4).State);
    }

    [Fact]
    public async Task ReconcileAsync_LegacyBackfillAndChangedKnownGeneration_FailClosedWithoutAdoptingReplacement()
    {
        var root = FileService.GetTempDirectory("audiobook-file-identity-physical-backfill");
        var legacyPath = await FileService.GetFileAsync(root, "legacy.m4b", "legacy");
        var knownPath = await FileService.GetFileAsync(root, "known.m4b", "known");
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        const string knownPhysicalIdentity = "persisted-known-generation";
        await using (var setup = new ListenArrDbContext(options))
        {
            var legacy = BuildAudiobook(20, root, Path.GetFileName(legacyPath));
            var known = BuildAudiobook(21, root, Path.GetFileName(knownPath));
            known.Files[0].ApplyPhysicalObjectIdentity(
                knownPhysicalIdentity,
                DateTime.UtcNow);
            setup.Audiobooks.AddRange(legacy, known);
            await setup.SaveChangesAsync();
        }

        var identityResolver = new Mock<IAudiobookFilePathIdentityResolver>(MockBehavior.Strict);
        identityResolver.Setup(resolver => resolver.ResolveAsync(
                It.IsAny<Audiobook>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns<Audiobook, string, CancellationToken>((audiobook, path, _) =>
            {
                var semantics = FileSystemPathSemantics.CurrentHostDefault;
                Assert.True(FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                    audiobook.BasePath!,
                    path,
                    semantics,
                    out var absolutePath));
                return ValueTask.FromResult(AudiobookFilePathIdentity.CreateValid(
                    absolutePath,
                    semantics,
                    FileSystemCaseSensitivityMode.Auto,
                    audiobook.BasePath!));
            });
        var reconciler = new AudiobookFileIdentityReconciler(
            new TestDbContextFactory(options),
            identityResolver.Object,
            NullLogger<AudiobookFileIdentityReconciler>.Instance);

        var result = await reconciler.ReconcileAsync();

        Assert.Equal(
            new AudiobookFileIdentityReconciliationResult(2, 2, 0, 0),
            result);
        await using var verification = new ListenArrDbContext(options);
        var files = await verification.AudiobookFiles
            .AsNoTracking()
            .OrderBy(file => file.AudiobookId)
            .ToListAsync();
        Assert.False(string.IsNullOrWhiteSpace(files[0].PhysicalObjectIdentity));
        Assert.NotNull(files[0].PhysicalIdentityObservedAtUtc);
        Assert.Equal(PathIdentityState.Valid, files[0].PathIdentityState);
        Assert.Equal(knownPhysicalIdentity, files[1].PhysicalObjectIdentity);
        Assert.Equal(PathIdentityState.Valid, files[1].PathIdentityState);
        Assert.False(string.IsNullOrWhiteSpace(files[1].PathIdentityLookupKey));
        Assert.False(string.IsNullOrWhiteSpace(files[1].PathOwnershipKey));

        var competingIdentity = AudiobookFilePathIdentity.CreateValid(
            knownPath,
            FileSystemPathSemantics.CurrentHostDefault,
            FileSystemCaseSensitivityMode.Auto,
            root);
        var repository = new EfAudiobookFileRepository(verification);
        var ownership = await repository.CheckOwnershipAsync(
            audiobookId: 999,
            fileId: null,
            competingIdentity);
        Assert.Equal(
            AudiobookFileOwnershipCheckOutcome.OwnedByOtherAudiobook,
            ownership.Outcome);
        Assert.Equal(21, ownership.ExistingFile?.AudiobookId);
    }

    [Fact]
    public async Task ReconcileAsync_KnownPhysicalGenerationUnavailable_PreservesPathOwnershipFence()
    {
        var root = FileService.GetTempDirectory(
            "audiobook-file-identity-physical-unavailable");
        var missingPath = Path.Join(root, "missing.m4b");
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        const string knownPhysicalIdentity = "persisted-known-generation";
        await using (var setup = new ListenArrDbContext(options))
        {
            var audiobook = BuildAudiobook(30, root, Path.GetFileName(missingPath));
            audiobook.Files![0].ApplyPhysicalObjectIdentity(
                knownPhysicalIdentity,
                DateTime.UtcNow);
            setup.Audiobooks.Add(audiobook);
            await setup.SaveChangesAsync();
        }

        var identityResolver = new Mock<IAudiobookFilePathIdentityResolver>(MockBehavior.Strict);
        identityResolver.Setup(resolver => resolver.ResolveAsync(
                It.IsAny<Audiobook>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns<Audiobook, string, CancellationToken>((audiobook, path, _) =>
            {
                var semantics = FileSystemPathSemantics.CurrentHostDefault;
                Assert.True(FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                    audiobook.BasePath!,
                    path,
                    semantics,
                    out var absolutePath));
                return ValueTask.FromResult(AudiobookFilePathIdentity.CreateValid(
                    absolutePath,
                    semantics,
                    FileSystemCaseSensitivityMode.Auto,
                    audiobook.BasePath!));
            });
        var reconciler = new AudiobookFileIdentityReconciler(
            new TestDbContextFactory(options),
            identityResolver.Object,
            NullLogger<AudiobookFileIdentityReconciler>.Instance);

        var result = await reconciler.ReconcileAsync();

        Assert.Equal(
            new AudiobookFileIdentityReconciliationResult(1, 1, 0, 0),
            result);
        await using var verification = new ListenArrDbContext(options);
        var file = await verification.AudiobookFiles.AsNoTracking().SingleAsync();
        Assert.Equal(PathIdentityState.Valid, file.PathIdentityState);
        Assert.False(string.IsNullOrWhiteSpace(file.PathIdentityLookupKey));
        Assert.False(string.IsNullOrWhiteSpace(file.PathOwnershipKey));
        Assert.Equal(knownPhysicalIdentity, file.PhysicalObjectIdentity);

        var competingIdentity = AudiobookFilePathIdentity.CreateValid(
            missingPath,
            FileSystemPathSemantics.CurrentHostDefault,
            FileSystemCaseSensitivityMode.Auto,
            root);
        var repository = new EfAudiobookFileRepository(verification);
        var ownership = await repository.CheckOwnershipAsync(
            audiobookId: 999,
            fileId: null,
            competingIdentity);
        Assert.Equal(
            AudiobookFileOwnershipCheckOutcome.OwnedByOtherAudiobook,
            ownership.Outcome);
        Assert.Equal(30, ownership.ExistingFile?.AudiobookId);
    }

    [Fact]
    public async Task ReconcileAsync_KnownMatchingPhysicalGeneration_RemainsValid()
    {
        var root = FileService.GetTempDirectory("audiobook-file-identity-known-match");
        var knownPath = await FileService.GetFileAsync(root, "known.m4b", "known");
        string knownPhysicalIdentity;
        using (var lease = PinnedAudiobookFileRegistrationLease.Open(knownPath))
        {
            knownPhysicalIdentity = lease.PhysicalObjectIdentity;
        }
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using (var setup = new ListenArrDbContext(options))
        {
            var known = BuildAudiobook(22, root, Path.GetFileName(knownPath));
            known.Files![0].ApplyPhysicalObjectIdentity(
                knownPhysicalIdentity,
                DateTime.UtcNow);
            setup.Audiobooks.Add(known);
            await setup.SaveChangesAsync();
        }

        var identityResolver = new Mock<IAudiobookFilePathIdentityResolver>(MockBehavior.Strict);
        identityResolver.Setup(resolver => resolver.ResolveAsync(
                It.IsAny<Audiobook>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns<Audiobook, string, CancellationToken>((audiobook, path, _) =>
            {
                var semantics = FileSystemPathSemantics.CurrentHostDefault;
                Assert.True(FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                    audiobook.BasePath!,
                    path,
                    semantics,
                    out var absolutePath));
                return ValueTask.FromResult(AudiobookFilePathIdentity.CreateValid(
                    absolutePath,
                    semantics,
                    FileSystemCaseSensitivityMode.Auto,
                    audiobook.BasePath!));
            });
        var reconciler = new AudiobookFileIdentityReconciler(
            new TestDbContextFactory(options),
            identityResolver.Object,
            NullLogger<AudiobookFileIdentityReconciler>.Instance);

        var result = await reconciler.ReconcileAsync();

        Assert.Equal(new AudiobookFileIdentityReconciliationResult(1, 1, 0, 0), result);
        await using var verification = new ListenArrDbContext(options);
        var file = await verification.AudiobookFiles.AsNoTracking().SingleAsync();
        Assert.Equal(PathIdentityState.Valid, file.PathIdentityState);
        Assert.Equal(knownPhysicalIdentity, file.PhysicalObjectIdentity);
    }

    [LinuxFact]
    public async Task ReconcileAsync_ParentGenerationReplacedAfterPin_DoesNotValidateOldChildAgainstNewPath()
    {
        var container = FileService.GetTempDirectory(
            "audiobook-file-identity-parent-replacement");
        var root = Path.Join(container, "Book");
        var displacedRoot = Path.Join(container, "Book.original");
        Directory.CreateDirectory(root);
        var filePath = await FileService.GetFileAsync(root, "book.m4b", "owned");
        string originalPhysicalIdentity;
        using (var lease = PinnedAudiobookFileRegistrationLease.Open(filePath))
        {
            originalPhysicalIdentity = lease.PhysicalObjectIdentity;
        }
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using (var setup = new ListenArrDbContext(options))
        {
            var audiobook = BuildAudiobook(23, root, Path.GetFileName(filePath));
            audiobook.Files![0].ApplyPhysicalObjectIdentity(
                originalPhysicalIdentity,
                DateTime.UtcNow);
            setup.Audiobooks.Add(audiobook);
            await setup.SaveChangesAsync();
        }

        var identityResolver = new Mock<IAudiobookFilePathIdentityResolver>(MockBehavior.Strict);
        identityResolver.Setup(resolver => resolver.ResolveAsync(
                It.IsAny<Audiobook>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns<Audiobook, string, CancellationToken>((audiobook, path, _) =>
            {
                var semantics = FileSystemPathSemantics.CurrentHostDefault;
                Assert.True(FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                    audiobook.BasePath!,
                    path,
                    semantics,
                    out var absolutePath));
                return ValueTask.FromResult(AudiobookFilePathIdentity.CreateValid(
                    absolutePath,
                    semantics,
                    FileSystemCaseSensitivityMode.Auto,
                    audiobook.BasePath!));
            });
        var reconciler = new AudiobookFileIdentityReconciler(
            new TestDbContextFactory(options),
            identityResolver.Object,
            NullLogger<AudiobookFileIdentityReconciler>.Instance);
        var replaced = false;
        reconciler.AfterPhysicalIdentityParentPinnedForTest = _ =>
        {
            if (replaced)
            {
                return;
            }
            replaced = true;
            Directory.Move(root, displacedRoot);
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Join(root, "book.m4b"), "replacement");
        };

        var result = await reconciler.ReconcileAsync();

        Assert.True(replaced);
        Assert.Equal(
            new AudiobookFileIdentityReconciliationResult(1, 1, 0, 0),
            result);
        await using var verification = new ListenArrDbContext(options);
        var file = await verification.AudiobookFiles.AsNoTracking().SingleAsync();
        Assert.Equal(PathIdentityState.Valid, file.PathIdentityState);
        Assert.False(string.IsNullOrWhiteSpace(file.PathIdentityLookupKey));
        Assert.False(string.IsNullOrWhiteSpace(file.PathOwnershipKey));
        Assert.Equal(originalPhysicalIdentity, file.PhysicalObjectIdentity);
        Assert.Equal("owned", await File.ReadAllTextAsync(
            Path.Join(displacedRoot, "book.m4b")));
        Assert.Equal("replacement", await File.ReadAllTextAsync(
            Path.Join(root, "book.m4b")));
    }

    [Fact]
    public async Task ReconcileAsync_ForeignHostPaths_AreUnavailableWithoutPerFileWarning()
    {
        var foreignBasePath = OperatingSystem.IsWindows()
            ? "/server/mnt/drive/Audiobooks/Author/Book"
            : "C:\\Audiobooks\\Author\\Book";
        var foreignFilePath = OperatingSystem.IsWindows()
            ? "Disc 1/Book.m4b"
            : "Disc 1\\Book.m4b";
        var options = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using (var setup = new ListenArrDbContext(options))
        {
            setup.Audiobooks.Add(BuildAudiobook(10, foreignBasePath, foreignFilePath));
            await setup.SaveChangesAsync();
        }

        var roots = new Mock<IRootFolderRepository>(MockBehavior.Strict);
        roots.Setup(repository => repository.GetAllAsync()).ReturnsAsync([]);
        var semantics = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        semantics.Setup(resolver => resolver.ResolveAsync(
                It.IsAny<string>(),
                It.IsAny<FileSystemCaseSensitivityMode>(),
                It.IsAny<CancellationToken>()))
            .Throws(new ArgumentException("Filesystem semantics require an absolute path."));
        var identityResolver = new AudiobookFilePathIdentityResolver(
            roots.Object,
            semantics.Object);
        var logger = new Mock<ILogger<AudiobookFileIdentityReconciler>>();
        var reconciler = new AudiobookFileIdentityReconciler(
            new TestDbContextFactory(options),
            identityResolver,
            logger.Object);

        var result = await reconciler.ReconcileAsync();
        var state = Assert.Single(await ReadStateAsync(options));

        Assert.Equal(new AudiobookFileIdentityReconciliationResult(1, 0, 0, 1), result);
        Assert.Equal(PathIdentityState.Unavailable, state.State);
        Assert.Null(state.OwnershipKey);
        Assert.NotNull(state.LookupKey);
        Assert.Contains("cannot be validated", state.Reason, StringComparison.OrdinalIgnoreCase);
        semantics.VerifyNoOtherCalls();
        Assert.DoesNotContain(
            logger.Invocations,
            invocation => invocation.Arguments.Count > 0
                && invocation.Arguments[0] is LogLevel.Warning);
    }

    private static Audiobook BuildAudiobook(int id, string basePath, string filePath) =>
        new() { Id = id, Title = $"Book {id}", BasePath = basePath, Files = [new AudiobookFile { AudiobookId = id, Path = filePath }] };

    private static async Task<List<FileState>> ReadStateAsync(DbContextOptions<ListenArrDbContext> options)
    {
        await using var context = new ListenArrDbContext(options);
        return await context.AudiobookFiles.AsNoTracking().OrderBy(file => file.AudiobookId)
            .Select(file => new FileState(file.AudiobookId, file.PathIdentityState, file.PathIdentityLookupKey, file.PathOwnershipKey, file.PathIdentityReason))
            .ToListAsync();
    }

    private sealed record FileState(int AudiobookId, PathIdentityState State, string? LookupKey, string? OwnershipKey, string? Reason);

    private sealed class TestDbContextFactory(DbContextOptions<ListenArrDbContext> options) : IDbContextFactory<ListenArrDbContext>
    {
        public ListenArrDbContext CreateDbContext() => new(options);
        public Task<ListenArrDbContext> CreateDbContextAsync() => Task.FromResult(new ListenArrDbContext(options));
    }
}
