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

using Listenarr.Application.Common;
using Listenarr.Application.Search.Filters;
using Listenarr.Application.Search.Strategies;

namespace Listenarr.Api.Startup;

public static class ListenarrWorkflowRegistration
{
    public static IServiceCollection AddListenarrDomainWorkflows(this IServiceCollection services)
    {
        services.AddScoped<IRootFolderService, RootFolderService>();
        services.AddScoped<ILegacyOutputPathMigrator, LegacyOutputPathMigrator>();
        services.AddMemoryCache();

        services.AddListenarrMetadataWorkflows();
        services.AddListenarrSearchWorkflows();
        services.AddListenarrControllerWorkflows();

        return services;
    }

    private static IServiceCollection AddListenarrMetadataWorkflows(this IServiceCollection services)
    {
        services.AddScoped<MetadataConverters>();
        services.AddScoped<MetadataMerger>();
        services.AddScoped<MetadataSourceCatalog>();
        services.AddScoped<IMetadataStrategy, AudibleMetadataStrategy>();
        services.AddScoped<IMetadataStrategy, AudnexusStrategy>();
        services.AddScoped<MetadataStrategyCoordinator>();
        services.AddScoped<AudibleAuthorPageCollector>();
        services.AddScoped<AudibleSimpleLookupWorkflow>();
        services.AddScoped<AudibleAuthorSearchWorkflow>();
        return services;
    }

    private static IServiceCollection AddListenarrSearchWorkflows(this IServiceCollection services)
    {
        services.AddScoped<SearchProgressReporter>();
        services.AddScoped<IndexerAdditionalSettingsParser>();
        services.AddScoped<IndexerSearchWorkflow>();
        services.AddScoped<ISearchResultFilter, KindleEditionFilter>();
        services.AddScoped<ISearchResultFilter, AudiobookOnlyFilter>();
        services.AddScoped<ISearchResultFilter, PromotionalTitleFilter>();
        services.AddScoped<ISearchResultFilter, ProductLikeTitleFilter>();
        services.AddScoped<ISearchResultFilter, MissingInformationFilter>();
        services.AddScoped<SearchResultFilterPipeline>();
        services.AddScoped<AsinCandidateCollector>();
        services.AddScoped<AsinEnricher>();
        services.AddScoped<SearchResultScorerService>();
        services.AddScoped<SearchResultSortingService>();
        services.AddScoped<SearchFinalDispositionLogger>();
        services.AddScoped<AsinSearchHandler>();
        return services;
    }

    private static IServiceCollection AddListenarrControllerWorkflows(this IServiceCollection services)
    {
        services.AddScoped<LibraryMetadataRescanWorkflow>();
        services.AddScoped<LibraryScanPathResolver>();
        services.AddScoped<LibraryScanQueueWorkflow>();
        services.AddScoped<LibraryAddWorkflow>();
        services.AddScoped<LibraryManualScanWorkflow>();
        services.AddScoped<LibraryBulkEditWorkflow>();
        services.AddScoped<LibraryMoveWorkflow>();
        services.AddScoped<LibraryDeleteWorkflow>();
        services.AddScoped<LibraryUpdateWorkflow>();
        services.AddScoped<LibraryIdentifierWorkflow>();
        services.AddScoped<LibraryPreviewPathWorkflow>();
        services.AddScoped<LibraryQueryWorkflow>();
        services.AddScoped<LibraryRenameWorkflow>();
        services.AddScoped<SearchResponseMapper>();
        services.AddScoped<ImagePlaceholderResolver>();
        services.AddScoped<IndexerTestWorkflow>();
        services.AddScoped<ProwlarrIndexerImportWorkflow>();
        services.AddScoped<ProwlarrIndexerNotificationWorkflow>();
        services.AddScoped<ProwlarrIndexerUpsertWorkflow>();
        services.AddScoped<StructuredSearchWorkflow>();
        services.AddScoped<SearchByTitleWorkflow>();
        services.AddScoped<ManualImportPathPlanner>();
        services.AddScoped<ManualImportCompanionImporter>();
        return services;
    }
}
