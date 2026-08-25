using SilentScan.Core.Rules;
namespace SilentScan.Core.Predicates;

public enum DynamicSqlOutcome
{
    AnalyzedLiteral,

    Unanalyzable,

    InnerParseFailed,

    PartiallyAnalyzed,
}

public sealed record DynamicSqlFinding(string SourcePath, int Line, int Column, DynamicSqlOutcome Outcome, string? Reason)
{
    public string RuleId { get; } = FindingRuleIds.DynamicSqlRuleId(Outcome);
}

