using Listenarr.Infrastructure.Downloads.DirectDownload.Sources;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Downloads.Submission;

[Trait("Name", "DirectDownloadSubmissionResolverTests")]
[Trait("Category", "DirectDownloadSubmissionResolver")]
public sealed class DirectDownloadSubmissionResolverTests : BaseTests
{
    [Fact]
    public async Task ResolveAsync_MatchingPolicy_ReturnsSubmissionWithPolicyKey()
    {
        var indexer = await _indexerRepository.AddAsync(CreateIndexer());
        var candidate = CreateCandidate(indexer.Id, "https://archive.org/download/book/book.m4b");
        var resolver = new DirectDownloadSubmissionResolver(
            _indexerRepository,
            [new InternetArchiveDirectDownloadSourcePolicy()]);

        var prepared = await resolver.ResolveAsync(candidate, provisionalDownloadId: null, CancellationToken.None);

        var ddl = Assert.IsType<PreparedDirectDownloadSubmission>(prepared);
        Assert.Equal("InternetArchive", ddl.SourcePolicyKey);
        var artifact = Assert.Single(ddl.Artifacts);
        Assert.Equal("https://archive.org/download/book/book.m4b", artifact.DownloadUri.ToString());
    }

    [Fact]
    public async Task ResolveAsync_UnsupportedSource_ThrowsTrustedSourceError()
    {
        var indexer = await _indexerRepository.AddAsync(CreateIndexer(implementation: "OtherIndexer"));
        var candidate = CreateCandidate(indexer.Id, "https://example.com/book.m4b");
        var resolver = new DirectDownloadSubmissionResolver(
            _indexerRepository,
            [new InternetArchiveDirectDownloadSourcePolicy()]);

        var exception = await Assert.ThrowsAsync<DownloadClientSubmissionException>(() =>
            resolver.ResolveAsync(candidate, provisionalDownloadId: null, CancellationToken.None));

        Assert.Contains("not trusted", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_MultipleMatchingPolicies_UsesLowestPriority()
    {
        var indexer = await _indexerRepository.AddAsync(CreateIndexer());
        var candidate = CreateCandidate(indexer.Id, "https://archive.org/download/book/book.m4b");
        var resolver = new DirectDownloadSubmissionResolver(
            _indexerRepository,
            [
                new TestDirectDownloadSourcePolicy("later", priority: 10),
                new TestDirectDownloadSourcePolicy("first", priority: 0)
            ]);

        var prepared = await resolver.ResolveAsync(candidate, provisionalDownloadId: null, CancellationToken.None);

        var ddl = Assert.IsType<PreparedDirectDownloadSubmission>(prepared);
        Assert.Equal("first", ddl.SourcePolicyKey);
    }

    [Fact]
    public async Task ResolveAsync_ArtifactBatch_PreservesArtifactMetadata()
    {
        // Given
        var indexer = await _indexerRepository.AddAsync(CreateIndexer());
        var candidate = new TrustedDownloadCandidate(
            "id",
            "Book",
            "Author",
            "Album",
            "ia",
            "MP3",
            "English",
            300,
            null,
            new DownloadSourceDescriptor(
                indexer.Id,
                "InternetArchive",
                DownloadProtocol.DirectDownload,
                [
                    new(
                        DownloadSourceLocatorKind.DirectUrl,
                        "https://archive.org/download/book/chapter-01.mp3",
                        "chapter-01.mp3",
                        100,
                        DirectDownloadArtifactPackaging.File),
                    new(
                        DownloadSourceLocatorKind.DirectUrl,
                        "https://archive.org/download/book/chapter-02.mp3",
                        "chapter-02.mp3",
                        200,
                        DirectDownloadArtifactPackaging.File)
                ]));
        var resolver = new DirectDownloadSubmissionResolver(
            _indexerRepository,
            [new InternetArchiveDirectDownloadSourcePolicy()]);

        // When
        var prepared = await resolver.ResolveAsync(candidate, null, CancellationToken.None);

        // Then
        var artifacts = Assert.IsType<PreparedDirectDownloadSubmission>(prepared).Artifacts;
        Assert.Collection(
            artifacts,
            first =>
            {
                Assert.Equal("chapter-01.mp3", first.FileName);
                Assert.Equal(100, first.ExpectedSize);
            },
            second =>
            {
                Assert.Equal("chapter-02.mp3", second.FileName);
                Assert.Equal(200, second.ExpectedSize);
            });
    }

    [Fact]
    public async Task ResolveAsync_NormalizedDuplicateArtifactNames_RejectsSubmission()
    {
        var indexer = await _indexerRepository.AddAsync(CreateIndexer());
        var candidate = new TrustedDownloadCandidate(
            "id",
            "Book",
            "Author",
            "Album",
            "ia",
            "MP3",
            "English",
            300,
            null,
            new DownloadSourceDescriptor(
                indexer.Id,
                "InternetArchive",
                DownloadProtocol.DirectDownload,
                [
                    new(
                        DownloadSourceLocatorKind.DirectUrl,
                        "https://archive.org/download/book/chapter-01",
                        "chapter-01",
                        100,
                        DirectDownloadArtifactPackaging.File),
                    new(
                        DownloadSourceLocatorKind.DirectUrl,
                        "https://archive.org/download/book/chapter-01.download",
                        "chapter-01.download",
                        200,
                        DirectDownloadArtifactPackaging.File)
                ]));
        var resolver = new DirectDownloadSubmissionResolver(
            _indexerRepository,
            [new InternetArchiveDirectDownloadSourcePolicy()]);

        var exception = await Assert.ThrowsAsync<DownloadClientSubmissionException>(() =>
            resolver.ResolveAsync(candidate, null, CancellationToken.None));

        Assert.Contains("filename", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static Indexer CreateIndexer(string implementation = "InternetArchive") => new()
    {
        Name = "Internet Archive",
        Type = "DirectDownload",
        Implementation = implementation,
        Url = "https://archive.org",
        IsEnabled = true
    };

    private static TrustedDownloadCandidate CreateCandidate(int indexerId, string url) => new(
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
            IndexerId: indexerId,
            IndexerImplementation: "InternetArchive",
            Protocol: DownloadProtocol.DirectDownload,
            Locators:
            [
                new DownloadSourceLocator(
                    DownloadSourceLocatorKind.DirectUrl,
                    url)
            ]));

    private sealed class TestDirectDownloadSourcePolicy(string key, int priority) : IDirectDownloadSourcePolicy
    {
        public int Priority { get; } = priority;
        public string Key { get; } = key;

        public bool CanPrepare(Indexer indexer, TrustedDownloadCandidate candidate, IReadOnlyList<Uri> uris) => true;

        public bool TryValidateArtifactPlan(IReadOnlyList<Uri> uris, out string error)
        {
            error = string.Empty;
            return true;
        }

        public bool TryValidateInitialUri(Uri uri, out string error)
        {
            error = string.Empty;
            return true;
        }

        public bool TryValidateRedirectUri(Uri uri, Uri previousUri, out string error)
        {
            error = string.Empty;
            return true;
        }

        public string GetFileName(Uri uri, Download download) => "book.m4b";
    }
}
