using System.Text.Json.Serialization;
using SilentScan.Core.TypeInference;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public enum AlterColumnSafetyKind
{
    PrecisionOrScaleNarrowing,
    IncompatibleFamilyConversion,
    TemporalOffsetDropped,
}

public sealed record AlterColumnSafetyFinding(
    string TableQualifiedName,
    string ColumnName,
    AlterColumnSafetyKind Kind,
    SqlType PreviousType,
    SqlType NewType,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.AlterColumnSafetyRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, 1);
}
