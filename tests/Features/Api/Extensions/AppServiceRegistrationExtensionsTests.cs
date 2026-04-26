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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Listenarr.Api.Services;
using Listenarr.Api.Extensions;

namespace Listenarr.Tests.Features.Api.Extensions
{
    [Trait("Name", "AppServiceRegistrationExtensionsTests")]
    [Trait("Category", "ServiceCollection")]
    public class AppServiceRegistrationExtensionsTests
    {
        [Fact]
        public void AddListenarrAppServices_RegistersImageCacheServiceAsSingleton()
        {
            var services = new ServiceCollection();
            var config = new ConfigurationBuilder().AddInMemoryCollection().Build();

            services.AddLogging();
            services.AddHttpClient();
            services.AddSingleton<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>(
                new StubWebHostEnvironment());

            services.AddListenarrAppServices(config);

            Assert.Contains(
                services,
                descriptor =>
                    descriptor.ServiceType == typeof(IImageCacheService) &&
                    descriptor.Lifetime == ServiceLifetime.Singleton);
        }

        private sealed class StubWebHostEnvironment : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
        {
            public string ApplicationName { get; set; } = "Listenarr.Tests";
            public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } =
                new Microsoft.Extensions.FileProviders.NullFileProvider();
            public string WebRootPath { get; set; } = string.Empty;
            public string EnvironmentName { get; set; } = "Development";
            public string ContentRootPath { get; set; } = System.IO.Directory.GetCurrentDirectory();
            public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
                new Microsoft.Extensions.FileProviders.NullFileProvider();
        }
    }
}
