using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Listenarr.Tests.Features.Architecture;

internal sealed record ReleaseIdentityOwnershipViolation(
    int Line,
    string Reason);

/// <summary>
/// Finds production code that works out a release blocklist key for itself instead of asking
/// <c>ReleaseIdentity</c> for one.
///
/// The release blocklist has had four defects and they were all the same defect: an identity
/// derived in one place and re-derived differently in another, so the row written when a release
/// failed was looked up under a key nothing ever computed again. The last of them cost a live
/// install an indexer termination warning while a correctly formatted blocklist row for the book
/// sat in the database unmatched.
///
/// <c>BlockedReleaseFilter</c> carries a comment saying the field-picking belongs to
/// <c>ReleaseIdentity</c> and nowhere else. A comment is advice. This is the same statement in a
/// form that fails a build.
/// </summary>
internal static class ReleaseIdentitySourceAnalyzer
{
    /// <summary>
    /// The scheme prefixes a blocklist key is written with. ReleaseIdentity decides the wire
    /// format of a key, so a literal starting with one of these anywhere else is a second author
    /// of that format. Only a leading match counts: "xt=urn:btih:" inside a magnet URI is a
    /// magnet, not a key.
    /// </summary>
    private static readonly string[] KeySchemePrefixes = ["btih:", "name:", "url:"];

    /// <summary>The metadata slot the grab stamps the identity into; ReleaseIdentity.MetadataKey names it.</summary>
    private const string MetadataSlotName = "ReleaseIdentity";

