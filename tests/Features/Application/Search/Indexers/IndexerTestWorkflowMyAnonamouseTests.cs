using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Search.Indexers;

[Trait("Name", "IndexerTestWorkflowMyAnonamouseTests")]
[Trait("Category", "IndexerTestWorkflow")]
public sealed class IndexerTestWorkflowMyAnonamouseTests : BaseTests
{
    [Fact]
    public async Task TestDraftAsync_MyAnonamouseMissingMamIdPropagatesTesterFailure()
    {
        var tester = CreateTester(IndexerConnectionTestResult.Failure(
            "MyAnonamouse test failed.",
            "MAM ID is required for MyAnonamouse."));
        var workflow = CreateWorkflow(new Mock<IIndexerRepository>().Object, tester.Object);
        var indexer = CreateIndexer(additionalSettings: "{}");

        var result = await workflow.TestDraftAsync(indexer);

        Assert.Equal(IndexerTestWorkflowResultKind.Failed, result.Kind);
        Assert.False(result.TestResult!.Succeeded);
        Assert.False(indexer.LastTestSuccessful);
        Assert.Contains("MAM ID is required", indexer.LastTestError);
        tester.Verify(
            value => value.TestAsync(
                It.Is<Indexer>(candidate => candidate == indexer),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task TestPersistedAsync_MyAnonamouseSuccessPersistsStatusAndRefreshedCookie()
    {
        const string originalMamId = "original-secret";
        const string refreshedMamId = "refreshed-secret";
        var stored = CreateIndexer(
            7,
            $"{{\"mam_id\":\"{originalMamId}\",\"mam_options\":{{\"filter\":\"Freeleech\"}}}}");
        var repository = new Mock<IIndexerRepository>();
        repository.Setup(value => value.GetByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(stored);
        var tester = CreateTester(IndexerConnectionTestResult.Success(
            "MyAnonamouse authentication successful.",
            mamId: refreshedMamId));
        var workflow = CreateWorkflow(repository.Object, tester.Object);

        var result = await workflow.TestPersistedAsync(7);

        Assert.Equal(IndexerTestWorkflowResultKind.Success, result.Kind);
        Assert.True(result.TestResult!.Succeeded);
        Assert.True(stored.LastTestSuccessful);
        Assert.Null(stored.LastTestError);
        Assert.Equal(refreshedMamId, MyAnonamouseHelper.TryGetMamId(stored.AdditionalSettings));
        Assert.DoesNotContain(refreshedMamId, result.TestResult.Message);
        Assert.DoesNotContain(originalMamId, result.TestResult.Message);
        repository.Verify(
            value => value.UpdateAsync(
                It.Is<Indexer>(indexer =>
                    indexer.LastTestSuccessful == true &&
                    indexer.LastTestError == null &&
                    indexer.AdditionalSettings.Contains(refreshedMamId)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task TestPersistedAsync_MyAnonamouseFailurePersistsSanitizedError()
    {
        const string mamId = "never-expose-this";
        var stored = CreateIndexer(9, $"{{\"mam_id\":\"{mamId}\"}}");
        var repository = new Mock<IIndexerRepository>();
        repository.Setup(value => value.GetByIdAsync(9, It.IsAny<CancellationToken>()))
            .ReturnsAsync(stored);
        var tester = CreateTester(IndexerConnectionTestResult.Failure(
            "MyAnonamouse authentication failed.",
            "MyAnonamouse returned HTTP 403.",
            403));
        var workflow = CreateWorkflow(repository.Object, tester.Object);

        var result = await workflow.TestPersistedAsync(9);

        Assert.Equal(IndexerTestWorkflowResultKind.Failed, result.Kind);
        Assert.False(result.TestResult!.Succeeded);
        Assert.Equal(403, result.TestResult.StatusCode);
        Assert.False(stored.LastTestSuccessful);
        Assert.Equal("MyAnonamouse returned HTTP 403.", stored.LastTestError);
        Assert.DoesNotContain(mamId, result.TestResult.Message);
        Assert.DoesNotContain(mamId, result.TestResult.Error);
        repository.Verify(
            value => value.UpdateAsync(
                It.Is<Indexer>(indexer =>
                    indexer.LastTestSuccessful == false &&
                    indexer.LastTestError == "MyAnonamouse returned HTTP 403."),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static Mock<IIndexerConnectionTester> CreateTester(IndexerConnectionTestResult result)
    {
        var tester = new Mock<IIndexerConnectionTester>();
        tester.SetupGet(value => value.IndexerType).Returns("MyAnonamouse");
        tester.Setup(value => value.TestAsync(
                It.IsAny<Indexer>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return tester;
    }

    private static IndexerTestWorkflow CreateWorkflow(
        IIndexerRepository repository,
        IIndexerConnectionTester tester)
        => new(
            repository,
            new[] { tester },
            NullLogger<IndexerTestWorkflow>.Instance);

    private static Indexer CreateIndexer(int id = 0, string? additionalSettings = null)
        => new()
        {
            Id = id,
            Name = "MAM",
            Url = "https://www.myanonamouse.net",
            Implementation = "MyAnonamouse",
            Type = "Torrent",
            AdditionalSettings = additionalSettings ?? """{"mam_id":"secret"}"""
        };
}
