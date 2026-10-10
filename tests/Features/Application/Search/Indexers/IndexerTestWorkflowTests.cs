using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Search.Indexers;

[Trait("Name", "IndexerTestWorkflowTests")]
[Trait("Category", "IndexerTestWorkflow")]
public sealed class IndexerTestWorkflowTests : BaseTests
{
    [Fact]
    public async Task TestPersistedAsync_MissingIndexerReturnsNotFound()
    {
        var repository = new Mock<IIndexerRepository>();
        repository.Setup(value => value.GetByIdAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Indexer?)null);
        var workflow = CreateWorkflow(repository.Object, CreateTester("Generic").Object);

        var result = await workflow.TestPersistedAsync(42);

        Assert.Equal(IndexerTestWorkflowResultKind.NotFound, result.Kind);
        Assert.Equal("Indexer not found", result.Message);
    }

    [Fact]
    public async Task TestDraftAsync_InternetArchiveWithSpaceUsesInternetArchiveTester()
    {
        var internetArchiveTester = CreateTester("InternetArchive");
        var genericTester = CreateTester("Generic");
        var workflow = CreateWorkflow(new Mock<IIndexerRepository>().Object, internetArchiveTester.Object, genericTester.Object);
        var indexer = CreateIndexer("Internet Archive");

        var result = await workflow.TestDraftAsync(indexer);

        Assert.Equal(IndexerTestWorkflowResultKind.Success, result.Kind);
        internetArchiveTester.Verify(value => value.TestAsync(indexer, It.IsAny<CancellationToken>()), Times.Once);
        genericTester.Verify(value => value.TestAsync(It.IsAny<Indexer>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TestDraftAsync_CustomUsesGenericFallback()
    {
        var genericTester = CreateTester("Generic");
        var workflow = CreateWorkflow(new Mock<IIndexerRepository>().Object, genericTester.Object);
        var indexer = CreateIndexer("Custom");

        var result = await workflow.TestDraftAsync(indexer);

        Assert.Equal(IndexerTestWorkflowResultKind.Success, result.Kind);
        genericTester.Verify(value => value.TestAsync(indexer, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TestPersistedAsync_SuccessUpdatesStoredStatus()
    {
        var stored = CreateIndexer("Newznab");
        stored.Id = 5;
        var repository = new Mock<IIndexerRepository>();
        repository.Setup(value => value.GetByIdAsync(5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(stored);
        var tester = CreateTester("TorznabNewznab");
        var workflow = CreateWorkflow(repository.Object, tester.Object);

        var result = await workflow.TestPersistedAsync(5);

        Assert.Equal(IndexerTestWorkflowResultKind.Success, result.Kind);
        Assert.True(stored.LastTestSuccessful);
        Assert.Null(stored.LastTestError);
        Assert.NotNull(stored.LastTestedAt);
        repository.Verify(value => value.UpdateAsync(
            It.Is<Indexer>(indexer => indexer.Id == 5 && indexer.LastTestSuccessful == true),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TestDraftAsync_DoesNotPersist()
    {
        var repository = new Mock<IIndexerRepository>();
        var workflow = CreateWorkflow(repository.Object, CreateTester("Generic").Object);
        var indexer = CreateIndexer("Generic");

        var result = await workflow.TestDraftAsync(indexer);

        Assert.Equal(IndexerTestWorkflowResultKind.Success, result.Kind);
        Assert.True(indexer.LastTestSuccessful);
        repository.Verify(value => value.UpdateAsync(It.IsAny<Indexer>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TestDraftAsync_MissingGenericFallbackReturnsFailure()
    {
        var workflow = CreateWorkflow(new Mock<IIndexerRepository>().Object, CreateTester("InternetArchive").Object);
        var indexer = CreateIndexer("UnknownIndexer");

        var result = await workflow.TestDraftAsync(indexer);

        Assert.Equal(IndexerTestWorkflowResultKind.Failed, result.Kind);
        Assert.False(result.TestResult!.Succeeded);
        Assert.Contains("No connection tester", result.TestResult.Error);
    }

    private static Indexer CreateIndexer(string implementation)
        => new()
        {
            Name = implementation,
            Type = "Usenet",
            Implementation = implementation,
            Url = "example.com"
        };

    private static Mock<IIndexerConnectionTester> CreateTester(string indexerType)
    {
        var tester = new Mock<IIndexerConnectionTester>();
        tester.SetupGet(value => value.IndexerType).Returns(indexerType);
        tester.Setup(value => value.TestAsync(It.IsAny<Indexer>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(IndexerConnectionTestResult.Success("ok"));
        return tester;
    }

    private static IndexerTestWorkflow CreateWorkflow(
        IIndexerRepository repository,
        params IIndexerConnectionTester[] testers)
        => new(repository, testers, NullLogger<IndexerTestWorkflow>.Instance);
}
