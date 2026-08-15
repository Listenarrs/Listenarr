using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Listenarr.Tests.Features.Architecture;

internal sealed record TestEvidenceViolation(
    string MethodName,
    int Line,
    string Reason);

internal static class TestEvidenceSourceAnalyzer
{
    public static IReadOnlyList<TestEvidenceViolation> Analyze(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var tree = CSharpSyntaxTree.ParseText(
            source,
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));
        var root = tree.GetRoot();
        var violations = new List<TestEvidenceViolation>();
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (!IsTestMethod(method))
            {
                continue;
            }

            foreach (var returnStatement in method.DescendantNodes()
                         .OfType<ReturnStatementSyntax>()
                         .Where(statement => statement.Expression == null)
                         .Where(statement => ExitsMethod(statement, method)))
            {
                var reason = returnStatement.Ancestors()
                    .TakeWhile(ancestor => ancestor != method)
                    .Any(ancestor => ancestor is CatchClauseSyntax)
                        ? "catches a setup/capability failure and silently returns"
                        : DescribeConditionalReturn(returnStatement, method);
                violations.Add(new TestEvidenceViolation(
                    method.Identifier.ValueText,
                    tree.GetLineSpan(returnStatement.Span).StartLinePosition.Line + 1,
                    reason));
            }
        }

        return violations;
    }

    private static bool IsTestMethod(MethodDeclarationSyntax method) =>
        method.AttributeLists
            .SelectMany(list => list.Attributes)
            .Select(attribute => GetAttributeName(attribute.Name))
            .Any(IsTestAttributeName);

    private static bool IsTestAttributeName(string name) =>
        name.EndsWith("Fact", StringComparison.Ordinal)
        || name.EndsWith("FactAttribute", StringComparison.Ordinal)
        || name.EndsWith("Theory", StringComparison.Ordinal)
        || name.EndsWith("TheoryAttribute", StringComparison.Ordinal);

    private static string GetAttributeName(NameSyntax name) => name switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        QualifiedNameSyntax qualified => GetAttributeName(qualified.Right),
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        _ => name.ToString().Split('.').Last()
    };

    private static bool ExitsMethod(
        ReturnStatementSyntax statement,
        MethodDeclarationSyntax method)
    {
        var nearestFunction = statement.Ancestors()
            .FirstOrDefault(ancestor => ancestor is
                MethodDeclarationSyntax or LocalFunctionStatementSyntax or
                AnonymousFunctionExpressionSyntax);
        return ReferenceEquals(nearestFunction, method);
    }

    private static string DescribeConditionalReturn(
        ReturnStatementSyntax statement,
        MethodDeclarationSyntax method)
    {
        var conditional = statement.Ancestors()
            .TakeWhile(ancestor => ancestor != method)
            .OfType<IfStatementSyntax>()
            .FirstOrDefault();
        if (conditional == null)
        {
            return "silently returns before proving its assertions";
        }

        var conditionText = conditional.Condition.ToString();
        if (conditionText.Contains("OperatingSystem.Is", StringComparison.Ordinal)
            || conditionText.Contains(
                "RuntimeInformation.IsOSPlatform",
                StringComparison.Ordinal))
        {
            return "uses an operating-system guard that reports a pass instead of a platform skip";
        }

        if (conditional.Condition.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Select(invocation => GetInvokedName(invocation.Expression))
            .Any(IsCapabilityProbeName))
        {
            return "uses a capability probe that reports a pass when the capability is unavailable";
        }

        return "conditionally returns before proving its assertions";
    }

    private static string GetInvokedName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        _ => expression.ToString()
    };

    private static bool IsCapabilityProbeName(string name) =>
        name.StartsWith("TryCreate", StringComparison.Ordinal)
        || name.StartsWith("TryEnable", StringComparison.Ordinal)
        || name.StartsWith("CanCreate", StringComparison.Ordinal)
        || name.StartsWith("CanUse", StringComparison.Ordinal)
        || name.StartsWith("Supports", StringComparison.Ordinal)
        || name.StartsWith("IsSupported", StringComparison.Ordinal)
        || name.StartsWith("IsAvailable", StringComparison.Ordinal)
        || name.StartsWith("HasCapability", StringComparison.Ordinal);
}
