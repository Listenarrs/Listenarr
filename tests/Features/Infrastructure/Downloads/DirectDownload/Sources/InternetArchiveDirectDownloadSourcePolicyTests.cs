// Listenarr - Audiobook Management System
// Copyright (C) 2024-2026 Listenarr Contributors

using Listenarr.Infrastructure.Downloads.DirectDownload.Sources;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Downloads.DirectDownload.Sources;

[Trait("Name", "InternetArchiveDirectDownloadSourcePolicyTests")]
[Trait("Category", "InternetArchiveDirectDownloadSourcePolicy")]
public sealed class InternetArchiveDirectDownloadSourcePolicyTests : BaseTests
{
    private readonly InternetArchiveDirectDownloadSourcePolicy _policy = new();

    [Fact]
    public void CanPrepare_EnabledInternetArchiveDownloadUrl_ReturnsTrue()
    {
        var indexer = CreateIndexer("InternetArchive", isEnabled: true);
        var candidate = CreateCandidate();
        var uri = new Uri("https://archive.org/download/book/book.m4b");

        Assert.True(_policy.CanPrepare(indexer, candidate, [uri]));
    }

    [Theory]
    [InlineData(false, "InternetArchive", "https://archive.org/download/book/book.m4b")]
    [InlineData(true, "OtherIndexer", "https://archive.org/download/book/book.m4b")]
    [InlineData(true, "InternetArchive", "https://example.com/download/book/book.m4b")]
    [InlineData(true, "InternetArchive", "https://archive.org/details/book")]
    public void CanPrepare_UntrustedInputs_ReturnsFalse(
        bool isEnabled,
        string implementation,
        string url)
    {
        var indexer = CreateIndexer(implementation, isEnabled);
        var candidate = CreateCandidate();

        Assert.False(_policy.CanPrepare(indexer, candidate, [new Uri(url)]));
    }

    [Fact]
    public void CanPrepare_UrlsFromDifferentItems_ReturnsFalse()
    {
        var indexer = CreateIndexer("InternetArchive", isEnabled: true);

        var result = _policy.CanPrepare(
            indexer,
            CreateCandidate(),
            [
                new Uri("https://archive.org/download/book-one/chapter-01.mp3"),
                new Uri("https://archive.org/download/book-two/chapter-02.mp3")
            ]);

        Assert.False(result);
    }

    [Theory]
    [InlineData("https://archive.org/download/")]
    [InlineData("https://archive.org/download/book")]
    [InlineData("https://archive.org/download/book/")]
    public void TryValidateInitialUri_MissingArtifactPath_ReturnsFalse(string url)
    {
        var result = _policy.TryValidateInitialUri(
            new Uri(url),
            out var error);

        Assert.False(result);
        Assert.Contains("artifact", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidateInitialUri_NestedDownloadArtifactPath_ReturnsTrue()
    {
        var result = _policy.TryValidateInitialUri(
            new Uri("https://archive.org/download/book/subdir/chapter-01.mp3"),
            out var error);

        Assert.True(result);
        Assert.Empty(error);
    }

    [Fact]
    public void TryValidateInitialUri_PrivateTarget_ReturnsFalse()
    {
        var result = _policy.TryValidateInitialUri(
            new Uri("http://127.0.0.1/download/book/book.m4b"),
            out var error);

        Assert.False(result);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void TryValidateRedirectUri_InternetArchiveStorageHost_ReturnsTrue()
    {
        var result = _policy.TryValidateRedirectUri(
            new Uri("https://ia800000.us.archive.org/0/items/book/book.m4b"),
            new Uri("https://archive.org/download/book/book.m4b"),
            out var error);

        Assert.True(result);
        Assert.Empty(error);
    }

    [Fact]
    public void TryValidateRedirectUri_NonInternetArchiveHost_ReturnsFalse()
    {
        var result = _policy.TryValidateRedirectUri(
            new Uri("https://example.com/book.m4b"),
            new Uri("https://archive.org/download/book/book.m4b"),
            out var error);

        Assert.False(result);
        Assert.Contains("trusted", error, StringComparison.OrdinalIgnoreCase);
    }

    private static Indexer CreateIndexer(string implementation, bool isEnabled) => new()
    {
        Name = "Indexer",
        Type = "DirectDownload",
        Implementation = implementation,
        Url = "https://archive.org",
        IsEnabled = isEnabled
    };

    private static TrustedDownloadCandidate CreateCandidate() => new(
        "id",
        "Book",
        "Author",
        "Album",
        "ia",
        "M4B",
        "en",
        100,
        null,
        new DownloadSourceDescriptor(
            IndexerId: 1,
            IndexerImplementation: "InternetArchive",
            Protocol: DownloadProtocol.DirectDownload,
            Locators:
            [
                new DownloadSourceLocator(
                    DownloadSourceLocatorKind.DirectUrl,
                    "https://archive.org/download/book/book.m4b")
            ]));
}
