using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum SelectiveXmlIndexValueColumnFindingKind
{
    TooWide,

    LargeObject,
}

public sealed record SelectiveXmlIndexValueColumnFinding(
    string TableQualifiedName,
    string SecondaryIndexName,
    string PrimaryIndexName,
    string PathName,
    string TypeDisplay,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    SelectiveXmlIndexValueColumnFindingKind Kind = SelectiveXmlIndexValueColumnFindingKind.TooWide,
    FindingConfidence Confidence = FindingConfidence.High) : IFinding
{
    public SourceSpan Location => new(SourcePath, Line, 1);
}
