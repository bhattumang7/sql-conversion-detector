using System.Text.Json.Serialization;
using SilentScan.Core.Rules;

namespace SilentScan.Core.Predicates;

public enum CreateDatabaseOptionConflictKind
{
    ContainmentPartialAndCatalogCollation,
}

public sealed record CreateDatabaseOptionConflictFinding(
    CreateDatabaseOptionConflictKind Kind,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.CreateDatabaseOptionConflictRuleId;

    public SourceSpan Location => new(SourcePath, Line, Column);
}
