using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 4 "Security" - see <see cref="SecurityFinding"/> for the full
/// scope, precision-guard, and severity documentation.
/// </summary>
public static partial class SecurityScanner
{
    // Independently chosen, generic credential-suggestive words - not copied from any third-party
    // tool's own word list. Matched as a WHOLE WORD (camelCase/PascalCase/underscore-delimited
    // token, never a bare substring - see SplitIntoWords) against a variable's own name.
    //
    // Two real false positives caught by spot-checking real findings against real module text
    // before shipping, not by unit tests alone, and both load-bearing for this list's final shape:
    // a bare substring match first fired on `@VehInOpWD` ("Operating WeekDays") and `@DaysOpWD`
    // purely because "OpWD" happens to CONTAIN the letters "pwd" - fixed by requiring a full
    // word-token match instead of a substring one (see SplitIntoWords). That alone was not enough:
    // `@GetPWDTrips` (a real paratransit-domain term - "Persons/People With Disabilities" trips,
    // nothing to do with a password) still tokenizes "PWD" as its own whole word, so a bare 3-letter
    // "pwd" abbreviation is inherently too ambiguous across domains to include even as a whole-word
    // match - deliberately DROPPED from this list for that reason, keeping only the unambiguous
    // full spellings.
    private static readonly string[] CredentialWords = ["password", "passwd", "secret"];

    // Splits an identifier into its camelCase/PascalCase/underscore-delimited word tokens, e.g.
    // "dbPassword" -> ["db", "Password"], "My_Secret_Key" -> ["My", "Secret", "Key"]. Never treats
    // a mid-word letter run as its own token, which is exactly what made the bare-substring
    // approach above false-positive on "OpWD"/"GetPWDTrips".
    [GeneratedRegex(@"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|\d+")]
    private static partial Regex WordTokenRegex();

    private static IEnumerable<string> SplitIntoWords(string identifier) =>
        WordTokenRegex().Matches(identifier).Select(m => m.Value);

    // MD2/MD4/MD5/SHA(=SHA-0)/SHA1 - cryptographically broken/deprecated per NIST SP 800-131A and
    // OWASP's own published guidance, independently of any one vendor's tool. SHA2_256/SHA2_512 are
    // HASHBYTES's other two supported algorithms and are deliberately not included here.
    private static readonly HashSet<string> WeakHashAlgorithms = new(StringComparer.OrdinalIgnoreCase)
    {
        "MD2", "MD4", "MD5", "SHA", "SHA1",
    };

    // A benign IPv4 address is never worth flagging: loopback (127.0.0.0/8), the all-zeros/
    // all-ones addresses, and the three IANA-reserved (RFC 5737) TEST-NET documentation ranges
    // meant specifically for examples/docs. Independently derived from public IANA allocations, not
    // copied from any third party's own exclusion list.
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

    /// <summary>Derives <see cref="SecurityFindingKind.UnprovableDynamicSqlText"/> findings from the
    /// dynamic-SQL pipeline's own already-computed <see cref="DynamicSqlOutcome.Unanalyzable"/>
    /// classification - one finding per call site whose assembled SQL text this tool could not
    /// prove is free of runtime/external influence. Reuses the existing, already-oracle-backed
    /// pipeline rather than duplicating its reaching-definitions machinery.
    ///
    /// Deduplicated by (<see cref="DynamicSqlFinding.SourcePath"/>, Line, Column): the source
    /// pipeline's own multi-round reparse fixpoint loop can revisit and re-report the same
    /// unanalyzable call site several times across rounds (a real, measured effect against the
    /// local test database - up to 18x for one call site) - that repetition is meaningful for the
    /// pipeline's own internal bookkeeping but would just be noise repeated here, so this consumer
    /// collapses it to one finding per real call site rather than propagating it.</summary>
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
