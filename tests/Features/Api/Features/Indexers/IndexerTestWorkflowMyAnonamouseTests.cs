using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Features.Indexers;

[Trait("Name", "IndexerTestWorkflowMyAnonamouseTests")]
[Trait("Category", "IndexerTestWorkflow")]
public sealed class IndexerTestWorkflowMyAnonamouseTests : BaseTests
{
    [Fact]
    public async Task TestMyAnonamouseAsync_MissingMamIdFailsBeforeTester()
    {
        var tester = new Mock<IMyAnonamouseConnectionTester>();
        var workflow = CreateWorkflow(new Mock<IIndexerRepository>().Object, tester.Object);
        var indexer = CreateIndexer(additionalSettings: "{}");

        var result = await workflow.TestMyAnonamouseAsync(indexer, persist: false);

        Assert.False(result.Succeeded);
        Assert.False(indexer.LastTestSuccessful);
        Assert.Contains("MAM ID is required", indexer.LastTestError);
        tester.Verify(
            value => value.TestAsync(
                It.IsAny<Indexer>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TestMyAnonamouseAsync_SuccessPersistsStatusAndRefreshedCookie()
    {
        const string originalMamId = "original-secret";
        const string refreshedMamId = "refreshed-secret";
        var stored = CreateIndexer(
            7,
            $"{{\"mam_id\":\"{originalMamId}\",\"mam_options\":{{\"filter\":\"Freeleech\"}}}}");
        var repository = new Mock<IIndexerRepository>();
        repository.Setup(value => value.GetByIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(stored);
        var tester = new Mock<IMyAnonamouseConnectionTester>();
        tester.Setup(value => value.TestAsync(
                It.IsAny<Indexer>(),
                originalMamId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(MyAnonamouseConnectionTestResult.Success(refreshedMamId));
        var workflow = CreateWorkflow(repository.Object, tester.Object);

        var result = await workflow.TestMyAnonamouseAsync(stored, persist: true);

        Assert.True(result.Succeeded);
        Assert.True(stored.LastTestSuccessful);
        Assert.Null(stored.LastTestError);
        Assert.Equal(refreshedMamId, MyAnonamouseHelper.TryGetMamId(stored.AdditionalSettings));
        Assert.DoesNotContain(refreshedMamId, result.Message);
        Assert.DoesNotContain(originalMamId, result.Message);
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
    public async Task TestMyAnonamouseAsync_FailurePersistsSanitizedError()
    {
        const string mamId = "never-expose-this";
        var stored = CreateIndexer(9, $"{{\"mam_id\":\"{mamId}\"}}");
        var repository = new Mock<IIndexerRepository>();
        repository.Setup(value => value.GetByIdAsync(9, It.IsAny<CancellationToken>()))
            .ReturnsAsync(stored);
        var tester = new Mock<IMyAnonamouseConnectionTester>();
        tester.Setup(value => value.TestAsync(
                It.IsAny<Indexer>(),
                mamId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(MyAnonamouseConnectionTestResult.Failure(
                "MyAnonamouse authentication failed.",
                403));
        var workflow = CreateWorkflow(repository.Object, tester.Object);

        var result = await workflow.TestMyAnonamouseAsync(stored, persist: true);

        Assert.False(result.Succeeded);
        Assert.Equal(403, result.Status);
        Assert.False(stored.LastTestSuccessful);
        Assert.Equal("MyAnonamouse authentication failed.", stored.LastTestError);
        Assert.DoesNotContain(mamId, result.Message);
        Assert.DoesNotContain(mamId, result.Error);
        repository.Verify(
            value => value.UpdateAsync(
                It.Is<Indexer>(indexer =>
                    indexer.LastTestSuccessful == false &&
                    indexer.LastTestError == "MyAnonamouse authentication failed."),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static IndexerTestWorkflow CreateWorkflow(
        IIndexerRepository repository,
        IMyAnonamouseConnectionTester tester)
        => new(
            repository,
            new HttpClient(),
            NullLogger<IndexerTestWorkflow>.Instance,
            tester);

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
