using Listenarr.Api.Startup;
using Listenarr.Application.Search.Matching;
using Listenarr.Infrastructure.DependencyInjection;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.DependencyInjection;

[Trait("Name", "ReleaseAnnotationCatalogRegistrationTests")]
[Trait("Category", "Infrastructure")]
public sealed class ReleaseAnnotationCatalogRegistrationTests : BaseTests
{
    private static ServiceProvider BuildProductionRegistration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddListenarrInfrastructure();
        services.AddListenarrDomainWorkflows();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    [Fact]
    public void ProvidersAreScopedWhileTheirValidatedSnapshotIsShared()
    {
        using var root = BuildProductionRegistration();
        using var first = root.CreateScope();
        using var second = root.CreateScope();
        var a = first.ServiceProvider.GetRequiredService<ReleaseAnnotationCatalogProvider>();
        var b = second.ServiceProvider.GetRequiredService<ReleaseAnnotationCatalogProvider>();

        Assert.Same(a, first.ServiceProvider.GetRequiredService<ReleaseAnnotationCatalogProvider>());
        Assert.NotSame(a, b);
        Assert.Same(a.GetSnapshot(), b.GetSnapshot());
        Assert.True(a.GetSnapshot().IsAvailable);
        Assert.Equal(TestAnnotationCatalog.Current.Fingerprint, a.GetSnapshot().Fingerprint);
        Assert.Null(root.GetService<ReleaseAnnotationCatalog>());
        Assert.Throws<InvalidOperationException>(() => root.GetRequiredService<ReleaseAnnotationCatalogProvider>());
    }

    [Fact]
    public async Task ConcurrentScopesResolveDistinctProvidersOverOneLoadedSnapshot()
    {
        using var root = BuildProductionRegistration();
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            using var scope = root.CreateScope();
            var provider = scope.ServiceProvider.GetRequiredService<ReleaseAnnotationCatalogProvider>();
            return (Provider: provider, Snapshot: provider.GetSnapshot());
        })));

        Assert.Equal(results.Length, results.Select(r => r.Provider).Distinct().Count());
        Assert.All(results, r => Assert.Same(results[0].Snapshot, r.Snapshot));
        Assert.NotSame(TestAnnotationCatalog.Current, results[0].Snapshot);
        Assert.True(results[0].Snapshot.IsAvailable);
    }

    [Fact]
    public void SeparateApplicationContainersDoNotShareHiddenStaticCatalogState()
    {
        using var first = BuildProductionRegistration();
        using var second = BuildProductionRegistration();
        using var firstScope = first.CreateScope();
        using var secondScope = second.CreateScope();
        var a = firstScope.ServiceProvider.GetRequiredService<ReleaseAnnotationCatalogProvider>().GetSnapshot();
        var b = secondScope.ServiceProvider.GetRequiredService<ReleaseAnnotationCatalogProvider>().GetSnapshot();

        Assert.NotSame(a, b);
        Assert.Equal(a.Fingerprint, b.Fingerprint);
    }
}
