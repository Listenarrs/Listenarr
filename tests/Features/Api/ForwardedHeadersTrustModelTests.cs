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
using Listenarr.Infrastructure.Web;
using Listenarr.Tests.Mocks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

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
            Assert.True(options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedPrefix));

            Assert.Contains(options.KnownIPNetworks, network => Matches(network, "10.0.0.0", 8));
            Assert.Contains(options.KnownIPNetworks, network => Matches(network, "172.16.0.0", 12));
            Assert.Contains(options.KnownIPNetworks, network => Matches(network, "192.168.0.0", 16));
            Assert.Contains(options.KnownIPNetworks, network => Matches(network, "fc00::", 7));
            Assert.Contains(options.KnownIPNetworks, network => Matches(network, "fe80::", 10));
        }

        [Fact]
        public void RequestContextAccessor_IgnoresRawForwardedHostHeaders()
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Scheme = "http";
            httpContext.Request.Host = new HostString("listenarr.internal:4545");
            httpContext.Request.Headers["X-Forwarded-Proto"] = "https";
            httpContext.Request.Headers["X-Forwarded-Host"] = "attacker.example";

            var accessor = new AspNetRequestContextAccessor(new HttpContextAccessor
            {
                HttpContext = httpContext
            });

            var snapshot = accessor.Current;

            Assert.NotNull(snapshot);
            Assert.Equal("http", snapshot.Scheme);
            Assert.Equal("listenarr.internal:4545", snapshot.Host);
        }

        private static bool Matches(System.Net.IPNetwork network, string prefix, int prefixLength)
        {
            return network.BaseAddress.Equals(IPAddress.Parse(prefix)) && network.PrefixLength == prefixLength;
        }
    }
}
