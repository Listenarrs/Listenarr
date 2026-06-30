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
        Assert.Equal("https://archive.org/download/book/book.m4b", ddl.DownloadUri.ToString());
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

        public bool CanPrepare(Indexer indexer, TrustedDownloadCandidate candidate, Uri uri) => true;

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
