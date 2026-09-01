using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public sealed record CursorCloseOnCommitFinding(
    [property: JsonIgnore] string SourcePath,
    string CursorName,
    int OpenLine,
    int OpenColumn,
    int ClosingStatementLine,
    int ClosingStatementColumn,
    bool ClosedByRollback,
    int FetchLine,
    int FetchColumn,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.CursorCloseOnCommitRuleId;

    public SourceSpan Location => new(SourcePath, FetchLine, FetchColumn);
}
