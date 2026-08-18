using Listenarr.Infrastructure.Library.Files;

using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Files;

[Trait("Name", "AudiobookFilePathIdentityResolverTests")]
[Trait("Category", "Infrastructure")]
public sealed class AudiobookFilePathIdentityResolverTests : BaseTests
{
    [WindowsFact]
    public async Task ResolveAsync_WindowsInsensitiveVariants_CreateSameOwnershipIdentity()
    {
        var resolver = BuildResolver(new RootFolder { Path = "C:\\Library", CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive, ResolvedCaseSensitivity = FileSystemCaseSensitivity.Insensitive, PathIdentityState = PathIdentityState.Valid });
        var audiobook = new Audiobook { BasePath = "C:\\Library\\Author\\Book" };
        var first = await resolver.ResolveAsync(audiobook, "C:\\Library\\Author\\Book\\Disc 1\\Book.m4b");
        var second = await resolver.ResolveAsync(audiobook, "c:/LIBRARY/Author/Book/Disc 1/BOOK.m4b");
        Assert.Equal(PathIdentityState.Valid, first.State);
        Assert.Equal(first.OwnershipKey, second.OwnershipKey);
        Assert.Equal(first.LookupKey, second.LookupKey);
        Assert.Equal(FileSystemPathSyntax.Windows, first.Syntax);
        Assert.Equal(FileSystemCaseSensitivity.Insensitive, first.CaseSensitivity);
        Assert.Equal("C:\\Library", first.BoundaryPath);
    }

    [WindowsFact]
    public async Task ResolveAsync_DeviceAliasConfiguredRoot_DoesNotMintDirectOwnershipIdentity()
    {
        var physicalRoot = FileService.GetTempDirectory(
            "identity-device-alias-root");
        var filePath = Path.Join(
            physicalRoot,
            "Author",
            "Book",
            "book.m4b");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "audio");
        var root = new RootFolder
        {
            Path = @"\\?\" + physicalRoot,
            CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive,
            ResolvedCaseSensitivity = FileSystemCaseSensitivity.Insensitive,
            PathIdentityState = PathIdentityState.Unavailable
        };
        var roots = new Mock<IRootFolderRepository>(MockBehavior.Strict);
        roots.Setup(repository => repository.GetAllAsync()).ReturnsAsync([root]);
        var semantics = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        var resolver = new AudiobookFilePathIdentityResolver(
            roots.Object,
            semantics.Object);

        var identity = await resolver.ResolveAsync(
            new Audiobook { BasePath = Path.GetDirectoryName(filePath) },
            filePath);

