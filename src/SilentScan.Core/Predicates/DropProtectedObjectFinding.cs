using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public enum DropProtectedObjectKind
{
    SchemaNotEmpty,
    FixedDatabaseRole,
}

public sealed record DropProtectedObjectFinding(
    DropProtectedObjectKind Kind,
    string ObjectName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.DropProtectedObjectRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, Column);
}
