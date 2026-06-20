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
            "Controllers/AdminMetadataController.cs",
            "Controllers/DiscordController.cs",
            "Controllers/FfmpegController.cs",
            "Controllers/FileSystemController.cs",
            "Controllers/ImageCachedPathValidator.cs",
            "Controllers/ImagePlaceholderResolver.cs",
            "Controllers/ImagesController.cs",
            "Controllers/LibraryBulkEditWorkflow.cs",
            "Controllers/LibraryDeleteWorkflow.cs",
            "Controllers/LibraryManualScanWorkflow.cs",
            "Controllers/LibraryMoveWorkflow.cs",
            "Controllers/LibraryPathPlanner.cs",
            "Controllers/ManualImportCompanionImporter.cs",
            "Controllers/ManualImportController.cs",
            "Controllers/RootFoldersController.cs",
            "Controllers/SystemController.cs",
            "Program.Testing.cs",
            "Startup/ListenarrBuilderFactory.cs",
            "Startup/ListenarrSecurityStartup.cs",
            "Startup/ListenarrStaticAssetsStartup.cs",
            "Startup/ListenarrSwaggerRegistration.cs"
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
    public void ExternalHttpClients_HaveOneRegistrationOwner()
    {
        var registrationFiles = new[]
        {
            Path.Join(RepositoryRoot, "listenarr.api", "Startup", "ListenarrWorkflowRegistration.cs"),
            Path.Join(RepositoryRoot, "listenarr.infrastructure", "Extensions", "ServiceRegistrationExtensions.cs"),
            Path.Join(RepositoryRoot, "listenarr.infrastructure", "Extensions", "InfrastructureStartupCompositionExtensions.cs")
        };
        var source = string.Join(Environment.NewLine, registrationFiles.Select(File.ReadAllText));

        Assert.Single(Regex.Matches(source, "AddHttpClient\\(\\\"us\\\"\\)"));
        Assert.Single(Regex.Matches(source, "AddHttpClient<AudibleService>"));
        Assert.Single(Regex.Matches(source, "AddHttpClient<(?:IAudnexusService,\\s*)?AudnexusService>"));
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
