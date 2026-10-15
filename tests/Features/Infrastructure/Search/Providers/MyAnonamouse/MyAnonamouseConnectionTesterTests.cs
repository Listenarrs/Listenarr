using System.Net;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.Search.Providers.MyAnonamouse;

[Trait("Name", "MyAnonamouseConnectionTesterTests")]
[Trait("Category", "MyAnonamouseConnectionTester")]
public sealed class MyAnonamouseConnectionTesterTests : BaseTests
{
    [Fact]
    public async Task TestAsync_UsesProductionGetRequestAndAcceptsEmptyResults()
    {
        HttpRequestMessage? captured = null;
        using var client = CreateClient((request, _) =>
        {
            captured = request;
            return Task.FromResult(JsonResponse("""{"data":[]}"""));
        });
        var tester = CreateTester(client);

        var result = await tester.TestAsync(CreateIndexer("https://mam.example", "secret-cookie"));

        Assert.True(result.Succeeded);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Get, captured.Method);
        Assert.Equal("/tor/js/loadSearchJSONbasic.php", captured.RequestUri!.AbsolutePath);
        Assert.Contains("tor%5Btext%5D=test", captured.RequestUri.Query);
        Assert.Contains("tor%5Bperpage%5D=1", captured.RequestUri.Query);
        Assert.Contains("mam_id=secret-cookie", captured.Headers.GetValues("Cookie").Single());
        Assert.NotNull(captured.Headers.Referrer);
        Assert.NotEmpty(captured.Headers.UserAgent);
        Assert.Contains(captured.Headers.Accept, value => value.MediaType == "application/json");
    }

    [Fact]
    public async Task TestAsync_ReturnsRefreshedMamId()
    {
        using var client = CreateClient((_, _) =>
        {
            var response = JsonResponse("""{"data":[]}""");
            response.Headers.Add("Set-Cookie", "mam_id=refreshed; Path=/; HttpOnly");
            return Task.FromResult(response);
        });

        var result = await CreateTester(client).TestAsync(CreateIndexer(mamId: "original"));

        Assert.True(result.Succeeded);
        Assert.Equal("refreshed", result.MamId);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task TestAsync_MapsAuthenticationFailures(HttpStatusCode statusCode)
    {
        using var client = CreateClient((_, _) =>
            Task.FromResult(new HttpResponseMessage(statusCode)));

        var result = await CreateTester(client).TestAsync(CreateIndexer(mamId: "secret"));

        Assert.False(result.Succeeded);
        Assert.Equal((int)statusCode, result.StatusCode);
        Assert.Contains("authentication failed", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", result.Message);
    }

    [Fact]
    public async Task TestAsync_RejectsMalformedJson()
    {
        using var client = CreateClient((_, _) => Task.FromResult(JsonResponse("<html>blocked</html>")));

        var result = await CreateTester(client).TestAsync(CreateIndexer(mamId: "secret"));

        Assert.False(result.Succeeded);
        Assert.Contains("invalid JSON", result.Error);
        Assert.DoesNotContain("secret", result.Error);
    }

    [Fact]
    public async Task TestAsync_MapsOtherHttpFailuresSafely()
    {
        using var client = CreateClient((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        var result = await CreateTester(client).TestAsync(CreateIndexer(mamId: "secret"));

        Assert.False(result.Succeeded);
        Assert.Equal(503, result.StatusCode);
        Assert.Equal("MyAnonamouse returned HTTP 503.", result.Error);
        Assert.DoesNotContain("secret", result.Error);
    }

    [Fact]
    public async Task TestAsync_FollowsRedirectAndReappliesAuthentication()
    {
        var requests = new List<HttpRequestMessage>();
        using var client = CreateClient((request, _) =>
        {
            requests.Add(request);
            if (requests.Count == 1)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("/tor/js/loadSearchJSONbasic.php?redirected=1", UriKind.Relative);
                return Task.FromResult(redirect);
            }

            return Task.FromResult(JsonResponse("""{"data":[]}"""));
        });

        var result = await CreateTester(client).TestAsync(CreateIndexer(mamId: "secret"));

        Assert.True(result.Succeeded);
        Assert.Equal(2, requests.Count);
        Assert.All(requests, request =>
            Assert.Contains("mam_id=secret", request.Headers.GetValues("Cookie").Single()));
    }

    [Fact]
    public async Task TestAsync_PropagatesCallerCancellation()
    {
        using var client = CreateClient(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse("""{"data":[]}""");
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            CreateTester(client).TestAsync(CreateIndexer(mamId: "secret"), cancellation.Token));
    }

    private static MyAnonamouseConnectionTester CreateTester(HttpClient client)
        => new(client, NullLogger<MyAnonamouseConnectionTester>.Instance);

    private static Indexer CreateIndexer(string url = "https://www.myanonamouse.net", string mamId = "secret")
        => new()
        {
            Name = "MAM",
            Url = url,
            Implementation = "MyAnonamouse",
            Type = "Torrent",
            AdditionalSettings = $"{{\"mam_id\":\"{mamId}\"}}"
        };

    private static HttpClient CreateClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        => new(new DelegateHandler(handler)) { BaseAddress = new Uri("https://test.invalid") };

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }
}
