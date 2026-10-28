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
using Listenarr.Infrastructure.DownloadClients.Common;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.DownloadClients.Common
{
    [Trait("Name", "DownloadClientWorkflowRegistrationTests")]
    [Trait("Category", "DependencyInjection")]
    public class DownloadClientWorkflowRegistrationTests : BaseTests
    {
        [Fact]
        public void AllDownloadClientAdapters_ResolveFromDi()
        {
            var adapters = _provider.GetServices<IDownloadClientAdapter>().ToList();

            Assert.Contains(adapters, a => a.ClientType == DownloadClientTypes.Qbittorrent);
            Assert.Contains(adapters, a => a.ClientType == DownloadClientTypes.Transmission);
            Assert.Contains(adapters, a => a.ClientType == DownloadClientTypes.Sabnzbd);
            Assert.Contains(adapters, a => a.ClientType == DownloadClientTypes.Nzbget);
        }

        [Fact]
        public void DownloadClientWorkflows_ResolveFromDi()
        {
            _provider.GetRequiredService<QbittorrentAddWorkflow>();
            _provider.GetRequiredService<QbittorrentQueueFetchWorkflow>();
            _provider.GetRequiredService<QbittorrentItemFetchWorkflow>();
            _provider.GetRequiredService<QbittorrentImportMarkerWorkflow>();
            _provider.GetRequiredService<QbittorrentImportItemResolver>();

            _provider.GetRequiredService<TransmissionRpcClient>();
            _provider.GetRequiredService<TransmissionAddWorkflow>();
            _provider.GetRequiredService<TransmissionQueueFetchWorkflow>();
            _provider.GetRequiredService<TransmissionItemFetchWorkflow>();
            _provider.GetRequiredService<TransmissionImportItemResolver>();

            _provider.GetRequiredService<SabnzbdRequestBuilder>();
            _provider.GetRequiredService<SabnzbdAddWorkflow>();
            _provider.GetRequiredService<SabnzbdQueueFetchWorkflow>();
            _provider.GetRequiredService<SabnzbdHistoryFetchWorkflow>();
            _provider.GetRequiredService<SabnzbdImportItemResolver>();

            _provider.GetRequiredService<NzbgetXmlRpcClient>();
            _provider.GetRequiredService<NzbgetHistoryReader>();
            _provider.GetRequiredService<NzbgetHistoryEnrichmentWorkflow>();
            _provider.GetRequiredService<NzbgetQueueFetchWorkflow>();
            _provider.GetRequiredService<NzbgetImportItemResolver>();
        }

        [Fact]
        public void QbittorrentAuthSession_UsesScopedLifetime()
        {
            using var firstScope = _provider.CreateScope();
            using var secondScope = _provider.CreateScope();

            var first = firstScope.ServiceProvider.GetRequiredService<QbittorrentAuthSession>();
            var firstAgain = firstScope.ServiceProvider.GetRequiredService<QbittorrentAuthSession>();
            var second = secondScope.ServiceProvider.GetRequiredService<QbittorrentAuthSession>();

            Assert.Same(first, firstAgain);
            Assert.NotSame(first, second);
        }

        [Fact]
        public void SharedProtocolHelpers_UseScopedLifetime()
        {
            using var firstScope = _provider.CreateScope();
            using var secondScope = _provider.CreateScope();

            Assert.Same(
                firstScope.ServiceProvider.GetRequiredService<TransmissionRpcClient>(),
                firstScope.ServiceProvider.GetRequiredService<TransmissionRpcClient>());
            Assert.NotSame(
                firstScope.ServiceProvider.GetRequiredService<TransmissionRpcClient>(),
                secondScope.ServiceProvider.GetRequiredService<TransmissionRpcClient>());

            Assert.Same(
                firstScope.ServiceProvider.GetRequiredService<SabnzbdRequestBuilder>(),
                firstScope.ServiceProvider.GetRequiredService<SabnzbdRequestBuilder>());
            Assert.NotSame(
                firstScope.ServiceProvider.GetRequiredService<SabnzbdRequestBuilder>(),
                secondScope.ServiceProvider.GetRequiredService<SabnzbdRequestBuilder>());

            Assert.Same(
                firstScope.ServiceProvider.GetRequiredService<NzbgetXmlRpcClient>(),
                firstScope.ServiceProvider.GetRequiredService<NzbgetXmlRpcClient>());
            Assert.NotSame(
                firstScope.ServiceProvider.GetRequiredService<NzbgetXmlRpcClient>(),
                secondScope.ServiceProvider.GetRequiredService<NzbgetXmlRpcClient>());
        }
    }
}
