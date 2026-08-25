using System.Text.Json.Serialization;
using SilentScan.Core.Catalog;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public sealed record CascadingForeignKeyFinding(
    string ConstraintName,
    string ParentTableQualifiedName,
    string ReferencedTableQualifiedName,
    ReferentialAction DeleteAction,
    ReferentialAction UpdateAction,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.CascadingForeignKeyRuleId;

    public SourceSpan Location => new(SourcePath, Line, 1);
}

