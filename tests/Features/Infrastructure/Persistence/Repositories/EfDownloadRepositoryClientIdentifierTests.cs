/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Persistence.Repositories
{
    [Trait("Name", "EfDownloadRepositoryClientIdentifierTests")]
    [Trait("Category", "Infrastructure")]
    public sealed class EfDownloadRepositoryClientIdentifierTests : BaseTests
    {
        [Fact]
        public async Task FillMissingClientIdentifiers_TorrentClient_FillsBothWhenMissing()
        {
            await _downloadRepository.AddAsync(
                new DownloadBuilder().WithId("d1").WithTitle("Book").Build());

            var saved = await _downloadRepository.FillMissingClientIdentifiersAsync(
                "d1", "hash-A", isTorrentClient: true);

            Assert.NotNull(saved);
            Assert.Equal("hash-A", saved!["ClientDownloadId"].ToString());
            Assert.Equal("hash-A", saved["TorrentHash"].ToString());
            var persisted = await _downloadRepository.GetByIdAsync("d1");
            Assert.Equal("hash-A", persisted!.GetMetadataString("ClientDownloadId"));
            Assert.Equal("hash-A", persisted.GetMetadataString("TorrentHash"));
        }

        [Fact]
        public async Task FillMissingClientIdentifiers_DoesNotOverwriteDifferentRecordedHash()
        {
            // A grab already recorded the real hash; a title-matched queue item whose snapshot saw
            // no identifier must not rebind it, or a later removal would delete the unrelated
            // same-title torrent (#1055).
            var download = new DownloadBuilder().WithId("d2").WithTitle("Book").Build();
            download.SetMetadata("TorrentHash", "real-hash");
            await _downloadRepository.AddAsync(download);

            var saved = await _downloadRepository.FillMissingClientIdentifiersAsync(
                "d2", "unrelated-hash", isTorrentClient: true);

            Assert.NotNull(saved);
            Assert.Equal("real-hash", saved!["TorrentHash"].ToString());
            var persisted = await _downloadRepository.GetByIdAsync("d2");
            Assert.Equal("real-hash", persisted!.GetMetadataString("TorrentHash"));
            // The conflicting hash must not have leaked into ClientDownloadId either.
            Assert.True(string.IsNullOrEmpty(persisted.GetMetadataString("ClientDownloadId")));
        }

        [Fact]
        public async Task FillMissingClientIdentifiers_NonTorrentClient_DoesNotAddTorrentHash()
        {
            await _downloadRepository.AddAsync(
                new DownloadBuilder().WithId("d3").WithTitle("Book").Build());

            var saved = await _downloadRepository.FillMissingClientIdentifiersAsync(
                "d3", "ddl-id", isTorrentClient: false);

            Assert.NotNull(saved);
            Assert.Equal("ddl-id", saved!["ClientDownloadId"].ToString());
            var persisted = await _downloadRepository.GetByIdAsync("d3");
            Assert.True(string.IsNullOrEmpty(persisted!.GetMetadataString("TorrentHash")));
        }

        [Fact]
        public async Task FillMissingClientIdentifiers_SameValue_LeavesRecordIntact()
        {
            var download = new DownloadBuilder().WithId("d4").WithTitle("Book").Build();
            download.SetMetadata("ClientDownloadId", "hash-A");
            download.SetMetadata("TorrentHash", "hash-A");
            await _downloadRepository.AddAsync(download);

            var saved = await _downloadRepository.FillMissingClientIdentifiersAsync(
                "d4", "hash-A", isTorrentClient: true);

            Assert.Equal("hash-A", saved!["ClientDownloadId"].ToString());
            Assert.Equal("hash-A", saved["TorrentHash"].ToString());
        }

        [Fact]
        public async Task FillMissingClientIdentifiers_MissingDownload_ReturnsNull()
        {
            Assert.Null(await _downloadRepository.FillMissingClientIdentifiersAsync(
                "does-not-exist", "hash-A", isTorrentClient: true));
        }
    }
}
