using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum StatementShapeFindingKind
{
InsertWithoutColumnList,

OrdinalOrderBy,

TopWithoutOrderBy,

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
    FindingConfidence Confidence = FindingConfidence.Medium)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

