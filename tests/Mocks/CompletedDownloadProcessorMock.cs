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
using Listenarr.Api.Services;
using Listenarr.Application.Repositories;
using Listenarr.Domain.Models;

namespace Listenarr.Tests.Mocks
{
    internal class TestCompletedDownloadProcessor : ICompletedDownloadProcessor
    {
        private readonly IDownloadRepository? _downloadRepository;

        public TestCompletedDownloadProcessor(IDownloadRepository? downloadRepo)
        {
            _downloadRepository = downloadRepo;
        }

        public async Task ProcessCompletedDownloadAsync(string downloadId, string finalPath)
        {
            if (_downloadRepository != null)
            {
                var d = await _downloadRepository.FindAsync(downloadId);
                if (d != null)
                {
                    d.Status = DownloadStatus.Completed;
                    d.FinalPath = finalPath;
                    await _downloadRepository.UpdateAsync(d);
                }
            }
        }
    }
}
