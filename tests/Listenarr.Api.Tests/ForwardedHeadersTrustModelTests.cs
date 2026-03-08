using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using HttpOverridesIPNetwork = Microsoft.AspNetCore.HttpOverrides.IPNetwork;

namespace Listenarr.Api.Tests
{
    public class ForwardedHeadersTrustModelTests : IClassFixture<ListenarrWebApplicationFactory>
    {
        private readonly ListenarrWebApplicationFactory _factory;

        public ForwardedHeadersTrustModelTests(ListenarrWebApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public void ForwardedHeadersOptions_TrustsCommonPrivateProxyNetworks()
        {
            using var scope = _factory.Services.CreateScope();
            var options = scope.ServiceProvider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

            Assert.True(options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedFor));
            Assert.True(options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedProto));
            Assert.True(options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedHost));

            Assert.Contains(options.KnownNetworks, network => Matches(network, "10.0.0.0", 8));
            Assert.Contains(options.KnownNetworks, network => Matches(network, "172.16.0.0", 12));
            Assert.Contains(options.KnownNetworks, network => Matches(network, "192.168.0.0", 16));
            Assert.Contains(options.KnownNetworks, network => Matches(network, "fc00::", 7));
            Assert.Contains(options.KnownNetworks, network => Matches(network, "fe80::", 10));
        }

        private static bool Matches(HttpOverridesIPNetwork network, string prefix, int prefixLength)
        {
            return network.Prefix.Equals(IPAddress.Parse(prefix)) && network.PrefixLength == prefixLength;
        }
    }
}
