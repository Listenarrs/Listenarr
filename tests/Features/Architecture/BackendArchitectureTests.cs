/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Architecture;

[Trait("Name", "BackendArchitectureTests")]
[Trait("Category", "Architecture")]
public sealed class BackendArchitectureTests : BaseTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    // Pre-existing convention debt is grandfathered by full type identity.
    // No test type added by this branch may appear in this set.
    private static readonly IReadOnlySet<string> LegacyTestConventionExemptions =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "Listenarr.Tests.Features.Api.Common.ListenarrExceptionHandlerTests",
            "Listenarr.Tests.Features.Api.Common.ServerErrorProblemDetailsFilterTests",
            "Listenarr.Tests.Features.Api.Extensions.SwaggerSecurityRequirementDocumentFilterTests",
            "Listenarr.Tests.Features.Api.Features.Configuration.ConfigurationControllerDownloadClientTests",
            "Listenarr.Tests.Features.Api.Features.Configuration.ConfigurationControllerSettingsTests",
            "Listenarr.Tests.Features.Api.Features.Downloads.ManualImport_MultiFileCollisionTests",
            "Listenarr.Tests.Features.Api.Features.Images.ImagesController_AlternateAsinCachedImageAliasTests",
            "Listenarr.Tests.Features.Api.Features.Images.ImagesController_AudnexusAuthorByAsinTests",
            "Listenarr.Tests.Features.Api.Features.Images.ImagesController_AuthorFallbackTests",
            "Listenarr.Tests.Features.Api.Features.Images.ImagesController_AuthorStoredAsinTests",
            "Listenarr.Tests.Features.Api.Features.Images.ImagesController_ContentRootResolutionTests",
            "Listenarr.Tests.Features.Api.Features.Images.ImagesController_LocalIsbnOpenLibraryFallbackTests",
            "Listenarr.Tests.Features.Api.Features.Images.ImagesController_LocalTitleAuthorOpenLibraryFallbackTests",
            "Listenarr.Tests.Features.Api.Features.Images.ImagesController_MetadataDescriptionDoesNotBlockFallbackTests",
            "Listenarr.Tests.Features.Api.Features.Images.ImagesController_MetadataDownloadFallbackTests",
            "Listenarr.Tests.Features.Api.Features.Images.ImagesController_MetadataDownloadTests",
            "Listenarr.Tests.Features.Api.Features.Images.ImagesController_PlaceholderFallbackTests",
            "Listenarr.Tests.Features.Api.Features.Images.ImagesController_TempToLibraryForAudiobookTests",
            "Listenarr.Tests.Features.Api.Features.Metadata.MetadataController_AuthorCatalogTests",
            "Listenarr.Tests.Features.Api.Features.Metadata.MetadataController_AuthorLookupTests",
            "Listenarr.Tests.Features.Api.Features.Metadata.MetadataController_SeriesTests",
            "Listenarr.Tests.Features.Api.Features.Prowlarr.ProwlarrCompatControllerTests",
            "Listenarr.Tests.Features.Api.Features.Search.IntelligentSearchIntegrationTests",
            "Listenarr.Tests.Features.Api.Features.Search.SearchControllerAdvancedNormalizationTests",
            "Listenarr.Tests.Features.Api.Features.Search.SearchControllerTests",
            "Listenarr.Tests.Features.Api.Features.SystemDiagnostics.ReadinessEndpointTests",
            "Listenarr.Tests.Features.Api.ForwardedHeadersTrustModelTests",
            "Listenarr.Tests.Features.Api.LibraryController_GetAllResilienceTests",
            "Listenarr.Tests.Features.Api.LibraryController_IdentifierDeduplicationTests",
            "Listenarr.Tests.Features.Api.LibraryController_MetadataRescanTests",
            "Listenarr.Tests.Features.Api.Middleware.AuthenticationMiddlewareTests",
            "Listenarr.Tests.Features.Api.Models.AudiobookDtoFactoryTests",
            "Listenarr.Tests.Features.Api.ProwlarrEndpointsTests",
            "Listenarr.Tests.Features.Api.SecurityPipelineEndToEndTests",
            "Listenarr.Tests.Features.Api.Services.DownloadNaming_AudiobookMetadataTests",
            "Listenarr.Tests.Features.Api.Services.DownloadNaming_PatternCollapseTests",
            "Listenarr.Tests.Features.Api.Services.FileNamingService_PathLengthTests",
            "Listenarr.Tests.Features.Api.Services.FileNamingService_PatternSelectionTests",
            "Listenarr.Tests.Features.Api.Services.Import_PatternIntegrationTests",
            "Listenarr.Tests.Features.Api.Services.LegacyOutputPathMigratorTests",
            "Listenarr.Tests.Features.Api.Services.MyAnonamouseTorrentAnnounceExtractionTests",
            "Listenarr.Tests.Features.Api.Services.Search.Providers.IndexersAuthTests",
            "Listenarr.Tests.Features.Api.Services.Search.Providers.IndexersControllerProwlarrImportTests",
            "Listenarr.Tests.Features.Api.Services.Search.Providers.IndexersControllerTests",
            "Listenarr.Tests.Features.Api.Services.Search.Providers.IndexersNewznabAuthTests",
            "Listenarr.Tests.Features.Api.Services.Search.Providers.IndexersNewznabParsingTests",
            "Listenarr.Tests.Features.Api.Services.Search.Providers.IndexersPersistedAuthTests",
            "Listenarr.Tests.Features.Api.Services.Search.Providers.MyAnonamouseRedirectSecurityIntegrationTests",
            "Listenarr.Tests.Features.Api.Services.Search.Providers.MyAnonamouseTorrentAnnounceRewriteTests",
            "Listenarr.Tests.Features.Api.Services.Search.Providers.MyAnonamouseTorrentRewriteTests",
            "Listenarr.Tests.Features.Api.Services.UnmatchedScanBackgroundServiceTests",
            "Listenarr.Tests.Features.Api.Services.WorkerCycleRunnerTests",
            "Listenarr.Tests.Features.Api.Services.downloadImportServiceHardlinkTests",
            "Listenarr.Tests.Features.Api.Services.downloadImportServiceTests",
            "Listenarr.Tests.Features.Api.SessionCookieAuthTests",
            "Listenarr.Tests.Features.Api.Startup.ListenarrBuilderFactoryTests",
            "Listenarr.Tests.Features.Api.Utils.FinalizePathHelperTests",
            "Listenarr.Tests.Features.Application.Audiobooks.Catalog.AuthorCatalogServiceTests",
            "Listenarr.Tests.Features.Application.Audiobooks.Catalog.SeriesCatalogServiceTests",
            "Listenarr.Tests.Features.Application.Audiobooks.Files.AudioFileServiceTests",
            "Listenarr.Tests.Features.Application.Audiobooks.Files.AudioFileService_UpdateAudiobookFieldsTests",
            "Listenarr.Tests.Features.Application.Audiobooks.Identifiers.AudiobookIdentifierMapperTests",
            "Listenarr.Tests.Features.Application.Audiobooks.Jobs.MoveQueueServiceTests",
            "Listenarr.Tests.Features.Application.Audiobooks.Matching.AudiobookStatusEvaluatorTests",
            "Listenarr.Tests.Features.Application.Audiobooks.Monitoring.AuthorMonitoringServiceTests",
            "Listenarr.Tests.Features.Application.Audiobooks.Quality.QualityProfileScoringTests",
            "Listenarr.Tests.Features.Application.Audiobooks.Quality.QualityScoringTests",
            "Listenarr.Tests.Features.Application.Audiobooks.Renaming.RenameServiceTests",
            "Listenarr.Tests.Features.Application.Audiobooks.RootFolders.RootFolderServiceTests",
            "Listenarr.Tests.Features.Application.Configuration.Core.StartupConfigServiceTests",
            "Listenarr.Tests.Features.Application.Downloads.Common.DownloadClientUriBuilderTests",
            "Listenarr.Tests.Features.Application.Downloads.Import.DownloadImportServiceTests",
            "Listenarr.Tests.Features.Application.Downloads.Import.DownloadValidationPipelineTests",
            "Listenarr.Tests.Features.Application.Downloads.Processing.DownloadHashRetrievalServiceTests",
            "Listenarr.Tests.Features.Application.Downloads.Processing.DownloadStateMachineTests",
            "Listenarr.Tests.Features.Application.Downloads.Queue.DownloadClientCategoryFilterTests",
            "Listenarr.Tests.Features.Application.Downloads.Queue.DownloadQueueServiceReconciliationTests",
            "Listenarr.Tests.Features.Application.Downloads.Submission.DirectDownloadWorkflowTests",
            "Listenarr.Tests.Features.Application.Downloads.Submission.DownloadIntegrationTests",
            "Listenarr.Tests.Features.Application.Downloads.Submission.DownloadServiceTests",
            "Listenarr.Tests.Features.Application.Downloads.Submission.TrustedDownloadCandidateFactoryTests",
            "Listenarr.Tests.Features.Application.Metadata.Audible.AudibleServiceTests",
            "Listenarr.Tests.Features.Application.Metadata.Core.AudiobookMetadataServiceTests",
            "Listenarr.Tests.Features.Application.Notifications.NotificationsTests",
            "Listenarr.Tests.Features.Application.Notifications.Payloads.NotificationPayloadBuilderAdapterTests",
            "Listenarr.Tests.Features.Application.Search.Core.SearchServiceFixesTests",
            "Listenarr.Tests.Features.Application.Search.Core.SearchWorkflowHelperTests",
            "Listenarr.Tests.Features.Application.Search.Parsing.ParseLanguageTests",
            "Listenarr.Tests.Features.Application.Search.ProwlarrIndexerPayloadParserTests",
            "Listenarr.Tests.Features.Application.Search.Scoring.SearchServiceScoringTests",
            "Listenarr.Tests.Features.Application.Search.Scoring.SearchServiceSortingTests",
            "Listenarr.Tests.Features.Application.Security.Redaction.LogRedactionTests",
            "Listenarr.Tests.Features.Application.Security.Redaction.SecurityRedactionTests",
            "Listenarr.Tests.Features.Domain.Audiobooks.Rules.AudiobookSeriesMembershipHelperTests",
            "Listenarr.Tests.Features.Domain.Downloads.DownloadClientItemTests",
            "Listenarr.Tests.Features.Domain.Downloads.DownloadProcessingJob",
            "Listenarr.Tests.Features.Domain.Utils.FileUtilsTests",
            "Listenarr.Tests.Features.Domain.Utils.QualityMatcherTests",
            "Listenarr.Tests.Features.Domain.Utils.TitleMatchingServiceTests",
            "Listenarr.Tests.Features.Infrastructure.ActivityHistory.Migrations.UnifiedActionHistoryMigrationTests",
            "Listenarr.Tests.Features.Infrastructure.ActivityHistory.Persistence.DownloadHistoryRepositoryTests",
            "Listenarr.Tests.Features.Infrastructure.ActivityHistory.Persistence.HistoryQueryRepositoryTests",
            "Listenarr.Tests.Features.Infrastructure.ActivityHistory.Services.DownloadHistoryServiceTests",
            "Listenarr.Tests.Features.Infrastructure.Configuration.OperationalOptionsValidatorTests",
            "Listenarr.Tests.Features.Infrastructure.Configuration.Paths.ApplicationPathServiceTests",
            "Listenarr.Tests.Features.Infrastructure.Converters.JsonValueConvertersTests",
            "Listenarr.Tests.Features.Infrastructure.DependencyInjection.HostedServicesRegistrationTests",
            "Listenarr.Tests.Features.Infrastructure.DependencyInjection.InfrastructureServiceRegistrationExtensionsTests",
            "Listenarr.Tests.Features.Infrastructure.DownloadClients.Common.UsenetAdapterFilteringTests",
            "Listenarr.Tests.Features.Infrastructure.DownloadClients.Nzbget.NzbgetAdapterTests",
            "Listenarr.Tests.Features.Infrastructure.DownloadClients.Nzbget.NzbgetRemovalWorkflowTests",
            "Listenarr.Tests.Features.Infrastructure.DownloadClients.Qbittorrent.QbittorrentAdapterTests",
            "Listenarr.Tests.Features.Infrastructure.DownloadClients.Qbittorrent.QbittorrentCategoryFilteringTests",
            "Listenarr.Tests.Features.Infrastructure.DownloadClients.Sabnzbd.SabnzbdAdapterTests",
            "Listenarr.Tests.Features.Infrastructure.Downloads.Cleanup.MovedDownloadCleanupProcessorTests",
            "Listenarr.Tests.Features.Infrastructure.Downloads.Import.ImportFinalizationServiceTests",
            "Listenarr.Tests.Features.Infrastructure.Downloads.Monitoring.DownloadMonitorPersistenceTests",
            "Listenarr.Tests.Features.Infrastructure.Downloads.Processing.DownloadProcessingJobCleanupProcessorTests",
            "Listenarr.Tests.Features.Infrastructure.FileSystem.ArchiveExtractorSafetyTests",
            "Listenarr.Tests.Features.Infrastructure.FileSystem.FileStorageSafetyTests",
            "Listenarr.Tests.Features.Infrastructure.Library.Moving.MoveBackgroundService_BroadcastTests",
            "Listenarr.Tests.Features.Infrastructure.Library.Moving.MoveBackgroundService_FailureTests",
            "Listenarr.Tests.Features.Infrastructure.Library.Moving.MoveBackgroundService_FilePathPreservationTests",
            "Listenarr.Tests.Features.Infrastructure.Metadata.Parsing.PathMetadataParserTests",
            "Listenarr.Tests.Features.Infrastructure.Migrations.MigrationMetadataTests",
            "Listenarr.Tests.Features.Infrastructure.Migrations.ReleasedSchemaUpgradeTests",
            "Listenarr.Tests.Features.Infrastructure.Notifications.Delivery.NotificationServiceTests",
            "Listenarr.Tests.Features.Infrastructure.Notifications.Discord.DiscordBotServiceTests",
            "Listenarr.Tests.Features.Infrastructure.Persistence.ApplicationSettingsConcurrencyTests",
            "Listenarr.Tests.Features.Infrastructure.Persistence.EfDownloadDeduplicationTests",
            "Listenarr.Tests.Features.Infrastructure.Persistence.EfDownloadProcessingJobDeduplicationTests",
            "Listenarr.Tests.Features.Infrastructure.Persistence.EfMoveQueuePersistenceTests",
            "Listenarr.Tests.Features.Infrastructure.Persistence.EfUnitOfWorkTests",
            "Listenarr.Tests.Features.Infrastructure.Persistence.TestDatabaseIsolationTests",
            "Listenarr.Tests.Features.Infrastructure.Repositories.AudiobookRepositoryTests",
            "Listenarr.Tests.Features.Infrastructure.Repositories.AudiobookRepository_CatalogCacheReadTests",
            "Listenarr.Tests.Features.Infrastructure.Repositories.DownloadProcessingJobRepositoryTests",
            "Listenarr.Tests.Features.Infrastructure.Security.Identity.LoginRateLimiterTests",
            "Listenarr.Tests.Features.Infrastructure.SystemDiagnostics.Diagnostics.MeterAppMetricsServiceTests",
            "Listenarr.Tests.Features.Infrastructure.SystemDiagnostics.Diagnostics.SystemReadinessServiceTests",
            "Listenarr.Tests.Features.Infrastructure.SystemDiagnostics.Diagnostics.SystemServiceVersionTests",
            "Listenarr.Tests.Features.Infrastructure.SystemDiagnostics.Processes.SystemProcessRunnerTests",
            "Listenarr.Tests.Features.Infrastructure.SystemDiagnostics.Version.ApplicationVersionServiceTests",
            "Listenarr.Tests.Features.Infrastructure.Torrents.TorrentFileDownloaderTests",
        };

    [Fact]
    public void TestClasses_FollowRepositoryConventions()
    {
        var violations = typeof(BackendArchitectureTests).Assembly
            .GetTypes()
            .Where(type => type is { IsAbstract: false, IsClass: true })
            .Where(type => type.GetMethods().Any(method =>
                method.CustomAttributes.Any(attribute =>
                    typeof(FactAttribute).IsAssignableFrom(attribute.AttributeType))))
            .Select(type => new
            {
                TypeName = type.FullName ?? type.Name,
                Problem = DescribeTestConventionViolation(type)
            })
            .Where(violation => violation.Problem != null)
            .OrderBy(violation => violation.TypeName, StringComparer.Ordinal)
            .ToArray();
        var unexpected = violations
            .Where(violation => !LegacyTestConventionExemptions.Contains(violation.TypeName))
            .Select(violation => $"{violation.TypeName}: {violation.Problem}")
            .ToArray();
        var currentViolationNames = violations
            .Select(violation => violation.TypeName)
            .ToHashSet(StringComparer.Ordinal);
        var staleExemptions = LegacyTestConventionExemptions
            .Where(exemption => !currentViolationNames.Contains(exemption))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unexpected.Length == 0,
            $"New test convention violations:{Environment.NewLine}{string.Join(Environment.NewLine, unexpected)}");
        Assert.True(
            staleExemptions.Length == 0,
            $"Remove repaired legacy exemptions:{Environment.NewLine}{string.Join(Environment.NewLine, staleExemptions)}");
    }

    [Fact]
    public void PlatformAndCapabilitySpecificTests_DoNotSilentlyPass()
    {
        var testsRoot = Path.Join(RepositoryRoot, "tests");
        var violations = Directory
            .EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => TestEvidenceSourceAnalyzer
                .Analyze(File.ReadAllText(file))
                .Select(violation =>
                    $"{Path.GetRelativePath(RepositoryRoot, file)}:{violation.Line} "
                    + $"{violation.MethodName}: {violation.Reason}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "Tests may not silently return before proving their assertions. "
            + "Use a platform fact/theory for OS selection and fail explicitly when a required native capability is unavailable:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void RootFolderController_DoesNotUseGenericEnumParsingForPublicRequestValues()
    {
        var controllerPath = Path.Join(
            RepositoryRoot,
            "listenarr.api",
            "Features",
            "Library",
            "RootFoldersController.cs");

        Assert.DoesNotContain(
            "Enum.TryParse<",
            File.ReadAllText(controllerPath),
            StringComparison.Ordinal);
    }

    private static string? DescribeTestConventionViolation(Type testType)
    {
        var problems = new List<string>();
        if (!typeof(BaseTests).IsAssignableFrom(testType))
        {
            problems.Add($"does not inherit {nameof(BaseTests)}");
        }

        var traits = testType.CustomAttributes
            .Where(attribute => attribute.AttributeType == typeof(TraitAttribute))
            .Select(attribute => (
                Name: attribute.ConstructorArguments[0].Value as string,
                Value: attribute.ConstructorArguments[1].Value as string))
            .ToArray();
        if (!traits.Any(trait => trait.Name == "Name" && trait.Value == testType.Name))
        {
            problems.Add("is missing its exact Name trait");
        }
        if (!traits.Any(trait => trait.Name == "Category" && !string.IsNullOrWhiteSpace(trait.Value)))
        {
            problems.Add("is missing a non-empty Category trait");
        }

        return problems.Count == 0
            ? null
            : $"{testType.FullName}: {string.Join(", ", problems)}";
    }

    [Fact]
    public void DomainAndApplication_DoNotReferenceImplementationProjects()
    {
        AssertProjectReferences(
            "listenarr.domain/Listenarr.Domain.csproj",
            []);
        AssertProjectReferences(
            "listenarr.application/Listenarr.Application.csproj",
            ["../listenarr.domain/Listenarr.Domain.csproj"]);
    }

    [Fact]
    public void PathMutatingServices_RequireAudiobookOperationCoordinator()
    {
        Type[] serviceTypes =
        [
            typeof(AudiobookFileService),
            typeof(AudiobookDestinationRewriteService),
            typeof(RenameService),
            typeof(DownloadImportService),
            typeof(ManualImportController),
            typeof(LibraryBulkEditWorkflow),
            typeof(LibraryDeleteWorkflow),
            typeof(LibraryManualScanWorkflow),
            typeof(LibraryMetadataRescanWorkflow),
            typeof(LibraryMoveWorkflow),
            typeof(LibraryUpdateWorkflow),
            typeof(MetadataRescanProcessor),
            typeof(MoveJobProcessor),
            typeof(RootFolderService),
            typeof(RootFolderRelocationService),
            typeof(ScanJobProcessor)
        ];

        AssertRequiredConstructorParameter<IAudiobookOperationCoordinator>(serviceTypes);
    }

    [Fact]
    public void LibraryDestinationCreatingServices_RequireDestinationMutationGuard()
    {
        AssertRequiredConstructorParameter<ILibraryDestinationMutationGuard>(
        [
            typeof(LibraryAddService),
            typeof(LibraryAddWorkflow)
        ]);
    }

    [Fact]
    public void GlobalFilesystemMutationServices_RequireFilesystemMutationCoordinator()
    {
        Type[] serviceTypes =
        [
            typeof(AudiobookFileService),
            typeof(AudiobookDestinationRewriteService),
            typeof(DownloadImportService),
            typeof(LibraryAddService),
            typeof(LibraryAddWorkflow),
            typeof(LibraryManualScanWorkflow),
            typeof(LibraryMoveWorkflow),
            typeof(ManualImportController),
            typeof(MoveJobProcessor),
            typeof(MoveQueueService),
            typeof(RenameService),
            typeof(RootFolderService),
            typeof(RootFolderRelocationService),
            typeof(ScanJobProcessor)
        ];

        AssertRequiredConstructorParameter<IFilesystemMutationCoordinator>(serviceTypes);
    }

    [Fact]
    public void AudiobookFileOwnership_CannotBypassIdentityClaimContract()
    {
        var pathProperty = typeof(AudiobookFile).GetProperty(nameof(AudiobookFile.Path));
        Assert.NotNull(pathProperty);
        Assert.NotNull(pathProperty!.SetMethod);
        Assert.False(pathProperty.SetMethod!.IsPublic);

        var repositoryMethods = typeof(IAudiobookFileRepository)
            .GetMethods()
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("AddAsync", repositoryMethods);
        Assert.DoesNotContain("ExistsAtPathAsync", repositoryMethods);
        Assert.DoesNotContain("IsPathUsedByOtherAsync", repositoryMethods);
        Assert.Contains(nameof(IAudiobookFileRepository.ClaimAsync), repositoryMethods);
        Assert.Contains(nameof(IAudiobookFileRepository.CheckOwnershipAsync), repositoryMethods);
    }

    [Fact]
    public void RenameService_RequiresOwnershipAndGlobalMutationContracts()
    {
        AssertRequiredConstructorParameter<IAudiobookFileRepository>([typeof(RenameService)]);
        AssertRequiredConstructorParameter<IAudiobookFilePathIdentityResolver>([typeof(RenameService)]);
        AssertRequiredConstructorParameter<IFilesystemMutationCoordinator>([typeof(RenameService)]);
        AssertRequiredConstructorParameter<IAudiobookOperationCoordinator>([typeof(RenameService)]);
    }

    [Fact]
    public void DomainAndApplication_DoNotReferenceForbiddenImplementationPackages()
    {
        var forbiddenPackages = new[]
        {
            "Microsoft.EntityFrameworkCore",
            "Microsoft.EntityFrameworkCore.Sqlite",
            "Microsoft.AspNetCore",
            "HtmlAgilityPack",
            "SixLabors.ImageSharp",
            "TagLibSharp",
            "BencodeNET",
            "SharpCompress"
        };

        AssertNoPackages("listenarr.domain/Listenarr.Domain.csproj", forbiddenPackages);
        AssertNoPackages("listenarr.application/Listenarr.Application.csproj", forbiddenPackages);
    }

    [Fact]
    public void Api_DoesNotReferenceInfrastructureImplementationPackages()
    {
        AssertNoPackages(
            "listenarr.api/Listenarr.Api.csproj",
            [
                "Microsoft.Data.Sqlite.Core",
                "Microsoft.EntityFrameworkCore",
                "Microsoft.EntityFrameworkCore.Design",
                "Microsoft.EntityFrameworkCore.Sqlite",
                "HtmlAgilityPack",
                "SixLabors.ImageSharp",
                "TagLibSharp",
                "Polly",
                "Microsoft.Extensions.Http.Polly"
            ]);
    }

    [Fact]
    public void Application_DoesNotUseServiceLocation()
    {
        var applicationRoot = Path.Join(RepositoryRoot, "listenarr.application");
        var serviceLocationPattern = new Regex(
            @"\b(?:IServiceProvider|IServiceScopeFactory|CreateScope\s*\(|GetRequiredService\s*<|GetService\s*<)",
            RegexOptions.Compiled);

        var violations = Directory
            .EnumerateFiles(applicationRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildArtifact(file))
            .Where(file => serviceLocationPattern.IsMatch(File.ReadAllText(file)))
            .Select(file => Normalize(Path.GetRelativePath(applicationRoot, file)))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void Application_DoesNotBlockOnAsyncOperations()
    {
        var applicationRoot = Path.Join(RepositoryRoot, "listenarr.application");
        var syncOverAsyncPattern = new Regex(
            @"GetAwaiter\s*\(\s*\)\s*\.\s*GetResult\s*\(\s*\)",
            RegexOptions.Compiled);

        var violations = Directory
            .EnumerateFiles(applicationRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildArtifact(file))
            .Where(file => syncOverAsyncPattern.IsMatch(File.ReadAllText(file)))
            .Select(file => Normalize(Path.GetRelativePath(applicationRoot, file)))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void NewDirectHttpClientConstruction_IsRestrictedToDocumentedLegacyAdapters()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "listenarr.application/Common/MyAnonamouseHelper.cs",
            "listenarr.api/Features/Indexers/IndexerDebugSearchWorkflow.cs",
            "listenarr.infrastructure/DownloadClients/Qbittorrent/QbittorrentCookieSession.cs",
            "listenarr.infrastructure/DownloadClients/Transmission/TransmissionAdapter.cs",
            "listenarr.infrastructure/Torrents/TorrentFileDownloader.cs"
        };
        var roots = new[]
        {
            "listenarr.application",
            "listenarr.infrastructure",
            "listenarr.api"
        };
        var constructionPattern = new Regex(@"\bnew\s+HttpClient\s*\(", RegexOptions.Compiled);

        var violations = roots
            .SelectMany(root => Directory.EnumerateFiles(
                Path.Join(RepositoryRoot, root),
                "*.cs",
                SearchOption.AllDirectories))
            .Where(file => !IsBuildArtifact(file))
            .Where(file => constructionPattern.IsMatch(File.ReadAllText(file)))
            .Select(file => Normalize(Path.GetRelativePath(RepositoryRoot, file)))
            .Where(file => !allowed.Contains(file))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void NewControllerBroadCatches_AreForbiddenOutsideDocumentedLegacyControllers()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Features/Configuration/ApiKeyController.cs",
            "Features/Configuration/ApiSourcesController.cs",
            "Features/Configuration/SettingsController.cs",
            "Features/Configuration/StartupConfigurationController.cs",
            "Features/DownloadClients/DownloadClientController.cs",
            "Features/Downloads/DownloadController.cs",
            "Features/Downloads/DownloadsController.cs",
            "Features/Downloads/ManualImportController.cs",
            "Features/Downloads/RemotePathMappingsController.cs",
            "Features/Identity/AccountController.cs",
            "Features/Identity/AntiforgeryController.cs",
            "Features/Images/ImagesController.cs",
            "Features/Library/AuthorMonitoringController.cs",
            "Features/Library/QualityProfileController.cs",
            "Features/Library/SeriesMonitoringController.cs",
            "Features/Metadata/AdminMetadataController.cs",
            "Features/Metadata/MetadataController.cs",
            "Features/Notifications/NotificationsController.cs",
            "Features/Prowlarr/ProwlarrCompatController.cs",
            "Features/Search/SearchController.cs",
            "Features/SystemDiagnostics/DiscordController.cs",
            "Features/SystemDiagnostics/FfmpegController.cs",
            "Features/SystemDiagnostics/FileSystemController.cs",
            "Features/SystemDiagnostics/SystemController.cs"
        };
        var apiRoot = Path.Join(RepositoryRoot, "listenarr.api");
        var broadCatchPattern = new Regex(@"catch\s*\(\s*Exception\b", RegexOptions.Compiled);

        var violations = Directory
            .EnumerateFiles(Path.Join(apiRoot, "Features"), "*Controller.cs", SearchOption.AllDirectories)
            .Where(file => broadCatchPattern.IsMatch(File.ReadAllText(file)))
            .Select(file => Normalize(Path.GetRelativePath(apiRoot, file)))
            .Where(file => !allowed.Contains(file))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void ActiveProductionNamespaces_MatchPhysicalFolders()
    {
        AssertNamespacesMatchFolders("listenarr.domain", "Listenarr.Domain");
        AssertNamespacesMatchFolders("listenarr.application", "Listenarr.Application");
        AssertNamespacesMatchFolders("listenarr.infrastructure", "Listenarr.Infrastructure");
        AssertNamespacesMatchFolders("listenarr.api", "Listenarr.Api");
    }

    [Fact]
    public void EfMigrations_RemainInTheirHistoricalNamespace()
    {
        var migrationRoot = Path.Join(
            RepositoryRoot,
            "listenarr.infrastructure",
            "Persistence",
            "Migrations");

        foreach (var file in Directory.EnumerateFiles(migrationRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relativeDirectory = Path.GetRelativePath(migrationRoot, Path.GetDirectoryName(file)!);
            var expectedNamespace = relativeDirectory == "."
                ? "Listenarr.Infrastructure.Persistence.Migrations"
                : $"Listenarr.Infrastructure.Persistence.Migrations.{ToNamespace(relativeDirectory)}";
            Assert.Equal(expectedNamespace, ReadNamespace(file));
        }
    }

    [Fact]
    public void DirectDownloadProcessor_DoesNotContainSourceSpecificTrustRules()
    {
        var processorFile = Path.Join(
            RepositoryRoot,
            "listenarr.infrastructure",
            "Downloads",
            "DirectDownload",
            "DirectDownloadProcessor.cs");
        var source = File.ReadAllText(processorFile);
        var forbiddenProviderLiterals = new[]
        {
            "archive.org",
            "InternetArchive",
            "AnnasArchive",
            "annas"
        };

        Assert.DoesNotContain(forbiddenProviderLiterals, literal =>
            source.Contains(literal, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConcreteDownloadAdapters_DoNotExposeLegacyFetchDownloadsAsync()
    {
        var adapterRoot = Path.Join(RepositoryRoot, "listenarr.infrastructure", "DownloadClients");
        var legacyPollingMethodPattern = new Regex(
            @"\bpublic\s+(?:async\s+)?Task\s*<\s*List\s*<\s*Download\s*>\s*>\s+FetchDownloadsAsync\s*\(",
            RegexOptions.Compiled);

        var violations = Directory
            .EnumerateFiles(adapterRoot, "*Adapter.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildArtifact(file))
            .Where(file => legacyPollingMethodPattern.IsMatch(File.ReadAllText(file)))
            .Select(file => Normalize(Path.GetRelativePath(RepositoryRoot, file)))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void ApiFilesystemUsage_IsRestrictedToKnownLegacyFilesDuringMigration()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Program.Testing.cs"
        };
        var apiRoot = Path.Join(RepositoryRoot, "listenarr.api");
        var filesystemPattern = new Regex(
            @"\b(?:System\.IO\.)?(?:File|Directory)\.(?:Exists|Read|Write|Delete|Move|Copy|Create|Enumerate|GetFiles|GetDirectories)",
            RegexOptions.Compiled);

        var violations = Directory
            .EnumerateFiles(apiRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildArtifact(file))
            .Where(file => filesystemPattern.IsMatch(File.ReadAllText(file)))
            .Select(file => Normalize(Path.GetRelativePath(apiRoot, file)))
            .Where(file => !allowed.Contains(file))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void Application_DoesNotImplementFilesystemAccess()
    {
        var applicationRoot = Path.Join(RepositoryRoot, "listenarr.application");
        var filesystemPattern = new Regex(
            @"\b(?:System\.IO\.)?(?:File|Directory)\.(?:Exists|Read|Write|Delete|Move|Copy|Create|Enumerate|GetFiles|GetDirectories|GetCurrentDirectory|GetParent)",
            RegexOptions.Compiled);

        var violations = Directory
            .EnumerateFiles(applicationRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildArtifact(file))
            .Where(file => filesystemPattern.IsMatch(File.ReadAllText(file)))
            .Select(file => Normalize(Path.GetRelativePath(applicationRoot, file)))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void Domain_DoesNotImplementFilesystemAccess()
    {
        var domainRoot = Path.Join(RepositoryRoot, "listenarr.domain");
        var filesystemPattern = new Regex(
            @"\b(?:System\.IO\.)?(?:File|Directory)\.(?:Exists|Read|Write|Delete|Move|Copy|Create|Enumerate|GetFiles|GetDirectories|GetCurrentDirectory|GetParent|Open)",
            RegexOptions.Compiled);

        var violations = Directory
            .EnumerateFiles(domainRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildArtifact(file))
            .Where(file => filesystemPattern.IsMatch(File.ReadAllText(file)))
            .Select(file => Normalize(Path.GetRelativePath(domainRoot, file)))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void ExternalHttpClients_HaveOneRegistrationOwner()
    {
        var registrationFiles = new[]
        {
            Path.Join(RepositoryRoot, "listenarr.api", "Startup", "ListenarrWorkflowRegistration.cs"),
            Path.Join(RepositoryRoot, "listenarr.infrastructure", "DependencyInjection", "Platform", "PlatformRegistrationExtensions.cs"),
            Path.Join(RepositoryRoot, "listenarr.infrastructure", "DependencyInjection", "Metadata", "MetadataRegistrationExtensions.cs"),
            Path.Join(RepositoryRoot, "listenarr.infrastructure", "DependencyInjection", "InfrastructureStartupCompositionExtensions.cs")
        };
        var source = string.Join(Environment.NewLine, registrationFiles.Select(File.ReadAllText));

        Assert.Single(Regex.Matches(source, "AddHttpClient\\(\\\"us\\\"\\)"));
        Assert.Single(Regex.Matches(source, "AddHttpClient<AudibleService>"));
        Assert.Single(Regex.Matches(source, "AddHttpClient<(?:IAudnexusService,\\s*)?AudnexusService>"));
    }

    [Fact]
    public void FeatureRegistrations_AreOwnedByFeatureModules()
    {
        var dependencyInjectionRoot = Path.Join(
            RepositoryRoot,
            "listenarr.infrastructure",
            "DependencyInjection");
        var compatibilityFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AppServiceRegistrationExtensions.cs",
            "HostedServiceRegistrationExtensions.cs",
            "InfrastructureServiceRegistrationExtensions.cs",
            "ServiceRegistrationExtensions.cs"
        };
        var registrationPattern = new Regex(
            @"\bservices\.(?:AddScoped|AddSingleton|AddTransient|AddHostedService|AddHttpClient|Configure|TryAdd)",
            RegexOptions.Compiled);

        var violations = Directory
            .EnumerateFiles(dependencyInjectionRoot, "*.cs", SearchOption.TopDirectoryOnly)
            .Where(file => compatibilityFiles.Contains(Path.GetFileName(file)))
            .Where(file => registrationPattern.IsMatch(File.ReadAllText(file)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void FileMover_DoesNotOwnManagedHierarchyCreation()
    {
        var fileMoverRoot = Path.Join(
            RepositoryRoot,
            "listenarr.infrastructure",
            "FileSystem");
        var createMissingPattern = new Regex(
            @"createMissing\s*:\s*true",
            RegexOptions.Compiled);
        var matches = Directory
            .EnumerateFiles(fileMoverRoot, "FileMover*.cs", SearchOption.TopDirectoryOnly)
            .SelectMany(file => createMissingPattern
                .Matches(File.ReadAllText(file))
                .Select(_ => Normalize(Path.GetRelativePath(RepositoryRoot, file))))
            .ToList();

        Assert.Equal(
            "listenarr.infrastructure/FileSystem/FileMover.FileMoveLocks.cs",
            Assert.Single(matches));
        var lockSource = File.ReadAllText(Path.Join(
            fileMoverRoot,
            "FileMover.FileMoveLocks.cs"));
        Assert.Contains(
            "OpenFileMoveLockDirectory()",
            lockSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "directory,\n            createMissing: true",
            lockSource.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "createDestinationParent",
            string.Join(Environment.NewLine, Directory
                .EnumerateFiles(fileMoverRoot, "FileMover*.cs", SearchOption.TopDirectoryOnly)
                .Select(File.ReadAllText)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AudiobookDatabaseDeletion_UsesSharedCommitBoundary()
    {
        const string commitOwner =
            "listenarr.application/Audiobooks/Deletion/AudiobookDeletionCommitService.cs";
        var directDeletePattern = new Regex(
            @"\.\s*DeleteByIdAsync\s*\(",
            RegexOptions.Compiled);
        var productionRoots = new[]
        {
            "listenarr.application",
            "listenarr.infrastructure",
            "listenarr.api"
        };

        var violations = productionRoots
            .SelectMany(root => Directory.EnumerateFiles(
                Path.Join(RepositoryRoot, root),
                "*.cs",
                SearchOption.AllDirectories))
            .Where(file => !IsBuildArtifact(file))
            .Where(file => directDeletePattern.IsMatch(File.ReadAllText(file)))
            .Select(file => Normalize(Path.GetRelativePath(RepositoryRoot, file)))
            .Where(file => !string.Equals(
                file,
                commitOwner,
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void Controllers_DoNotResolveServicesOrImplementPersistence()
    {
        var controllerFiles = Directory
            .EnumerateFiles(
                Path.Join(RepositoryRoot, "listenarr.api", "Features"),
                "*Controller.cs",
                SearchOption.AllDirectories)
            .Where(file => !IsBuildArtifact(file))
            .ToList();
        var forbiddenPattern = new Regex(
            @"\b(?:IServiceScopeFactory|DbContext|CreateScope\s*\(|GetRequiredService\s*<|GetService\s*<)",
            RegexOptions.Compiled);

        var violations = controllerFiles
            .Where(file => forbiddenPattern.IsMatch(File.ReadAllText(file)))
            .Select(file => Normalize(Path.GetRelativePath(
                Path.Join(RepositoryRoot, "listenarr.api"),
                file)))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void ActiveProductionSourceFiles_RemainFocused()
    {
        var projectRoots = new[]
        {
            "listenarr.domain",
            "listenarr.application",
            "listenarr.infrastructure",
            "listenarr.api"
        };
        var violations = projectRoots
            .SelectMany(root => Directory.EnumerateFiles(
                Path.Join(RepositoryRoot, root),
                "*.cs",
                SearchOption.AllDirectories))
            .Where(file => !IsBuildArtifact(file))
            .Where(file => !file.Contains(
                $"{Path.DirectorySeparatorChar}Persistence{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Select(file => new
            {
                File = Normalize(Path.GetRelativePath(RepositoryRoot, file)),
                Lines = File.ReadLines(file).Count()
            })
            .Where(source => source.Lines > 500)
            .Select(source => $"{source.File} ({source.Lines} lines)")
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void LegacyDirectoryMover_IsNotConnectedToProductionWorkflows()
    {
        var projectRoots = new[]
        {
            "listenarr.domain",
            "listenarr.application",
            "listenarr.infrastructure",
            "listenarr.api"
        };
        var invocationPattern = new Regex(
            @"\.(?:MoveDirectoryAsync|CopyDirectoryAsync)\s*\(",
            RegexOptions.Compiled);
        var violations = projectRoots
            .SelectMany(root => Directory.EnumerateFiles(
                Path.Join(RepositoryRoot, root),
                "*.cs",
                SearchOption.AllDirectories))
            .Where(file => !IsBuildArtifact(file))
            .Where(file => !file.Contains(
                $"{Path.DirectorySeparatorChar}Persistence{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Where(file => invocationPattern.IsMatch(File.ReadAllText(file)))
            .Select(file => Normalize(Path.GetRelativePath(RepositoryRoot, file)))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void MoveSourceAncestorCleanup_RequiresDurableOwnership()
    {
        var requestSource = File.ReadAllText(Path.Join(
            RepositoryRoot,
            "listenarr.infrastructure",
            "Library",
            "Moving",
            "AudiobookContentMoveService.cs"));
        var cleanupSource = File.ReadAllText(Path.Join(
            RepositoryRoot,
            "listenarr.infrastructure",
            "Library",
            "Moving",
            "AudiobookContentMoveService.SourceAncestorCleanup.cs"));

        Assert.DoesNotContain(
            "AllowUnownedSourceAncestorCleanup",
            requestSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TryDeleteEmptyDirectory",
            cleanupSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DurableMoveArchitectureDocumentation_MatchesCurrentMarkerlessContracts()
    {
        var architecture = File.ReadAllText(Path.Join(
            RepositoryRoot,
            "BACKEND_ARCHITECTURE.md"));

        Assert.Contains(
            $"Identity-key version {MoveManifestIdentity.Version}",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            $"Move manifest identity version {MoveManifestIdentity.Version}",
            architecture,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Generic `FileMover.MoveDirectoryAsync` fallback",
            architecture,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Named publication, move, and empty-source `.state` directories",
            architecture,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "tombstoned scaffold cleanup",
            architecture,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Each owned directory has two matching structured proofs",
            architecture,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StartupFilesystemReconciliation_IsBackgroundAndCannotReenterBlockingStartupTasks()
    {
        var startupTasks = File.ReadAllText(Path.Join(
            RepositoryRoot,
            "listenarr.api",
            "Startup",
            "ListenarrStartupTasks.cs"));
        var program = File.ReadAllText(Path.Join(
            RepositoryRoot,
            "listenarr.api",
            "Program.cs"));
        var reconciler = File.ReadAllText(Path.Join(
            RepositoryRoot,
            "listenarr.infrastructure",
            "Persistence",
            "LibraryFilesystemStartupReconciliationService.cs"));

        Assert.Contains("ApplyListenarrDatabaseMigrations", program, StringComparison.Ordinal);
        Assert.Contains("RunListenarrStartupTasksAsync", program, StringComparison.Ordinal);
        Assert.DoesNotContain("IRootFolderObjectIdentityReconciler", startupTasks, StringComparison.Ordinal);
        Assert.DoesNotContain("IRootFolderRelocationService", startupTasks, StringComparison.Ordinal);
        Assert.DoesNotContain("ILibraryDirectoryOwnershipReconciler", startupTasks, StringComparison.Ordinal);
        Assert.DoesNotContain("IAudiobookFileIdentityReconciler", startupTasks, StringComparison.Ordinal);
        Assert.Contains(": BackgroundService", reconciler, StringComparison.Ordinal);
        Assert.Contains("await Task.Yield()", reconciler, StringComparison.Ordinal);
        Assert.Contains("IRootFolderObjectIdentityReconciler", reconciler, StringComparison.Ordinal);
        Assert.Contains("IRootFolderRelocationService", reconciler, StringComparison.Ordinal);
        Assert.Contains("ILibraryDirectoryOwnershipReconciler", reconciler, StringComparison.Ordinal);
        Assert.Contains("IAudiobookFileIdentityReconciler", reconciler, StringComparison.Ordinal);
    }

    [Fact]
    public void FilesystemDependentWorkers_WaitOnSharedStartupReadinessGate()
    {
        var workerFiles = new[]
        {
            "listenarr.infrastructure/Library/Moving/MoveBackgroundService.cs",
            "listenarr.infrastructure/Library/Scanning/ScanBackgroundService.cs",
            "listenarr.infrastructure/Library/Scanning/UnmatchedScanBackgroundService.cs",
            "listenarr.infrastructure/Downloads/Processing/DownloadProcessingJobProcessor.cs",
            "listenarr.infrastructure/Metadata/Jobs/MetadataRescanService.cs"
        };

        foreach (var relativePath in workerFiles)
        {
            var source = File.ReadAllText(Path.Join(
                RepositoryRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Contains("ILibraryFilesystemReadiness", source, StringComparison.Ordinal);
            Assert.Contains("WaitUntilReadyAsync", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FilesystemReadiness_HasOneSingletonStateOwnerAndBackgroundOrchestrator()
    {
        var persistenceRegistration = File.ReadAllText(Path.Join(
            RepositoryRoot,
            "listenarr.infrastructure",
            "DependencyInjection",
            "Persistence",
            "PersistenceRegistrationExtensions.cs"));
        var startupComposition = File.ReadAllText(Path.Join(
            RepositoryRoot,
            "listenarr.infrastructure",
            "DependencyInjection",
            "InfrastructureStartupCompositionExtensions.cs"));
        var allDependencyInjectionSource = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(
                    Path.Join(RepositoryRoot, "listenarr.infrastructure", "DependencyInjection"),
                    "*.cs",
                    SearchOption.AllDirectories)
                .Where(file => !IsBuildArtifact(file))
                .Select(File.ReadAllText));

        Assert.Single(Regex.Matches(
            allDependencyInjectionSource,
            @"AddSingleton<LibraryFilesystemReadiness>\s*\("));
        Assert.Contains(
            "AddSingleton<ILibraryFilesystemReadiness>",
            persistenceRegistration,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddSingleton<ILibraryFilesystemMutationGate>",
            persistenceRegistration,
            StringComparison.Ordinal);
        Assert.Single(Regex.Matches(
            startupComposition,
            @"AddHostedService<LibraryFilesystemStartupReconciliationService>\s*\("));
        Assert.True(
            startupComposition.IndexOf(
                "AddHostedService<LibraryFilesystemStartupReconciliationService>",
                StringComparison.Ordinal)
            < startupComposition.IndexOf("AddListenarrHostedServices", StringComparison.Ordinal));
    }

    [Fact]
    public void FilesystemStartupArchitectureDocumentation_MatchesReadinessContract()
    {
        var architecture = File.ReadAllText(Path.Join(
            RepositoryRoot,
            "BACKEND_ARCHITECTURE.md"));

        Assert.Contains(
            "Filesystem startup reconciliation is deliberately **not** part of `IsReady`",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "`503 filesystem_initializing`",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "`503 filesystem_initialization_failed`",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "root-folder physical identities, active root relocations, directory ownership, durable audiobook-deletion intents, owner-bound file-rename journals, then audiobook-file identities",
            architecture,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RootDirectoryIdentity_HasNoIntermediateFilesystemEnrollmentCompatibility()
    {
        Assert.False(File.Exists(Path.Join(
            RepositoryRoot,
            "listenarr.infrastructure",
            "FileSystem",
            "ManagedDirectoryEnrollment.cs")));

        var productionRoots = new[]
        {
            Path.Join(RepositoryRoot, "listenarr.application"),
            Path.Join(RepositoryRoot, "listenarr.domain"),
            Path.Join(RepositoryRoot, "listenarr.infrastructure"),
            Path.Join(RepositoryRoot, "listenarr.api")
        };
        var violations = productionRoots
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(file =>
            {
                var source = File.ReadAllText(file);
                return source.Contains("ManagedDirectoryEnrollment", StringComparison.Ordinal)
                    || source.Contains(".listenarr-root-enrollment.json", StringComparison.Ordinal)
                    || source.Contains("UpgradeLegacyAsync", StringComparison.Ordinal);
            })
            .Select(file => Normalize(Path.GetRelativePath(RepositoryRoot, file)))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void LibraryFilesystem_HasNoListenarrScratchNamespaceProtocol()
    {
        var productionRoots = new[]
        {
            Path.Join(RepositoryRoot, "listenarr.application"),
            Path.Join(RepositoryRoot, "listenarr.domain"),
            Path.Join(RepositoryRoot, "listenarr.infrastructure"),
            Path.Join(RepositoryRoot, "listenarr.api")
        };
        var forbidden = new[]
        {
            ".listenarr-",
            "entry.claim"
        };

        var violations = productionRoots
            .SelectMany(root => Directory.EnumerateFiles(
                root,
                "*.cs",
                SearchOption.AllDirectories))
            .Where(file => !IsBuildArtifact(file))
            .Select(file => new
            {
                File = Normalize(Path.GetRelativePath(RepositoryRoot, file)),
                Source = File.ReadAllText(file)
            })
            .SelectMany(candidate => forbidden
                .Where(token => candidate.Source.Contains(token, StringComparison.Ordinal))
                .Select(token => $"{candidate.File}: {token}"))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void LibraryDirectoryOwnership_ProductionDoesNotAdoptExistingDirectories()
    {
        var excludedFiles = new HashSet<string>(StringComparer.Ordinal)
        {
            "listenarr.application/Audiobooks/Contracts/ILibraryDirectoryOwnershipStore.cs",
            "listenarr.infrastructure/Library/Moving/EfLibraryDirectoryOwnershipStore.cs"
        };
        var forbidden = new[]
        {
            ".RecordCreatedAsync(",
            ".ClaimRetainedAsync("
        };
        var productionRoots = new[]
        {
            Path.Join(RepositoryRoot, "listenarr.application"),
            Path.Join(RepositoryRoot, "listenarr.domain"),
            Path.Join(RepositoryRoot, "listenarr.infrastructure"),
            Path.Join(RepositoryRoot, "listenarr.api")
        };

        var violations = productionRoots
            .SelectMany(root => Directory.EnumerateFiles(
                root,
                "*.cs",
                SearchOption.AllDirectories))
            .Where(file => !IsBuildArtifact(file))
            .Select(file => new
            {
                File = Normalize(Path.GetRelativePath(RepositoryRoot, file)),
                Source = File.ReadAllText(file)
            })
            .Where(candidate => !excludedFiles.Contains(candidate.File))
            .SelectMany(candidate => forbidden
                .Where(token => candidate.Source.Contains(token, StringComparison.Ordinal))
                .Select(token => $"{candidate.File}: {token}"))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void DurableMoveBoundary_RequiresExplicitFilesystemSemantics()
    {
        var files = new[]
        {
            "listenarr.application/Audiobooks/Jobs/MoveQueueService.cs",
            "listenarr.application/Audiobooks/RootFolders/RootFolderService.cs",
            "listenarr.infrastructure/Library/Moving/AudiobookContentMoveService.cs",
            "listenarr.infrastructure/Library/Moving/AudiobookContentMoveService.Copy.cs",
            "listenarr.infrastructure/Library/Moving/AudiobookContentMoveService.Manifest.cs",
            "listenarr.infrastructure/Library/Moving/MoveJobProcessor.cs",
            "listenarr.infrastructure/Library/Moving/RootFolderRelocationService.cs",
            "listenarr.infrastructure/Library/Moving/RootFolderRelocationService.Reconciliation.cs"
        };
        var forbidden = new[]
        {
            "FilesystemPathComparerForCurrentOs",
            "AreFilesystemPathsEquivalentForCurrentOs",
            "FileUtils.IsPathSameOrInside",
            ".Replace('\\\\', '/')"
        };

        var violations = files
            .SelectMany(file => forbidden
                .Where(token => File.ReadAllText(Path.Join(RepositoryRoot, file)).Contains(token, StringComparison.Ordinal))
                .Select(token => $"{file}: {token}"))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void RootFolderRelocation_UsesSharedRootFolderPathValidation()
    {
        var serviceFile = Path.Join(
            RepositoryRoot,
            "listenarr.infrastructure",
            "Library",
            "Moving",
            "RootFolderRelocationService.cs");
        var source = File.ReadAllText(serviceFile);

        Assert.Contains(
            "FileUtils.NormalizeRootFolderPathForStorage(command.TargetPath)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ResolveNativeAbsolutePath(command.TargetPath)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ImportDestinationBoundaries_RequireExplicitFilesystemSemantics()
    {
        var files = new[]
        {
            "listenarr.application/Downloads/Import/DownloadImportService.cs",
            "listenarr.application/Downloads/Import/ImportDestinationPlanner.cs",
            "listenarr.api/Features/Downloads/ManualImportController.cs",
            "listenarr.api/Features/Downloads/ManualImportCompanionImporter.cs",
            "listenarr.api/Features/Downloads/ManualImportDestinationTracker.cs",
            "listenarr.api/Features/Downloads/ManualImportPathPlanner.cs",
            "listenarr.domain/Audiobooks/Rules/MultiFileImportPlanner.cs"
        };
        var forbidden = new[]
        {
            "FilesystemPathComparerForCurrentOs",
            "AreFilesystemPathsEquivalentForCurrentOs",
            "FileUtils.IsPathSameOrInside",
            "FileSystemPathSemantics.CurrentHostDefault",
            ".Replace('\\\\', '/')"
        };

        var violations = files
            .SelectMany(file => forbidden
                .Where(token => File.ReadAllText(Path.Join(RepositoryRoot, file))
                    .Contains(token, StringComparison.Ordinal))
                .Select(token => $"{file}: {token}"))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void ScannerAndMetadataBoundaries_RequireExplicitFilesystemSemantics()
    {
        var files = new[]
        {
            "listenarr.infrastructure/Library/Scanning/ScanFileDiscovery.cs",
            "listenarr.infrastructure/Library/Scanning/ScanJobProcessor.cs",
            "listenarr.infrastructure/Library/Scanning/ScanPathPlanner.cs",
            "listenarr.infrastructure/Library/Scanning/UnmatchedScanBackgroundService.cs",
            "listenarr.infrastructure/Library/Scanning/UnmatchedScanProcessor.Grouping.cs",
            "listenarr.infrastructure/Metadata/Parsing/PathMetadataParser.cs"
        };
        var forbidden = new[]
        {
            "FilesystemPathComparerForCurrentOs",
            "AreFilesystemPathsEquivalentForCurrentOs",
            "FileUtils.IsPathSameOrInside",
            "FileSystemPathSemantics.CurrentHostDefault",
            ".Replace('\\\\', '/')"
        };

        var violations = files
            .SelectMany(file => forbidden
                .Where(token => File.ReadAllText(Path.Join(RepositoryRoot, file))
                    .Contains(token, StringComparison.Ordinal))
                .Select(token => $"{file}: {token}"))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void LegacyHostPathIdentity_StaysOnExplicitAllowList()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Domain compatibility helpers intentionally expose host-default utilities for
            // callers that operate on local process paths rather than library-volume identity.
            "listenarr.domain/Common/FileSystemPathIdentity.cs",
            "listenarr.domain/Common/FileUtils.cs",
            "listenarr.domain/Common/FileUtils.PathCombining.cs",
            "listenarr.domain/Common/FileUtils.AudioMatching.cs",

            // Internal application/runtime paths, not user library volume identity.
            "listenarr.application/Configuration/Core/StartupConfigService.cs",
            "listenarr.infrastructure/DependencyInjection/InfrastructureStartupCompositionExtensions.cs",
            "listenarr.infrastructure/Ffmpeg/Installation/FfmpegService.cs",
            "listenarr.infrastructure/FileSystem/FileSystemSafety.cs",
            // Remote path mapping translates client-reported paths to local native paths and
            // uses host-native semantics only to keep relative joins inside the mapped local base.
            "listenarr.infrastructure/Configuration/Paths/RemotePathMappingService.cs",

            // Temporary migration allow-list. Each user-library entry must be removed as its
            // subsystem is moved to explicit FileSystemPathSemantics.
        };
        var forbidden = new[]
        {
            "FilesystemPathComparerForCurrentOs",
            "AreFilesystemPathsEquivalentForCurrentOs",
            "FileUtils.IsPathInsideOf",
            "FileUtils.IsPathSameOrInside",
            "FileSystemPathSemantics.CurrentHostDefault"
        };
        var roots = new[]
        {
            "listenarr.api",
            "listenarr.application",
            "listenarr.domain",
            "listenarr.infrastructure"
        };

        var violations = roots
            .SelectMany(root => Directory.EnumerateFiles(
                Path.Join(RepositoryRoot, root),
                "*.cs",
                SearchOption.AllDirectories))
            .Where(file => !IsBuildArtifact(file))
            .Where(file => !file.Contains(
                $"{Path.DirectorySeparatorChar}Persistence{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Select(file => Normalize(Path.GetRelativePath(RepositoryRoot, file)))
            .SelectMany(file => forbidden
                .Where(token => File.ReadAllText(Path.Join(RepositoryRoot, file)).Contains(token, StringComparison.Ordinal))
                .Select(token => $"{file}: {token}"))
            .Where(violation => !allowed.Contains(violation.Split(':', 2)[0]))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void NamedNonCancelablePhaseTokens_UseSharedRequestCancellationBoundary()
    {
        var roots = new[]
        {
            "listenarr.api",
            "listenarr.application",
            "listenarr.infrastructure"
        };
        var rawPhaseToken = new Regex(
            @"\b(?:mutationToken|commitToken|completionToken)\s*=\s*CancellationToken\.None\b",
            RegexOptions.CultureInvariant);
        var violations = roots
            .SelectMany(root => Directory.EnumerateFiles(
                Path.Join(RepositoryRoot, root),
                "*.cs",
                SearchOption.AllDirectories))
            .Where(file => !IsBuildArtifact(file))
            .Where(file => !file.Contains(
                $"{Path.DirectorySeparatorChar}Persistence{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Where(file => rawPhaseToken.IsMatch(File.ReadAllText(file)))
            .Select(file => Normalize(Path.GetRelativePath(RepositoryRoot, file)))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            violations.Count == 0,
            "Named request-to-noncancelable phase tokens must be entered through "
            + "RequestCancellationBoundary.EnterNonCancelablePhase:" + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    private static void AssertRequiredConstructorParameter<TParameter>(IEnumerable<Type> serviceTypes)
    {
        foreach (var serviceType in serviceTypes)
        {
            var coordinatorParameters = serviceType
                .GetConstructors(System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic)
                .SelectMany(constructor => constructor.GetParameters())
                .Where(parameter => parameter.ParameterType == typeof(TParameter))
                .ToList();

            var coordinatorParameter = Assert.Single(coordinatorParameters);
            Assert.False(coordinatorParameter.IsOptional);
            Assert.False(coordinatorParameter.HasDefaultValue);
        }
    }

    private static void AssertProjectReferences(string relativeProject, IReadOnlyCollection<string> expected)
    {
        var document = XDocument.Load(Path.Join(RepositoryRoot, relativeProject));
        var actual = document
            .Descendants("ProjectReference")
            .Select(element => Normalize(element.Attribute("Include")?.Value ?? string.Empty))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(expected.Order(), actual.Order());
    }

    private static void AssertNoPackages(string relativeProject, IEnumerable<string> forbiddenPackages)
    {
        var document = XDocument.Load(Path.Join(RepositoryRoot, relativeProject));
        var packages = document
            .Descendants("PackageReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => value != null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain(forbiddenPackages, packages.Contains);
    }

    private static void AssertNamespacesMatchFolders(string relativeRoot, string rootNamespace)
    {
        var projectRoot = Path.Join(RepositoryRoot, relativeRoot);
        foreach (var file in Directory.EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildArtifact(file) ||
                string.Equals(Path.GetFileName(file), "GlobalUsings.cs", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(file).StartsWith("Program", StringComparison.OrdinalIgnoreCase) ||
                file.Contains($"{Path.DirectorySeparatorChar}Persistence{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativeDirectory = Path.GetRelativePath(projectRoot, Path.GetDirectoryName(file)!);
            var expectedNamespace = relativeDirectory == "."
                ? rootNamespace
                : $"{rootNamespace}.{ToNamespace(relativeDirectory)}";
            Assert.Equal(expectedNamespace, ReadNamespace(file));
        }
    }

    private static string ReadNamespace(string file)
    {
        var match = Regex.Match(
            File.ReadAllText(file),
            @"^\s*namespace\s+([A-Za-z0-9_.]+)",
            RegexOptions.Multiline);
        Assert.True(match.Success, $"No namespace declaration found in {file}");
        return match.Groups[1].Value;
    }

    private static bool IsBuildArtifact(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        file.Contains($"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        file.Contains($"{Path.DirectorySeparatorChar}wwwroot{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static string ToNamespace(string relativePath) =>
        relativePath
            .Replace(Path.DirectorySeparatorChar, '.')
            .Replace(Path.AltDirectorySeparatorChar, '.');

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static string FindRepositoryRoot() => TestUtils.FindRepositoryRoot();
}
