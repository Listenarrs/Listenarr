using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Listenarr.Tests.Features.Architecture;

internal sealed record NativeCapabilityAttributeViolation(
    string SourcePath,
    string MethodName,
    int Line,
    string MissingCapability);

internal static class NativeCapabilityTestSourceAnalyzer
{
    public static IReadOnlyList<NativeCapabilityAttributeViolation> Analyze(
        string source) =>
        AnalyzeSources(new[] { new NativeCapabilitySource(string.Empty, source) });

    public static IReadOnlyList<NativeCapabilityAttributeViolation> AnalyzeSources(
        IEnumerable<NativeCapabilitySource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var parsedSources = sources
            .Select(source => new ParsedSource(
                source.Path,
                CSharpSyntaxTree.ParseText(
                    source.Source,
                    CSharpParseOptions.Default.WithLanguageVersion(
                        LanguageVersion.Latest),
                    path: source.Path)))
            .ToArray();
        var types = parsedSources
            .SelectMany(source => source.Tree.GetRoot()
                .DescendantNodes()
                .OfType<TypeDeclarationSyntax>()
                .Select(type => new TypePart(source.Path, source.Tree, type)))
            .GroupBy(part => GetTypeKey(part.Type), StringComparer.Ordinal)
            .Select(group => new AnalyzedType(
                group.Key,
                group.First().Type.Identifier.ValueText,
                group.SelectMany(part => part.Type.Members
                        .OfType<MethodDeclarationSyntax>()
                        .Select(method => new MethodPart(
                            part.Path,
                            part.Tree,
                            method)))
                    .ToArray()))
            .ToArray();
        var typesByReference = types
            .SelectMany(type => new[]
            {
                new KeyValuePair<string, AnalyzedType>(type.Key, type),
                new KeyValuePair<string, AnalyzedType>(type.SimpleName, type)
            })
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(pair => pair.Value).Distinct().ToArray(),
                StringComparer.Ordinal);
        var violations = new List<NativeCapabilityAttributeViolation>();

        foreach (var type in types)
        {
            var methodsByName = GroupMethodsByName(type.Methods);

            foreach (var method in type.Methods.Where(part => IsTestMethod(part.Method)))
            {
                var capabilities = FindCapabilities(
                    method,
                    methodsByName,
                    typesByReference,
                    new HashSet<MethodDeclarationSyntax>());
                var attributes = method.Method.AttributeLists
                    .SelectMany(list => list.Attributes)
                    .Select(attribute => GetAttributeName(attribute.Name))
                    .ToArray();
                var line = method.Tree.GetLineSpan(method.Method.Identifier.Span)
                    .StartLinePosition.Line + 1;

                if (capabilities.Contains(NativeCapabilityKind.DirectoryLinks)
                    && !attributes.Any(DeclaresDirectoryLinkCapability))
                {
                    violations.Add(new NativeCapabilityAttributeViolation(
                        method.Path,
                        method.Method.Identifier.ValueText,
                        line,
                        "directory symbolic links"));
                }

                if (capabilities.Contains(NativeCapabilityKind.FileLinks)
                    && !attributes.Any(DeclaresFileLinkCapability))
                {
                    violations.Add(new NativeCapabilityAttributeViolation(
                        method.Path,
                        method.Method.Identifier.ValueText,
                        line,
                        "file symbolic links"));
                }
            }
        }

