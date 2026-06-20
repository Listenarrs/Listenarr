/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */


namespace Listenarr.Api.Features.Indexers
{
    internal sealed class IndexerResponseRedactor
    {
        public bool ShouldRedact(HttpContext httpContext)
        {
            return HttpSecurityRequestUtils.ShouldRedactSecretsForCaller(httpContext);
        }

        public Indexer RedactIndexerForCaller(Indexer indexer, HttpContext httpContext)
        {
            return ShouldRedact(httpContext) ? ApiResponseRedactor.RedactIndexer(indexer) : indexer;
        }

        public List<Indexer> RedactIndexersForCaller(IEnumerable<Indexer> indexers, HttpContext httpContext)
        {
            return ShouldRedact(httpContext)
                ? indexers.Select(ApiResponseRedactor.RedactIndexer).ToList()
                : indexers.ToList();
        }

        public string? RedactMamIdForCaller(string? mamId, HttpContext httpContext)
        {
            return ShouldRedact(httpContext) && !string.IsNullOrWhiteSpace(mamId)
                ? ApiResponseRedactor.RedactedValue
                : mamId;
        }
    }
}
