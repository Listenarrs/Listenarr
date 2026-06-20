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

using Listenarr.Tests.Common;
using Microsoft.Extensions.Hosting;

namespace Listenarr.Tests.Features.Infrastructure.SystemDiagnostics.Version
{
    [Trait("Area", "Infrastructure")]
    [Trait("Name", "ApplicationVersionServiceTests")]
    [Trait("Category", "ApplicationVersionService")]
    public class ApplicationVersionServiceTests
    {
        [Fact]
        [Trait("Method", "Resolve")]
        [Trait("Scenario", "UsesHostApplicationAssemblyInformationalVersion")]
        public void Resolve_UsesHostApplicationAssemblyInformationalVersion()
        {
            var expectedVersion = ApplicationVersionTestUtils.GetExpectedApiVersion();
            var hostEnvironment = new Mock<IHostEnvironment>();
            hostEnvironment
                .SetupGet(environment => environment.ApplicationName)
                .Returns(typeof(global::Program).Assembly.GetName().Name!);

            var version = new ApplicationVersionService(hostEnvironment.Object).Resolve();

            Assert.Equal(expectedVersion, version);
            Assert.NotEqual("1.0.0.0", version);
        }
    }
}
