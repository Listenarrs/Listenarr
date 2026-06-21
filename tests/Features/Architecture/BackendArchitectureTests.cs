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

namespace Listenarr.Tests.Features.Architecture;

public sealed class BackendArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

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
        file.Contains($"{Path.DirectorySeparatorChar}wwwroot{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static string ToNamespace(string relativePath) =>
        relativePath
            .Replace(Path.DirectorySeparatorChar, '.')
            .Replace(Path.AltDirectorySeparatorChar, '.');

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Join(directory.FullName, "listenarr.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Unable to locate repository root from {AppContext.BaseDirectory}");
    }
}