    /// <summary>
    /// The parts of a release that an identity could be built out of. Two of these combined, or
    /// one of them hashed, is somebody deriving a key.
    /// </summary>
    private static readonly HashSet<string> ReleaseFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Title",
        "Size",
        "TotalSize",
        "ExpectedFileSize",
        "MagnetLink",
        "NzbUrl",
        "TorrentUrl",
        "SourceLink",
        "OriginalUrl",
        "DownloadUrl",
        "ReleaseUrl",
        "InfoHash",
        "TorrentHash",
        "TorrentInfoHash",
        "IndexerId",
        "ReleaseIdentifier"
    };

    private static readonly HashSet<string> HashingMethodNames = new(StringComparer.Ordinal)
    {
        "HashData",
        "ComputeHash",
        "ToHexString",
        "GetHashCode"
    };

    private static readonly HashSet<string> CompositionMethodNames = new(StringComparer.Ordinal)
    {
        "Join",
        "Concat",
        "Format"
    };

    /// <summary>Calls that take a release key as an argument, so an argument to one is a key.</summary>
    private static readonly HashSet<string> IdentitySinkMethodNames = new(StringComparer.Ordinal)
    {
        "BlockAsync",
        "SetMetadata",
        "GetMetadataString",
        "GetBlockedIdentifiersAsync"
    };

    private static readonly Regex IdentityNamePattern = new(
        "identit|identif|releasekey|blocklistkey|blockkey",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex BlocklistReceiverPattern = new(
        "blocked|blocklist",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<ReleaseIdentityOwnershipViolation> Analyze(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var tree = CSharpSyntaxTree.ParseText(
            source,
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));
        var root = tree.GetRoot();
        var violations = new List<ReleaseIdentityOwnershipViolation>();

        foreach (var node in root.DescendantNodes())
        {
            var reason = DescribeViolation(node);
            if (reason != null)
            {
                violations.Add(new ReleaseIdentityOwnershipViolation(
                    tree.GetLineSpan(node.Span).StartLinePosition.Line + 1,
                    reason));
            }
        }

        return violations
            .DistinctBy(violation => (violation.Line, violation.Reason))
            .OrderBy(violation => violation.Line)
            .ThenBy(violation => violation.Reason, StringComparer.Ordinal)
            .ToArray();
    }

    private static string? DescribeViolation(SyntaxNode node)
    {
        var mintedScheme = DescribeMintedKeyScheme(node);
        if (mintedScheme != null)
        {
            return mintedScheme;
        }

        if (node is LiteralExpressionSyntax literal
            && literal.IsKind(SyntaxKind.StringLiteralExpression)
            && string.Equals(literal.Token.ValueText, MetadataSlotName, StringComparison.Ordinal))
        {
            return "spells the release identity metadata slot as a literal; "
                + "address it through ReleaseIdentity.MetadataKey";
        }

        if (IsHashOfReleaseFields(node))
        {
            var sink = DescribeSink(node);
            return sink == null
                ? null
                : $"hashes release fields into a value {sink}; "
                    + "ask ReleaseIdentity for the key instead of hashing one here";
        }

        if (IsCompositionRoot(node))
        {
            var fields = CountDistinctReleaseFields(node);
            if (fields < 2)
            {
                return null;
            }

            var sink = DescribeSink(node);
            return sink == null
                ? null
                : $"combines {fields} release fields into a value {sink}; "
                    + "ask ReleaseIdentity for the key instead of picking fields here";
        }

        return null;
    }

    private static string? DescribeMintedKeyScheme(SyntaxNode node)
    {
        var leading = node switch
        {
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) =>
                literal.Token.ValueText,
            InterpolatedStringExpressionSyntax interpolated =>
                interpolated.Contents.FirstOrDefault() is InterpolatedStringTextSyntax text
                    ? text.TextToken.ValueText
                    : null,
            _ => null
        };
        if (leading == null)
        {
            return null;
        }

        var prefix = KeySchemePrefixes.FirstOrDefault(
            candidate => leading.StartsWith(candidate, StringComparison.Ordinal));
        return prefix == null
            ? null
            : $"writes a blocklist key prefix (\"{prefix}\") of its own; "
                + "ReleaseIdentity owns the key format, so call it rather than reproducing it";
    }

    private static bool IsHashOfReleaseFields(SyntaxNode node)
    {
        if (node is not InvocationExpressionSyntax invocation)
        {
            return false;
        }

        if (!HashingMethodNames.Contains(InvokedName(invocation.Expression)))
        {
            return false;
        }

        // Hashing is usually written nested, Convert.ToHexString(SHA256.HashData(...)), and one
        // key being minted should read as one violation rather than as one per layer.
        if (invocation.Ancestors()
            .OfType<InvocationExpressionSyntax>()
            .Any(outer => HashingMethodNames.Contains(InvokedName(outer.Expression))))
        {
            return false;
        }

        return CountDistinctReleaseFields(invocation.ArgumentList) >= 1;
    }

    /// <summary>
    /// The outermost node of a string built out of parts: an interpolation, a "+" chain, or a
    /// string.Join/Concat/Format. Nested "+" operands are skipped so one chain reports once.
    /// </summary>
    private static bool IsCompositionRoot(SyntaxNode node)
    {
        switch (node)
        {
            case InterpolatedStringExpressionSyntax:
                return node.Parent is not InterpolatedStringExpressionSyntax;
            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression):
                return binary.Parent is not BinaryExpressionSyntax parent
                    || !parent.IsKind(SyntaxKind.AddExpression);
            case InvocationExpressionSyntax invocation:
                return CompositionMethodNames.Contains(InvokedName(invocation.Expression))
                    && ReceiverName(invocation.Expression) is "string" or "String";
            default:
                return false;
        }
    }

    private static int CountDistinctReleaseFields(SyntaxNode node) =>
        node.DescendantNodesAndSelf()
            .Select(candidate => candidate switch
            {
                MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                IdentifierNameSyntax identifier when identifier.Parent is not MemberAccessExpressionSyntax =>
                    identifier.Identifier.ValueText,
                _ => null
            })
            .Where(name => name != null && ReleaseFieldNames.Contains(name))
            .Select(name => name!.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Count();

    /// <summary>
    /// Where the composed value ends up, if that place is a release identity. Everything else in
    /// the codebase concatenates a title and a size for a log line or a message, and none of that
    /// is this defect, so a composition only counts once it reaches an identity.
    /// </summary>
    private static string? DescribeSink(SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case VariableDeclaratorSyntax declarator
                    when IdentityNamePattern.IsMatch(declarator.Identifier.ValueText):
                    return $"held in '{declarator.Identifier.ValueText}'";

                case AssignmentExpressionSyntax assignment
                    when IdentityNamePattern.IsMatch(TargetName(assignment.Left)):
                    return $"assigned to '{TargetName(assignment.Left)}'";

                case ArgumentSyntax argument when DescribeArgumentSink(argument) is { } argumentSink:
                    return argumentSink;

                case PropertyDeclarationSyntax property
                    when IdentityNamePattern.IsMatch(property.Identifier.ValueText):
                    return $"returned as '{property.Identifier.ValueText}'";

                case MethodDeclarationSyntax method:
                    return IdentityNamePattern.IsMatch(method.Identifier.ValueText)
                        ? $"returned from '{method.Identifier.ValueText}'"
                        : null;

                case LocalFunctionStatementSyntax local:
                    return IdentityNamePattern.IsMatch(local.Identifier.ValueText)
                        ? $"returned from '{local.Identifier.ValueText}'"
                        : null;
            }
        }

        return null;
    }

    private static string? DescribeArgumentSink(ArgumentSyntax argument)
    {
        if (argument.NameColon is { } named && IdentityNamePattern.IsMatch(named.Name.Identifier.ValueText))
        {
            return $"passed as '{named.Name.Identifier.ValueText}'";
        }

        if (argument.Parent?.Parent is not InvocationExpressionSyntax invocation)
        {
            return null;
        }

        var invoked = InvokedName(invocation.Expression);
        if (IdentitySinkMethodNames.Contains(invoked))
        {
            return $"passed to {invoked}";
        }

        var receiver = ReceiverName(invocation.Expression);
        return receiver != null && BlocklistReceiverPattern.IsMatch(receiver)
            ? $"passed to {receiver}.{invoked}"
            : null;
    }

    private static string TargetName(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        _ => expression.ToString()
    };

    private static string InvokedName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        _ => expression.ToString()
    };

    private static string? ReceiverName(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax member => TargetName(member.Expression),
        _ => null
    };
}
