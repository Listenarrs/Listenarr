using System.Net;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks.Hosting;

namespace Listenarr.Tests.Features.Api.Features.Metadata;

/// <summary>
/// The backend test containers answer every external API from the test stub: a test that reaches
/// Audible, Audnexus, OpenLibrary or Amazon through a production client gets a fixture or a 404,
/// never the real service.
/// </summary>
[Trait("Name", "ExternalApiIsolationTests")]
[Trait("Category", "Api")]
public sealed class ExternalApiIsolationTests : BaseTests
{
    public static TheoryData<string, string> ProductionClients => new()
    {
        { "default", "https://openlibrary.org/search.json?q=dune" },
        { "us", "https://www.amazon.com/s?k=dune" },
        { "DirectDownload", "https://example.org/book.m4b" },
    };

    [Theory]
    [MemberData(nameof(ProductionClients))]
    public async Task TestContainer_ProductionClient_IsAnsweredByTheStub(string clientName, string url)
    {
        // Given the backend test container
        Init();
        using var client = _provider.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);

        // When a production client calls an external API
        using var response = await client.GetAsync(url);

        // Then the stub answers, not the network (the real services answer 200 or redirect)
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("No test fixture matches this request.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TestContainer_AudibleClient_IsAnsweredFromTheFixtures()
    {
        // Given the backend test container
        Init();
        var audibleService = _provider.GetRequiredService<AudibleService>();

        // When Audible is asked for a book with a fixture
        var book = await audibleService.GetBookMetadataAsync("B008DFUGCQ");

        // Then the fixture answers
        Assert.NotNull(book);
        Assert.Equal("A Princess of Mars", book.Title);
    }

    [Fact]
    public async Task TestContainer_AudibleClient_AnswersNotFoundWithoutAFixture()
    {
        // Given the backend test container
        Init();
        // AddHttpClient<AudibleService>() registers the client under the type's full name, not nameof().
        using var client = _provider.GetRequiredService<IHttpClientFactory>().CreateClient(typeof(AudibleService).FullName!);

        // When Audible is asked for a book without a fixture
        using var response = await client.GetAsync("https://api.audible.com/1.0/catalog/products/UNKNOWN1234");

        // Then the stub answers 404, not the network
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task WebApplicationFactory_ProductionClient_IsAnsweredByTheStub()
    {
        // Given the API host used by the integration tests
        using var factory = new ListenarrWebApplicationFactory();
        using var client = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient("default");

        // When a production client calls an external API
        using var response = await client.GetAsync("https://openlibrary.org/search.json?q=dune");

        // Then the stub answers, not the network
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("No test fixture matches this request.", await response.Content.ReadAsStringAsync());
    }
}
