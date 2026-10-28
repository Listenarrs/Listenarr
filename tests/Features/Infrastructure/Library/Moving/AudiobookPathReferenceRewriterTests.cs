/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving;

[Trait("Name", "AudiobookPathReferenceRewriterTests")]
[Trait("Category", "Infrastructure")]
public sealed class AudiobookPathReferenceRewriterTests : BaseTests
{
    [WindowsFact]
    public void Rewrite_ForeignSourceAlias_DoesNotMatchNativeCurrentBasePath()
    {
        var current = Path.Join(
            Path.GetPathRoot(Environment.CurrentDirectory)!,
            "listenarr-rewriter-native",
            Guid.NewGuid().ToString("N"));
        var foreignSource = TempFileService
            .GetWindowsRootRelativeForeignAlias(current);
        var target = Path.Join(
            Path.GetPathRoot(Environment.CurrentDirectory)!,
            "listenarr-rewriter-target",
            Guid.NewGuid().ToString("N"));
        var audiobook = new Audiobook { BasePath = current };
        var semantics = FileSystemPathSemantics.CurrentHostDefault;

        var exception = Assert.Throws<AudiobookPathRewriteException>(() =>
            AudiobookPathReferenceRewriter.Rewrite(
                audiobook,
                foreignSource,
                target,
                semantics,
                semantics));

        Assert.Contains("path changed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(current, audiobook.BasePath);
    }

    [Fact]
    public void Rewrite_UsesSourceAndTargetSyntaxWithoutHostPathConversion()
    {
        var audiobook = new Audiobook
        {
            BasePath = "C:\\Library\\Book",
            FilePath = "c:/library/book/book.m4b",
            ImageUrl = "https://example.test/cover.jpg",
            Files =
            [
                new AudiobookFile { Path = "C:\\LIBRARY\\BOOK\\disc-1\\chapter.mp3" },
                new AudiobookFile { Path = "disc-2/chapter.mp3" },
                new AudiobookFile { Path = "C:\\Other\\bonus.mp3" }
            ]
        };
        var sourceSemantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Windows,
            FileSystemCaseSensitivity.Insensitive);
        var targetSemantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);

        AudiobookPathReferenceRewriter.Rewrite(
            audiobook,
            "C:\\Library\\Book",
            "/library/Book",
            sourceSemantics,
            targetSemantics);

        Assert.Equal("/library/Book", audiobook.BasePath);
        Assert.Equal("/library/Book/book.m4b", audiobook.FilePath);
        Assert.Equal("https://example.test/cover.jpg", audiobook.ImageUrl);
        Assert.Equal("/library/Book/disc-1/chapter.mp3", audiobook.Files![0].Path);
        Assert.Equal("disc-2/chapter.mp3", audiobook.Files[1].Path);
        Assert.Equal("C:\\Other\\bonus.mp3", audiobook.Files[2].Path);
    }

    [Fact]
    public void Rewrite_SensitiveSourceDoesNotRemapDifferentCase()
    {
        var audiobook = new Audiobook
        {
            BasePath = "/Library/Book",
            FilePath = "/library/Book/book.m4b"
        };
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);

        AudiobookPathReferenceRewriter.Rewrite(
            audiobook,
            "/Library/Book",
            "/target/Book",
            semantics,
            semantics);

        Assert.Equal("/target/Book", audiobook.BasePath);
        Assert.Equal("/library/Book/book.m4b", audiobook.FilePath);
    }

    [Fact]
    public void Rewrite_CurrentBasePathMatchesNeitherEndpoint_ThrowsBeforeChangingReferences()
    {
        var source = Path.GetFullPath(Path.Join(Path.GetTempPath(), "rewrite-source"));
        var target = Path.GetFullPath(Path.Join(Path.GetTempPath(), "rewrite-target"));
        var current = Path.GetFullPath(Path.Join(Path.GetTempPath(), "rewrite-newer"));
        var filePath = Path.Join(source, "book.m4b");
        var audiobook = new Audiobook
        {
            BasePath = current,
            FilePath = filePath,
            ImageUrl = Path.Join(source, "cover.jpg"),
            Files = [new AudiobookFile { Path = filePath }]
        };
        var semantics = FileSystemPathSemantics.CurrentHostDefault;

        var exception = Assert.Throws<AudiobookPathRewriteException>(() =>
            AudiobookPathReferenceRewriter.Rewrite(
                audiobook,
                source,
                target,
                semantics,
                semantics));

        Assert.Contains("path changed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(current, audiobook.BasePath);
        Assert.Equal(filePath, audiobook.FilePath);
        Assert.Equal(Path.Join(source, "cover.jpg"), audiobook.ImageUrl);
        Assert.Equal(filePath, audiobook.Files![0].Path);
    }

    [Fact]
    public void Rewrite_RecoveryRefreshesIdentityWhenStoredPathAlreadyUsesTarget()
    {
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);
        var sourceIdentity = AudiobookFilePathIdentity.CreateValid(
            "/library/Book/book.m4b",
            semantics,
            FileSystemCaseSensitivityMode.Sensitive,
            "/library/Book");
        var file = AudiobookFile.CreateUnresolved("/target/Book/book.m4b");
        file.ApplyPathIdentity(file.Path!, sourceIdentity);
        var audiobook = new Audiobook
        {
            BasePath = "/target/Book",
            FilePath = "/target/Book/book.m4b",
            Files = [file]
        };
        var expectedIdentity = AudiobookFilePathIdentity.CreateValid(
            "/target/Book/book.m4b",
            semantics,
            FileSystemCaseSensitivityMode.Sensitive,
            "/target/Book");

        AudiobookPathReferenceRewriter.Rewrite(
            audiobook,
            "/library/Book",
            "/target/Book",
            semantics,
            semantics,
            FileSystemCaseSensitivityMode.Sensitive);

        Assert.Equal("/target/Book/book.m4b", file.Path);
        Assert.Equal(expectedIdentity.CanonicalPath, file.CanonicalPath);
        Assert.Equal(expectedIdentity.OwnershipKey, file.PathOwnershipKey);
        Assert.Equal(FileSystemCaseSensitivityMode.Sensitive, file.PathCaseSensitivityMode);
        Assert.Equal(PathIdentityState.Valid, file.PathIdentityState);
    }

    [Fact]
    public void Rewrite_MapsSourceRootReferencesToTargetRoot()
    {
        var audiobook = new Audiobook
        {
            BasePath = "/library/Book",
            FilePath = "/library/Book",
            ImageUrl = "/library/Book",
            Files =
            [
                new AudiobookFile { Path = "/library/Book" }
            ]
        };
        var semantics = new FileSystemPathSemantics(
            FileSystemPathSyntax.Unix,
            FileSystemCaseSensitivity.Sensitive);

        AudiobookPathReferenceRewriter.Rewrite(
            audiobook,
            "/library/Book",
            "/target/Book",
            semantics,
            semantics);

        Assert.Equal("/target/Book", audiobook.BasePath);
        Assert.Equal("/target/Book", audiobook.FilePath);
        Assert.Equal("/target/Book", audiobook.ImageUrl);
        Assert.Equal("/target/Book", audiobook.Files![0].Path);
    }
}
