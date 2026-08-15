using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Architecture;

[Trait("Name", "TestBuilderIdentityContractTests")]
[Trait("Category", "Architecture")]
public sealed class TestBuilderIdentityContractTests : BaseTests
{
    [Fact]
    public void NumericEntityBuilders_GenerateUniqueReservedIdsUnderParallelConstruction()
    {
        const int count = 10_000;
        var builders = GetNumericEntityBuilders()
            .Where(builder => builder.GeneratesId)
            .ToArray();
        var ids = new ConcurrentBag<int>();

        Assert.NotEmpty(builders);
        Parallel.For(0, count, index =>
        {
            var builder = builders[index % builders.Length];
            ids.Add(BuildAndReadId(builder));
        });

        var generatedIds = ids.ToArray();
        var invalidIds = generatedIds
            .Where(id => id <= TestEntityIdGenerator.GeneratedIdFloor)
            .Order()
            .Take(20)
            .ToArray();

        Assert.Equal(count, generatedIds.Length);
        Assert.Equal(count, generatedIds.Distinct().Count());
        Assert.True(
            invalidIds.Length == 0,
            "Generated IDs outside the reserved builder namespace: "
            + string.Join(", ", invalidIds));
    }

    [Fact]
    public void ExplicitFixtureIds_DoNotUseReservedBuilderNamespace()
    {
        var repositoryRoot = FindRepositoryRoot();
        var explicitIdPattern = new Regex(
            @"(?:\.WithId\(\s*|\bId\s*=\s*)(?<id>\d[\d_]*)(?:\s*\)|\s*[,;])",
            RegexOptions.CultureInvariant);
        var violations = Directory
            .EnumerateFiles(
                Path.Join(repositoryRoot, "tests"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(IsSourceFile)
            .SelectMany(path => explicitIdPattern
                .Matches(File.ReadAllText(path))
                .Select(match => new
                {
                    Path = Path.GetRelativePath(repositoryRoot, path),
                    Id = int.Parse(
                        match.Groups["id"].Value.Replace(
                            "_",
                            string.Empty,
                            StringComparison.Ordinal),
                        System.Globalization.CultureInfo.InvariantCulture)
                }))
            .Where(entry => entry.Id >= TestEntityIdGenerator.GeneratedIdFloor)
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ThenBy(entry => entry.Id)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "Explicit fixture IDs may not use the reserved generated-ID namespace:"
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                violations.Select(entry => $"{entry.Path}: {entry.Id}")));
    }

    [Fact]
    public void NumericEntityBuilders_AssignIdsOnlyThroughSharedContract()
    {
        var assignmentPattern = new Regex(
            @"\.Id\s*=\s*(?<value>[^;]+);",
            RegexOptions.CultureInvariant);
        var violations = GetNumericEntityBuilders()
            .SelectMany(builder => assignmentPattern
                .Matches(File.ReadAllText(builder.SourcePath))
                .Select(match => new
                {
                    Builder = builder.BuilderType.FullName,
                    Value = match.Groups["value"].Value.Trim()
                }))
            .Where(assignment => assignment.Value is not
                "TestEntityIdGenerator.Next()"
                and not "TestEntityIdGenerator.Explicit(value)")
            .OrderBy(assignment => assignment.Builder, StringComparer.Ordinal)
            .ThenBy(assignment => assignment.Value, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "Numeric entity builders may assign Id only through TestEntityIdGenerator:"
            + Environment.NewLine
            + string.Join(
                Environment.NewLine,
                violations.Select(violation =>
                    $"{violation.Builder}: {violation.Value}")));
    }

