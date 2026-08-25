using System.Text.Json.Serialization;
using SilentScan.Core.Rules;


namespace SilentScan.Core.Predicates;

public enum StatementShapeFindingKind
{
    InsertWithoutColumnList,

    OrdinalOrderBy,

    TableWithNoPrimaryKey,

    MissingSetNocountOn,

    BareSelectStar,
}

public sealed record StatementShapeFinding(
    StatementShapeFindingKind Kind,
    string ModuleQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    string DetailText,
    FindingConfidence Confidence = FindingConfidence.Medium) : IFinding
{
    public string RuleId { get; } = FindingRuleIds.StatementShapeRuleId(Kind);

    public SourceSpan Location => new(SourcePath, Line, Column);
}

