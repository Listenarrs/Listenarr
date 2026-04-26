/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using System.Net;
using Listenarr.Tests.Mocks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Listenarr.Tests.Features.Api
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

            Assert.Contains(options.KnownIPNetworks, network => Matches(network, "10.0.0.0", 8));
            Assert.Contains(options.KnownIPNetworks, network => Matches(network, "172.16.0.0", 12));
            Assert.Contains(options.KnownIPNetworks, network => Matches(network, "192.168.0.0", 16));
            Assert.Contains(options.KnownIPNetworks, network => Matches(network, "fc00::", 7));
            Assert.Contains(options.KnownIPNetworks, network => Matches(network, "fe80::", 10));
        }

        private static bool Matches(System.Net.IPNetwork network, string prefix, int prefixLength)
        {
            return network.BaseAddress.Equals(IPAddress.Parse(prefix)) && network.PrefixLength == prefixLength;
        }
    }
}
