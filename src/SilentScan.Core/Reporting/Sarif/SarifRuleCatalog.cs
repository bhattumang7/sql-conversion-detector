using SilentScan.Core.Predicates;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Reporting.Sarif;

/// <summary>Stable SARIF rule IDs/descriptions, one per finding kind this tool produces.</summary>
public static class SarifRuleCatalog
{
    public const string DynamicSqlAnalyzedRuleId = "silentscan/dynamic-sql/analyzed";
    public const string DynamicSqlUnanalyzableRuleId = "silentscan/dynamic-sql/unanalyzable";
    public const string DynamicSqlInnerParseFailedRuleId = "silentscan/dynamic-sql/inner-parse-failed";
    public const string ExpressionDerivedRuleId = "silentscan/lineage/expression-derived-column";
    public const string CollationConflictRuleId = "silentscan/verdict/collation-conflict";

    public static string DynamicSqlRuleId(DynamicSqlOutcome outcome) => outcome switch
    {
        DynamicSqlOutcome.AnalyzedLiteral => DynamicSqlAnalyzedRuleId,
        DynamicSqlOutcome.Unanalyzable => DynamicSqlUnanalyzableRuleId,
        DynamicSqlOutcome.InnerParseFailed => DynamicSqlInnerParseFailedRuleId,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unhandled DynamicSqlOutcome."),
    };

    public static string Tier1RuleId(SargabilityFindingKind kind) => kind switch
    {
        SargabilityFindingKind.FunctionWrappedColumn => "silentscan/tier1/function-wrapped-column",
        SargabilityFindingKind.CastOrConvertOnColumn => "silentscan/tier1/cast-or-convert-on-column",
        SargabilityFindingKind.ColumnArithmetic => "silentscan/tier1/column-arithmetic",
        SargabilityFindingKind.LeadingWildcardLike => "silentscan/tier1/leading-wildcard-like",
        SargabilityFindingKind.LikePatternNotLiteral => "silentscan/tier1/like-pattern-not-literal",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled SargabilityFindingKind."),
    };

    public static string VerdictRuleId(Verdict verdict) => verdict switch
    {
        Verdict.ScanForced => "silentscan/verdict/scan-forced",
        Verdict.RangeSeek => "silentscan/verdict/range-seek",
        Verdict.Unknown => "silentscan/verdict/unknown",
        Verdict.SeekPreserved => "silentscan/verdict/seek-preserved",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Unhandled Verdict."),
    };

    public static IReadOnlyList<SarifRule> AllRules { get; } =
    [
        Rule(Tier1RuleId(SargabilityFindingKind.FunctionWrappedColumn), "A column is wrapped in a function call inside a predicate, preventing an index seek."),
        Rule(Tier1RuleId(SargabilityFindingKind.CastOrConvertOnColumn), "A column has CAST/CONVERT applied to it inside a predicate."),
        Rule(Tier1RuleId(SargabilityFindingKind.ColumnArithmetic), "A column has arithmetic applied to it inside a predicate."),
        Rule(Tier1RuleId(SargabilityFindingKind.LeadingWildcardLike), "A LIKE predicate on a column starts with a wildcard, forcing a full scan."),
        Rule(Tier1RuleId(SargabilityFindingKind.LikePatternNotLiteral), "A LIKE predicate's pattern is not a literal, so a leading wildcard can't be ruled out statically."),
        Rule(VerdictRuleId(Verdict.ScanForced), "An implicit type conversion on the column side forces a full scan."),
        Rule(VerdictRuleId(Verdict.RangeSeek), "An implicit type conversion on the column side permits only a dynamic range seek, not a direct seek."),
        Rule(VerdictRuleId(Verdict.Unknown), "A predicate's sargability could not be determined (e.g. unresolved collation) - never guessed."),
        Rule(VerdictRuleId(Verdict.SeekPreserved), "A predicate compares types where the seek is preserved (reported for completeness; not filtered into ScanReportBuilder's actionable findings)."),
        Rule(DynamicSqlAnalyzedRuleId, "A dynamic SQL call site with a provably-constant argument; its contents were reparsed and analyzed like static SQL."),
        Rule(DynamicSqlUnanalyzableRuleId, "A dynamic SQL call site whose argument depends on a variable, parameter, or expression and could not be statically analyzed."),
        Rule(DynamicSqlInnerParseFailedRuleId, "A dynamic SQL call site's argument was provably constant but its reassembled text did not parse as T-SQL."),
        Rule(ExpressionDerivedRuleId, "A predicate compares a column that is a CAST/CONVERT or other computed expression by the time it reaches this statement (introduced in this statement's own derived table, or upstream in a view/TVF's SELECT list) - no index seek is possible regardless of the comparison's types."),
        Rule(CollationConflictRuleId, "Two columns with genuinely different, incompatible collations are compared directly - this does not compile (SQL Server error 468, \"Cannot resolve the collation conflict\"), not a seek/scan question."),
    ];

    private static SarifRule Rule(string id, string description) => new(id, new SarifMessage(description));
}
