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

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Listenarr.Domain.Search;

/// <summary>Detect source/evidence changes between assessment and automatic dispatch.</summary>
public static class SearchCandidateFingerprint
{
    public static string Compute(SearchResult result) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            result.Id,
            result.IndexerId,
            result.IndexerImplementation,
            result.Title,
            result.Artist,
            result.IdentityEvidence,
            result.DownloadType,
            result.MagnetLink,
            result.TorrentUrl,
            result.NzbUrl,
            result.DownloadReference,
            result.DirectDownloadArtifacts,
            TorrentBytes = result.TorrentFileContent is { } bytes
                ? Convert.ToHexString(SHA256.HashData(bytes)) : null
        }))));
}
