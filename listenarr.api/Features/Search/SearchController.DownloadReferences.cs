/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Search
{
    public partial class SearchController : ControllerBase
    {
        private void AttachDownloadReference(IndexerSearchResult result)
        {
            if (_downloadReferenceService == null)
            {
                return;
            }

            result.DownloadReference = _downloadReferenceService.Create(
                TrustedDownloadCandidateFactory.Create(SearchResultConverters.ToSearchResult(result)));
        }

        private void AttachDownloadReference(SearchResult result)
        {
            if (_downloadReferenceService == null)
            {
                return;
            }

            result.DownloadReference = _downloadReferenceService.Create(
                TrustedDownloadCandidateFactory.Create(result));
        }

        private static void ClearExecutableLocators(SearchResult result)
        {
            result.MagnetLink = string.Empty;
            result.TorrentUrl = string.Empty;
            result.NzbUrl = string.Empty;
        }

        private static void ClearExecutableLocators(IndexerSearchResult result)
        {
            result.MagnetLink = string.Empty;
            result.TorrentUrl = string.Empty;
            result.NzbUrl = string.Empty;
        }
    }
}
