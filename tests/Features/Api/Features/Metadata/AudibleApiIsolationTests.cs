using System.Net;
using Listenarr.Infrastructure.DependencyInjection;
using Listenarr.FakeProviders;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Features.Metadata;

[Trait("Name", "AudibleApiIsolationTests")]
[Trait("Category", "Api")]
public sealed class AudibleApiIsolationTests : BaseTests
{
    [Fact]
    public async Task StubbedAudibleClient_ReturnsGeneratedAudiobookFixture()
    {
        using var provider = CreateStubbedProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(nameof(AudibleService));
        var audibleService = new AudibleService(client, NullLogger<AudibleService>.Instance, authorPageParser: null);

        var book = await audibleService.GetBookMetadataAsync("B008DFUGCQ");

        Assert.NotNull(book);
        Assert.Equal("B008DFUGCQ", book.Asin);
        Assert.Equal("A Princess of Mars", book.Title);
        Assert.Contains(book.Authors, author => author.Name == "Edgar Rice Burroughs");
    }

    [Fact]
    public async Task StubbedAudibleClient_ReturnsNotFoundForUnknownAsin()
    {
        using var provider = CreateStubbedProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(nameof(AudibleService));

        using var response = await client.GetAsync("https://api.audible.com/1.0/catalog/products/UNKNOWN1234");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static ServiceProvider CreateStubbedProvider()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddListenarrHttpClients(configuration);
        services.AddListenarrAudibleTestStub();
        return services.BuildServiceProvider();
    }
}
