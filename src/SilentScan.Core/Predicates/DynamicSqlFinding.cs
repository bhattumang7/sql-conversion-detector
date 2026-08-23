namespace SilentScan.Core.Predicates;

public enum DynamicSqlOutcome
{
AnalyzedLiteral,

Unanalyzable,

InnerParseFailed,

PartiallyAnalyzed,
}

public sealed record DynamicSqlFinding(string SourcePath, int Line, int Column, DynamicSqlOutcome Outcome, string? Reason);
