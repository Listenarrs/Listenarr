/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Application.Audiobooks.Deletion;
using Listenarr.Application.Audiobooks.RootFolders;
using Listenarr.Infrastructure.Library.Realtime;
using Listenarr.Infrastructure.Persistence;
using Listenarr.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Listenarr.Infrastructure.DependencyInjection.Library;

internal static class LibraryRegistrationExtensions
{
    public static IServiceCollection AddLibraryServices(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IFilesystemMutationCoordinator, FilesystemMutationCoordinator>();
        services.AddSingleton<IDirectoryObjectIdentityResolver, DirectoryObjectIdentityResolver>();
        services.AddSingleton<IRootFolderStorageHealthResolver, RootFolderStorageHealthResolver>();
        services.AddSingleton<LibraryDirectoryOwnershipBoundaryAuthorizer>();
        services.AddSingleton<IAudiobookOperationCoordinator, AudiobookOperationCoordinator>();
        services.AddSingleton<IAudiobookUpdatePublisher, AudiobookUpdatePublisher>();
        services.AddSingleton<IRootFolderRelocationService, RootFolderRelocationService>();
        services.AddSingleton<IMoveCleanupBoundaryResolver, MoveCleanupBoundaryResolver>();
        services.AddSingleton<ILibraryDirectoryOwnershipStore, EfLibraryDirectoryOwnershipStore>();
        services.AddSingleton<IAudiobookDeletionIntentProbe, AudiobookDeletionIntentProbe>();
        services.AddSingleton<IFileRegistrationRecoveryProbe, FileRegistrationRecoveryProbe>();
        services.AddSingleton<IFileRenameRecoveryProbe, FileRenameRecoveryProbe>();
        services.AddSingleton<IMoveQueueService, MoveQueueService>();
        services.AddScoped<IAudiobookDeletionCommitService, AudiobookDeletionCommitService>();
        services.AddScoped<IAudiobookDeletionIntentStore, AudiobookDeletionIntentStore>();
        services.AddScoped<IAudiobookDeletionIntentReconciler, AudiobookDeletionIntentReconciler>();
        services.AddScoped<IRootFolderStorageConfirmationService, RootFolderStorageConfirmationService>();
        services.AddScoped<IAudiobookFilePathIdentityResolver, AudiobookFilePathIdentityResolver>();
        services.AddScoped<IFileRenameCommitStore, FileRenameCommitStore>();
        services.AddScoped<IFileRegistrationRecoveryService, FileRegistrationRecoveryService>();
        services.AddScoped<IFileRenameRecoveryReconciler, FileRenameRecoveryReconciler>();
        services.AddScoped<IAudiobookFileIdentityReconciler, AudiobookFileIdentityReconciler>();
        services.AddScoped<IRootFolderObjectIdentityReconciler, RootFolderObjectIdentityReconciler>();
        services.AddScoped<ILibraryDirectoryOwnershipReconciler, LibraryDirectoryOwnershipReconciler>();
        services.AddScoped<IAudiobookFileService, AudiobookFileService>();
        services.AddScoped<IScanPathAuthorizationService, ScanPathAuthorizationService>();
        services.AddScoped<IAudiobookScanService, AudiobookScanService>();
        services.AddScoped<MoveSourceManifestService>();
        services.AddScoped<IMoveSourceManifestService>(serviceProvider =>
            serviceProvider.GetRequiredService<MoveSourceManifestService>());
        services.AddScoped<IMoveSourcePlanService>(serviceProvider =>
            serviceProvider.GetRequiredService<MoveSourceManifestService>());
        services.AddScoped<IAuthorCatalogService, AuthorCatalogService>();
        services.AddScoped<ISeriesCatalogService, SeriesCatalogService>();
        services.AddScoped<ILibraryDestinationMutationGuard, LibraryDestinationMutationGuard>();
        services.AddScoped<ILibraryAddService, LibraryAddService>();
        services.AddScoped<IAudiobookFilesystemDeleteService, AudiobookFilesystemDeleteService>();
        services.AddScoped<ILibraryListService, LibraryListService>();
        services.AddScoped<IAuthorMonitoringService, AuthorMonitoringService>();
        services.AddScoped<ISeriesMonitoringService, SeriesMonitoringService>();
        services.AddScoped<IFileNamingService, FileNamingService>();
        services.AddScoped<IRenameService, RenameService>();
        services.AddScoped<IQualityProfileService, QualityProfileService>();
        return services;
    }

    public static IServiceCollection AddLibraryInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IAudiobookRepository, AudiobookRepository>();
        services.AddScoped<ILibraryAddCommitStore, EfLibraryAddCommitStore>();
        services.AddScoped<IQualityProfileRepository, QualityProfileRepository>();
        services.AddScoped<IAudiobookFileRepository, EfAudiobookFileRepository>();
        services.AddScoped<IMoveJobRepository, EfMoveJobRepository>();
        services.AddScoped<IMonitoredAuthorRepository, EfMonitoredAuthorRepository>();
        services.AddScoped<IMonitoredSeriesRepository, EfMonitoredSeriesRepository>();
        services.AddScoped<IRootFolderRepository, EfRootFolderRepository>();
        return services;
    }
}
