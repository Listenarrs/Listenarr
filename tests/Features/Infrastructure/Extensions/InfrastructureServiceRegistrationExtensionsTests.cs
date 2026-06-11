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
using Listenarr.Infrastructure.Extensions;
using Listenarr.Infrastructure.Cache;
using Listenarr.Infrastructure.Platform;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace Listenarr.Tests.Features.Infrastructure.Extensions
{
    [Trait("Name", "InfrastructureServiceRegistrationExtensionsTests")]
    [Trait("Category", "ServiceCollection")]
    public class InfrastructureServiceRegistrationExtensionsTests
    {
        [Fact]
        public void AddListenarrInfrastructure_RegistersInfrastructureServices()
        {
            var services = new ServiceCollection();

            services.AddListenarrInfrastructure();

            Assert.Contains(
                services,
                descriptor =>
                    descriptor.ServiceType == typeof(IImageCacheService) &&
                    descriptor.ImplementationType == typeof(ImageCacheService) &&
                    descriptor.Lifetime == ServiceLifetime.Singleton);

            Assert.Contains(
                services,
                descriptor =>
                    descriptor.ServiceType == typeof(IApplicationVersionService) &&
                    descriptor.ImplementationType == typeof(ApplicationVersionService) &&
                    descriptor.Lifetime == ServiceLifetime.Scoped);

            using var serviceProvider = services.BuildServiceProvider();
            var httpClientOptions = serviceProvider
                .GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
                .Get(typeof(ImageCacheService).Name);

            Assert.NotEmpty(httpClientOptions.HttpMessageHandlerBuilderActions);
        }
    }
}
