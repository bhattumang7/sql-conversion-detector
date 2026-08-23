using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static partial class SecurityScanner
{

    private static readonly string[] CredentialWords = ["password", "passwd", "secret"];

    [GeneratedRegex(@"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|\d+")]
    private static partial Regex WordTokenRegex();

    private static IEnumerable<string> SplitIntoWords(string identifier) =>
        WordTokenRegex().Matches(identifier).Select(m => m.Value);

    private static readonly HashSet<string> WeakHashAlgorithms = new(StringComparer.OrdinalIgnoreCase)
    {
        "MD2", "MD4", "MD5", "SHA", "SHA1",
    };

    private static bool IsBenignIpAddress(string ip)
    {
        if (ip.StartsWith("127.", StringComparison.Ordinal)
            || ip is "0.0.0.0" or "255.255.255.255"
            || ip.StartsWith("192.0.2.", StringComparison.Ordinal)
            || ip.StartsWith("198.51.100.", StringComparison.Ordinal)
            || ip.StartsWith("203.0.113.", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    [GeneratedRegex(@"(?<!\d)(?:\d{1,3}\.){3}\d{1,3}(?!\d)")]
    private static partial Regex IpAddressShapeRegex();

    private static bool TryGetIpAddress(string text, out string ip)
    {
        var match = IpAddressShapeRegex().Match(text);
        if (!match.Success)
        {
            ip = "";
            return false;
        }

        ip = match.Value;
        return ip.Split('.').All(octet => int.TryParse(octet, out var value) && value is >= 0 and <= 255);
    }

    private static bool IsCredentialSuggestiveName(string variableOrColumnName)
    {
        var bare = variableOrColumnName.TrimStart('@');
        return SplitIntoWords(bare).Any(word => CredentialWords.Contains(word, StringComparer.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<SecurityFinding> Scan(SqlParseResult parseResult)
    {
        var visitor = new Visitor(parseResult.SourcePath);
        parseResult.Fragment.Accept(visitor);
        return
        [
            .. visitor.Findings
                .OrderBy(f => f.Kind)
                .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];
    }

    public static IReadOnlyList<SecurityFinding> FromDynamicSqlFindings(IReadOnlyList<DynamicSqlFinding> dynamicSqlFindings) =>
    [
        .. dynamicSqlFindings
            .Where(f => f.Outcome == DynamicSqlOutcome.Unanalyzable)
            .DistinctBy(f => (f.SourcePath, f.Line, f.Column))
            .Select(f => new SecurityFinding(
                SecurityFindingKind.UnprovableDynamicSqlText,
                f.SourcePath, f.Line, f.Column,
                "This dynamic SQL call site's assembled text depends on a variable, parameter, or expression whose value this tool cannot trace - it cannot be shown, from the code alone, to be free of runtime/external influence. Review for injection safety (parameterize via sp_executesql's own @params, or validate/allowlist the value before concatenation).",
                FindingConfidence.Medium))
            .OrderBy(f => f.SourcePath, StringComparer.Ordinal).ThenBy(f => f.Line).ThenBy(f => f.Column),
    ];

    private sealed class Visitor(string sourcePath) : TSqlFragmentVisitor
    {
        public List<SecurityFinding> Findings { get; } = [];

        private bool _inBooleanComparison;

        public override void ExplicitVisit(DeclareVariableStatement node)
        {
            foreach (var element in node.Declarations)
            {
                if (element.Value is StringLiteral && IsCredentialSuggestiveName(element.VariableName.Value))
                {
                    AddCredential(element.VariableName.Value, element.VariableName);
                }

                element.Value?.Accept(this);
            }
        }

        public override void ExplicitVisit(SetVariableStatement node)
        {
            if (node.Expression is StringLiteral && node.Variable is { Name: { } name } && IsCredentialSuggestiveName(name))
            {
                AddCredential(name, node.Variable);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SelectSetVariable node)
        {
            if (node.Expression is StringLiteral && node.Variable is { Name: { } name } && IsCredentialSuggestiveName(name))
            {
                AddCredential(name, node.Variable);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(StringLiteral node)
        {
            if (node.Value is { Length: > 0 } text && TryGetIpAddress(text, out var ip) && !IsBenignIpAddress(ip))
            {
                Findings.Add(new SecurityFinding(
                    SecurityFindingKind.HardCodedIpAddress,
                    sourcePath, node.StartLine, node.StartColumn,
                    $"'{ip}' is a hardcoded IP address embedded in source text - an environment-specific detail that becomes stale, a deployment-coupling smell, and occasionally a genuine indicator of a hardcoded backdoor/debug endpoint. Make sure using it here is safe/intentional.",
                    FindingConfidence.High));
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(BooleanComparisonExpression node)
        {
            _inBooleanComparison = true;
            node.FirstExpression?.Accept(this);
            node.SecondExpression?.Accept(this);
            _inBooleanComparison = false;
        }

        public override void ExplicitVisit(FunctionCall node)
        {
            if (string.Equals(node.FunctionName?.Value, "HASHBYTES", StringComparison.OrdinalIgnoreCase)
                && node.Parameters is [StringLiteral { Value: { } algorithm }, ..] parameters
                && WeakHashAlgorithms.Contains(algorithm))
            {
                var sensitive = _inBooleanComparison
                    || (parameters.Count > 1 && IsCredentialSuggestiveOperand(parameters[1]));

                Findings.Add(new SecurityFinding(
                    sensitive ? SecurityFindingKind.WeakHashAlgorithmInSensitiveContext : SecurityFindingKind.WeakHashAlgorithm,
                    sourcePath, node.StartLine, node.StartColumn,
                    sensitive
                        ? $"HASHBYTES('{algorithm}', ...) uses a cryptographically broken/deprecated algorithm in what looks like a security-sensitive context (a credential-named value, or a direct comparison) - use SHA2_256 or SHA2_512 instead."
                        : $"HASHBYTES('{algorithm}', ...) uses a cryptographically broken/deprecated algorithm - fine for a non-security checksum/dedup use, but prefer SHA2_256/SHA2_512 if this value has any security purpose.",
                    sensitive ? FindingConfidence.Medium : FindingConfidence.High));
            }

            base.ExplicitVisit(node);
        }

        private void AddCredential(string variableName, TSqlFragment site) =>
            Findings.Add(new SecurityFinding(
                SecurityFindingKind.HardCodedCredential,
                sourcePath, site.StartLine, site.StartColumn,
                $"'{variableName}' looks like it holds a credential and is assigned a literal string directly in source text - keep credentials in a secrets store or external configuration, never embedded in a script.",
                FindingConfidence.Low));

        private static bool IsCredentialSuggestiveOperand(ScalarExpression expression) => expression switch
        {
            VariableReference v => IsCredentialSuggestiveName(v.Name),
            ColumnReferenceExpression { MultiPartIdentifier.Identifiers: [.., { } last] } => IsCredentialSuggestiveName(last.Value),
            _ => false,
        };
    }
}