        Assert.Equal(PathIdentityState.Unavailable, identity.State);
        Assert.Null(identity.OwnershipKey);
        Assert.NotNull(identity.LookupKey);
        Assert.Contains("configured root", identity.Reason, StringComparison.OrdinalIgnoreCase);
        semantics.VerifyNoOtherCalls();
    }

    [LinuxFact]
    public async Task ResolveAsync_UnixSensitiveCaseVariants_CreateDifferentOwnershipIdentities()
    {
        var resolver = BuildResolver(new RootFolder { Path = "/library", CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive, ResolvedCaseSensitivity = FileSystemCaseSensitivity.Sensitive, PathIdentityState = PathIdentityState.Valid });
        var audiobook = new Audiobook { BasePath = "/library/author/book" };
        var upper = await resolver.ResolveAsync(audiobook, "/library/author/book/Book.m4b");
        var lower = await resolver.ResolveAsync(audiobook, "/library/author/book/book.m4b");
        Assert.NotEqual(upper.OwnershipKey, lower.OwnershipKey);
        Assert.Equal(upper.LookupKey, lower.LookupKey);
    }

    [LinuxFact]
    public async Task ResolveAsync_UnixConfiguredInsensitiveCaseVariants_CreateSameOwnershipIdentity()
    {
        var resolver = BuildResolver(new RootFolder { Path = "/library", CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive, ResolvedCaseSensitivity = FileSystemCaseSensitivity.Insensitive, PathIdentityState = PathIdentityState.Valid });
        var audiobook = new Audiobook { BasePath = "/library/author/book" };
        var upper = await resolver.ResolveAsync(audiobook, "/library/author/book/Book.m4b");
        var lower = await resolver.ResolveAsync(audiobook, "/library/author/book/book.m4b");
        Assert.Equal(upper.OwnershipKey, lower.OwnershipKey);
    }

    [WindowsFact]
    public async Task ResolveAsync_UncVariants_CreateSameOwnershipIdentity()
    {
        var resolver = BuildResolver(new RootFolder { Path = "\\\\server\\share", CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive, ResolvedCaseSensitivity = FileSystemCaseSensitivity.Insensitive, PathIdentityState = PathIdentityState.Valid });
        var audiobook = new Audiobook { BasePath = "\\\\server\\share\\Author\\Book" };
        var first = await resolver.ResolveAsync(audiobook, "\\\\server\\share\\Author\\Book\\Book.m4b");
        var second = await resolver.ResolveAsync(audiobook, "\\\\SERVER\\SHARE\\Author\\Book\\BOOK.m4b");
        Assert.Equal(first.OwnershipKey, second.OwnershipKey);
    }

    [Fact]
    public async Task ResolveAsync_AmbiguousDoubleSlashBase_DoesNotBorrowNativeFilesystemContext()
    {
        var roots = new Mock<IRootFolderRepository>(MockBehavior.Strict);
        var semantics = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        var resolver = new AudiobookFilePathIdentityResolver(
            roots.Object,
            semantics.Object);
        var audiobook = new Audiobook
        {
            BasePath = "//server/share/Author/Book"
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(audiobook, "Disc 1/Book.m4b").AsTask());

        Assert.Contains(
            "authoritative absolute audiobook base path",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        roots.VerifyNoOtherCalls();
        semantics.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ResolveAsync_ForeignAbsolutePath_ReturnsUnavailableWithoutNativeProbe()
    {
        var foreign = GetForeignPathFixture();
        var roots = new Mock<IRootFolderRepository>(MockBehavior.Strict);
        roots.Setup(repository => repository.GetAllAsync()).ReturnsAsync([]);
        var semantics = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        var resolver = new AudiobookFilePathIdentityResolver(roots.Object, semantics.Object);

        var identity = await resolver.ResolveAsync(
            new Audiobook { BasePath = foreign.BasePath },
            foreign.AbsoluteFilePath);

        Assert.Equal(PathIdentityState.Unavailable, identity.State);
        Assert.Equal(foreign.Syntax, identity.Syntax);
        Assert.Equal(
            FileSystemPathIdentity.Canonicalize(foreign.AbsoluteFilePath, foreign.Syntax),
            identity.CanonicalPath);
        Assert.Equal(FileSystemCaseSensitivity.Unknown, identity.CaseSensitivity);
        Assert.Null(identity.OwnershipKey);
        Assert.NotNull(identity.LookupKey);
        Assert.Contains("cannot be validated", identity.Reason, StringComparison.OrdinalIgnoreCase);
        semantics.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ResolveAsync_ForeignRelativePath_DoesNotTrustPersistedRootAuthority()
    {
        var foreign = GetForeignPathFixture();
        var root = new RootFolder
        {
            Path = foreign.RootPath,
            CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive,
            ResolvedCaseSensitivity = FileSystemCaseSensitivity.Insensitive,
            PathIdentityState = PathIdentityState.Valid
        };
        var roots = new Mock<IRootFolderRepository>(MockBehavior.Strict);
        roots.Setup(repository => repository.GetAllAsync()).ReturnsAsync([root]);
        var semantics = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        var resolver = new AudiobookFilePathIdentityResolver(roots.Object, semantics.Object);

        var identity = await resolver.ResolveAsync(
            new Audiobook { BasePath = foreign.BasePath },
            foreign.RelativeFilePath);

        Assert.Equal(PathIdentityState.Unavailable, identity.State);
        Assert.Equal(foreign.Syntax, identity.Syntax);
        Assert.Null(identity.OwnershipKey);
        Assert.NotNull(identity.LookupKey);
        Assert.Contains("cannot be validated", identity.Reason, StringComparison.OrdinalIgnoreCase);
        roots.Verify(repository => repository.GetAllAsync(), Times.Never);
        semantics.VerifyNoOtherCalls();
    }

    [LinuxFact]
    public async Task ResolveAsync_AmbiguousConfiguredInsensitiveRoot_DoesNotBorrowSensitiveAutoSemantics()
    {
        var rootPath = Path.Join(
            FileService.GetTempDirectory("identity-ambiguous-root"),
            "library");
        Directory.CreateDirectory(rootPath);
        var filePath = Path.Join(rootPath, "Author", "Book", "book.m4b");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "audio");
        var ambiguousRoot = "/" + rootPath;
        Assert.False(FileSystemPathIdentity.TryDetectAbsoluteSyntax(
            ambiguousRoot,
            out _));

        var root = new RootFolder
        {
            Path = ambiguousRoot,
            CaseSensitivityMode = FileSystemCaseSensitivityMode.Insensitive
        };
        var roots = new Mock<IRootFolderRepository>(MockBehavior.Strict);
        roots.Setup(repository => repository.GetAllAsync()).ReturnsAsync([root]);
        var semantics = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        semantics.Setup(resolver => resolver.ResolveAsync(
                filePath,
                FileSystemCaseSensitivityMode.Auto,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileSystemSemanticsResolution(
                new FileSystemPathSemantics(
                    FileSystemPathSyntax.Unix,
                    FileSystemCaseSensitivity.Sensitive),
                PathIdentityState.Valid,
                rootPath));
        var resolver = new AudiobookFilePathIdentityResolver(
            roots.Object,
            semantics.Object);

        var identity = await resolver.ResolveAsync(
            new Audiobook { BasePath = Path.GetDirectoryName(filePath) },
            filePath);

        Assert.Equal(PathIdentityState.Unavailable, identity.State);
        Assert.Equal(FileSystemCaseSensitivityMode.Insensitive, identity.RequestedMode);
        Assert.Contains("ambiguous", identity.Reason, StringComparison.OrdinalIgnoreCase);
        semantics.Verify(service => service.ResolveAsync(
            It.IsAny<string>(),
            It.IsAny<FileSystemCaseSensitivityMode>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [LinuxFact]
    public async Task ResolveAsync_SameRelativePathUnderDifferentBases_IsDistinct()
    {
        var resolver = BuildResolver(new RootFolder { Path = "/library", CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive, ResolvedCaseSensitivity = FileSystemCaseSensitivity.Sensitive, PathIdentityState = PathIdentityState.Valid });
        var first = await resolver.ResolveAsync(new Audiobook { BasePath = "/library/first" }, "book.m4b");
        var second = await resolver.ResolveAsync(new Audiobook { BasePath = "/library/second" }, "book.m4b");
        Assert.NotEqual(first.OwnershipKey, second.OwnershipKey);
        Assert.Equal("/library/first/book.m4b", first.CanonicalPath);
        Assert.Equal("/library/second/book.m4b", second.CanonicalPath);
    }

    [LinuxFact]
    public async Task ResolveAsync_MultipleFiles_LoadsRootFoldersOncePerResolverScope()
    {
        var root = new RootFolder
        {
            Path = "/library",
            CaseSensitivityMode = FileSystemCaseSensitivityMode.Sensitive,
            ResolvedCaseSensitivity = FileSystemCaseSensitivity.Sensitive,
            PathIdentityState = PathIdentityState.Valid
        };
        var roots = new Mock<IRootFolderRepository>(MockBehavior.Strict);
        roots.Setup(repository => repository.GetAllAsync()).ReturnsAsync([root]);
        var resolver = new AudiobookFilePathIdentityResolver(
            roots.Object,
            new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict).Object);
        var audiobook = new Audiobook { BasePath = "/library/author/book" };

        await resolver.ResolveAsync(audiobook, "first.m4b");
        await resolver.ResolveAsync(audiobook, "second.m4b");

        roots.Verify(repository => repository.GetAllAsync(), Times.Once);
    }

    [LinuxFact]
    public async Task ResolveAsync_UnavailableFilesystemSemantics_ReturnsUnavailableIdentity()
    {
        var roots = new Mock<IRootFolderRepository>(MockBehavior.Strict);
        roots.Setup(repository => repository.GetAllAsync()).ReturnsAsync([]);
        var semantics = new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict);
        semantics.Setup(resolver => resolver.ResolveAsync("/offline/library/book.m4b", FileSystemCaseSensitivityMode.Auto, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileSystemSemanticsResolution(new FileSystemPathSemantics(FileSystemPathSyntax.Unix, FileSystemCaseSensitivity.Unknown), PathIdentityState.Unavailable, "/offline/library", "Network path is unavailable."));
        var resolver = new AudiobookFilePathIdentityResolver(roots.Object, semantics.Object);
        var identity = await resolver.ResolveAsync(new Audiobook { BasePath = "/offline/library" }, "book.m4b");
        Assert.Equal(PathIdentityState.Unavailable, identity.State);
        Assert.Null(identity.OwnershipKey);
        Assert.NotNull(identity.LookupKey);
    }

    private static ForeignPathFixture GetForeignPathFixture() =>
        OperatingSystem.IsWindows()
            ? new ForeignPathFixture(
                FileSystemPathSyntax.Unix,
                "/server/mnt/drive/Audiobooks",
                "/server/mnt/drive/Audiobooks/Author/Book",
                "Disc 1/Book.m4b",
                "/server/mnt/drive/Audiobooks/Author/Book/Disc 1/Book.m4b")
            : new ForeignPathFixture(
                FileSystemPathSyntax.Windows,
                "C:\\Audiobooks",
                "C:\\Audiobooks\\Author\\Book",
                "Disc 1\\Book.m4b",
                "C:\\Audiobooks\\Author\\Book\\Disc 1\\Book.m4b");

    private sealed record ForeignPathFixture(
        FileSystemPathSyntax Syntax,
        string RootPath,
        string BasePath,
        string RelativeFilePath,
        string AbsoluteFilePath);

    private static AudiobookFilePathIdentityResolver BuildResolver(RootFolder root)
    {
        var roots = new Mock<IRootFolderRepository>(MockBehavior.Strict);
        roots.Setup(repository => repository.GetAllAsync()).ReturnsAsync([root]);
        return new AudiobookFilePathIdentityResolver(roots.Object, new Mock<IFileSystemSemanticsResolver>(MockBehavior.Strict).Object);
    }
}