        return violations;
    }

    private static HashSet<NativeCapabilityKind> FindCapabilities(
        MethodPart method,
        IReadOnlyDictionary<string, MethodPart[]> methodsByName,
        IReadOnlyDictionary<string, AnalyzedType[]> typesByReference,
        HashSet<MethodDeclarationSyntax> visited)
    {
        if (!visited.Add(method.Method))
        {
            return [];
        }

        var capabilities = new HashSet<NativeCapabilityKind>();
        foreach (var invocation in method.Method.DescendantNodes()
                     .OfType<InvocationExpressionSyntax>())
        {
            if (TryGetSymbolicLinkCapability(invocation, out var capability))
            {
                capabilities.Add(capability);
                continue;
            }

            if (IsCapabilityPolicyDeclaration(invocation.Expression))
            {
                continue;
            }

            var invokedName = GetLocalInvokedName(invocation.Expression);
            if (invokedName != null
                && methodsByName.TryGetValue(invokedName, out var invokedMethods))
            {
                foreach (var invokedMethod in invokedMethods)
                {
                    capabilities.UnionWith(FindCapabilities(
                        invokedMethod,
                        methodsByName,
                        typesByReference,
                        visited));
                }

                continue;
            }

            if (TryGetQualifiedInvocationTarget(
                    invocation.Expression,
                    out var typeReference,
                    out var methodName)
                && typesByReference.TryGetValue(
                    typeReference,
                    out var referencedTypes))
            {
                foreach (var referencedType in referencedTypes)
                {
                    var referencedMethods = GroupMethodsByName(
                        referencedType.Methods);
                    if (!referencedMethods.TryGetValue(
                            methodName,
                            out var referencedInvokedMethods))
                    {
                        continue;
                    }

                    foreach (var referencedInvokedMethod in referencedInvokedMethods)
                    {
                        capabilities.UnionWith(FindCapabilities(
                            referencedInvokedMethod,
                            referencedMethods,
                            typesByReference,
                            visited));
                    }
                }
            }
        }

        return capabilities;
    }

    private static bool TryGetSymbolicLinkCapability(
        InvocationExpressionSyntax invocation,
        out NativeCapabilityKind capability)
    {
        capability = default;
        if (invocation.Expression is not MemberAccessExpressionSyntax member
            || member.Name.Identifier.ValueText != "CreateSymbolicLink")
        {
            return false;
        }

        var owner = member.Expression.ToString();
        if (owner == "Directory"
            || owner.EndsWith(".Directory", StringComparison.Ordinal))
        {
            capability = NativeCapabilityKind.DirectoryLinks;
            return true;
        }

        if (owner == "File"
            || owner.EndsWith(".File", StringComparison.Ordinal))
        {
            capability = NativeCapabilityKind.FileLinks;
            return true;
        }

        return false;
    }

    private static bool IsCapabilityPolicyDeclaration(
        ExpressionSyntax expression) =>
        expression is MemberAccessExpressionSyntax member
        && member.Name.Identifier.ValueText is
            "GetExecutionDecision" or "RequireAvailable"
        && (member.Expression.ToString() == "NativeTestCapabilityPolicy"
            || member.Expression.ToString().EndsWith(
                ".NativeTestCapabilityPolicy",
                StringComparison.Ordinal));

    private static bool IsTestMethod(MethodDeclarationSyntax method) =>
        method.AttributeLists
            .SelectMany(list => list.Attributes)
            .Select(attribute => GetAttributeName(attribute.Name))
            .Any(name => name.EndsWith("Fact", StringComparison.Ordinal)
                || name.EndsWith("FactAttribute", StringComparison.Ordinal)
                || name.EndsWith("Theory", StringComparison.Ordinal)
                || name.EndsWith("TheoryAttribute", StringComparison.Ordinal));

    private static bool DeclaresDirectoryLinkCapability(string attributeName) =>
        attributeName.Contains("DirectoryLink", StringComparison.Ordinal)
        || attributeName.Contains(
            "DirectoryAndFileLink",
            StringComparison.Ordinal);

    private static bool DeclaresFileLinkCapability(string attributeName) =>
        attributeName.Contains("FileLink", StringComparison.Ordinal)
        || attributeName.Contains(
            "DirectoryAndFileLink",
            StringComparison.Ordinal);

    private static string GetTypeKey(TypeDeclarationSyntax type)
    {
        var namespaceName = type.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Select(namespaceDeclaration => namespaceDeclaration.Name.ToString())
            .Reverse();
        var containingTypes = type.Ancestors()
            .OfType<TypeDeclarationSyntax>()
            .Select(containingType => containingType.Identifier.ValueText)
            .Reverse();
        return string.Join(
            ".",
            namespaceName
                .Concat(containingTypes)
                .Append(type.Identifier.ValueText));
    }

    private static string GetAttributeName(NameSyntax name) => name switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        QualifiedNameSyntax qualified => GetAttributeName(qualified.Right),
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        _ => name.ToString().Split('.').Last()
    };

    private static IReadOnlyDictionary<string, MethodPart[]> GroupMethodsByName(
        IEnumerable<MethodPart> methods) =>
        methods
            .GroupBy(
                part => part.Method.Identifier.ValueText,
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.Ordinal);

    private static string? GetLocalInvokedName(ExpressionSyntax expression) =>
        expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            MemberAccessExpressionSyntax
            {
                Expression: ThisExpressionSyntax,
                Name: SimpleNameSyntax name
            } => name.Identifier.ValueText,
            _ => null
        };

    private static bool TryGetQualifiedInvocationTarget(
        ExpressionSyntax expression,
        out string typeReference,
        out string methodName)
    {
        typeReference = string.Empty;
        methodName = string.Empty;
        if (expression is not MemberAccessExpressionSyntax member
            || member.Expression is ThisExpressionSyntax)
        {
            return false;
        }

        typeReference = member.Expression.ToString();
        methodName = member.Name.Identifier.ValueText;
        return typeReference.Length > 0 && methodName.Length > 0;
    }

    internal readonly record struct NativeCapabilitySource(
        string Path,
        string Source);

    private readonly record struct ParsedSource(
        string Path,
        SyntaxTree Tree);

    private readonly record struct TypePart(
        string Path,
        SyntaxTree Tree,
        TypeDeclarationSyntax Type);

    private readonly record struct MethodPart(
        string Path,
        SyntaxTree Tree,
        MethodDeclarationSyntax Method);

    private sealed record AnalyzedType(
        string Key,
        string SimpleName,
        MethodPart[] Methods);

    private enum NativeCapabilityKind
    {
        DirectoryLinks,
        FileLinks
    }
}
