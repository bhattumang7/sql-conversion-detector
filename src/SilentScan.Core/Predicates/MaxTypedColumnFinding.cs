using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

/// <summary>
/// A string/binary-family column declared MAX-typed (docs/detection-checklist.md Tier 1
/// "Oversized and MAX-typed parameters" #3) - catalog-only structural fact, independent of
/// whether any scanned query actually uses it in a predicate or JOIN: a MAX-typed column can
/// never be an index key at all (SQL Server enforces this at CREATE INDEX time), so it can never
/// drive a seek regardless of how it's used. Reported once per column, not once per use site.
/// </summary>
public sealed record MaxTypedColumnFinding(
    string TableQualifiedName,
    string ColumnName,
    string TypeDisplay,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, 1);
}