    [Fact]
    public void EntityBuilders_DoNotOwnMutableNumericStaticState()
    {
        var repositoryRoot = FindRepositoryRoot();
        var mutableStaticNumericFieldPattern = new Regex(
            @"\bstatic\s+(?:int|long)\s+\w+\s*(?:=|;)",
            RegexOptions.CultureInvariant);
        var violations = Directory
            .EnumerateFiles(
                Path.Join(repositoryRoot, "tests", "Builders"),
                "*Builder.cs",
                SearchOption.TopDirectoryOnly)
            .Where(path => mutableStaticNumericFieldPattern.IsMatch(
                File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "Entity builders must use TestEntityIdGenerator instead of mutable per-builder numeric state:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void ExplicitIdOverrides_RemainExact()
    {
        var builders = GetNumericEntityBuilders()
            .Where(builder => builder.WithIdMethod != null)
            .ToArray();

        Assert.NotEmpty(builders);
        foreach (var builder in builders)
        {
            var instance = Activator.CreateInstance(builder.BuilderType)
                ?? throw new InvalidOperationException(
                    $"Could not create {builder.BuilderType.FullName}.");
            builder.WithIdMethod!.Invoke(instance, [123]);

            Assert.Equal(123, BuildAndReadId(builder, instance));
        }
    }

    [Fact]
    public void ExplicitIdOverrides_RejectReservedBuilderNamespace()
    {
        var builders = GetNumericEntityBuilders()
            .Where(builder => builder.WithIdMethod != null)
            .ToArray();

        Assert.NotEmpty(builders);
        foreach (var builder in builders)
        {
            var instance = Activator.CreateInstance(builder.BuilderType)
                ?? throw new InvalidOperationException(
                    $"Could not create {builder.BuilderType.FullName}.");
            var exception = Assert.Throws<TargetInvocationException>(() =>
                builder.WithIdMethod!.Invoke(
                    instance,
                    [TestEntityIdGenerator.GeneratedIdFloor]));

            Assert.IsType<ArgumentOutOfRangeException>(exception.InnerException);
        }
    }

    private static NumericBuilderDescriptor[] GetNumericEntityBuilders()
    {
        var buildersRoot = Path.Join(
            FindRepositoryRoot(),
            "tests",
            "Builders");
        return typeof(AudiobookBuilder).Assembly
            .GetTypes()
            .Where(type => type.Namespace == typeof(AudiobookBuilder).Namespace)
            .Where(type => type.Name.EndsWith("Builder", StringComparison.Ordinal))
            .Where(type => type.GetConstructor(Type.EmptyTypes) != null)
            .Select(type => new
            {
                BuilderType = type,
                BuildMethod = type.GetMethod(
                    "Build",
                    BindingFlags.Instance | BindingFlags.Public,
                    binder: null,
                    types: Type.EmptyTypes,
                    modifiers: null)
            })
            .Where(candidate => candidate.BuildMethod != null)
            .Select(candidate => new
            {
                candidate.BuilderType,
                BuildMethod = candidate.BuildMethod!,
                IdProperty = candidate.BuildMethod!.ReturnType.GetProperty(
                    "Id",
                    BindingFlags.Instance | BindingFlags.Public)
            })
            .Where(candidate => candidate.IdProperty?.PropertyType == typeof(int))
            .Select(candidate =>
            {
                var sourcePath = Path.Join(
                    buildersRoot,
                    $"{candidate.BuilderType.Name}.cs");
                var source = File.ReadAllText(sourcePath);
                return new NumericBuilderDescriptor(
                    candidate.BuilderType,
                    candidate.BuildMethod,
                    candidate.IdProperty!,
                    candidate.BuilderType.GetMethod(
                        "WithId",
                        BindingFlags.Instance | BindingFlags.Public,
                        binder: null,
                        types: [typeof(int)],
                        modifiers: null),
                    sourcePath,
                    source.Contains(
                        "TestEntityIdGenerator.Next()",
                        StringComparison.Ordinal));
            })
            .OrderBy(builder => builder.BuilderType.FullName, StringComparer.Ordinal)
            .ToArray();
    }

    private static int BuildAndReadId(
        NumericBuilderDescriptor builder,
        object? instance = null)
    {
        instance ??= Activator.CreateInstance(builder.BuilderType)
            ?? throw new InvalidOperationException(
                $"Could not create {builder.BuilderType.FullName}.");
        var entity = builder.BuildMethod.Invoke(instance, null)
            ?? throw new InvalidOperationException(
                $"{builder.BuilderType.FullName}.Build returned null.");
        return (int)(builder.IdProperty.GetValue(entity)
            ?? throw new InvalidOperationException(
                $"{builder.BuildMethod.ReturnType.FullName}.Id returned null."));
    }

    private static bool IsSourceFile(string path) =>
        !path.Contains(
            $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
        && !path.Contains(
            $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase);

    private sealed record NumericBuilderDescriptor(
        Type BuilderType,
        MethodInfo BuildMethod,
        PropertyInfo IdProperty,
        MethodInfo? WithIdMethod,
        string SourcePath,
        bool GeneratesId);

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Join(current.FullName, "listenarr.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root from the test output directory.");
    }
}
