using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

/// <summary>
/// <c>col BETWEEN start AND end</c> where <paramref name="ColumnScale"/> (the column's own
/// declared TIME/DATETIME2/DATETIMEOFFSET fractional-seconds precision) exceeds
/// <paramref name="BoundaryLiteralFractionalDigits"/> (the upper-bound literal's own) - the
/// classic "end of period" BETWEEN hack (docs/detection-checklist.md Tier 1 "Type-aware upgrade
/// of the sargability stream", Aaron Bertrand's widely-cited "Bad Habits: Using BETWEEN").
/// Distinct from every other finding in this stream: this is a CORRECTNESS bug, not a lost seek -
/// BETWEEN is perfectly sargable - the boundary literal silently EXCLUDES rows whose fractional-
/// second value falls in the gap between the literal's own precision and the column's real one.
/// Oracle-confirmed directly: a DATETIME2(7) row at 2024-12-31 23:59:59.9999999 is silently
/// dropped by <c>BETWEEN '2024-01-01' AND '2024-12-31 23:59:59.997'</c> (the classic 3-nines
/// hack, itself a leftover habit from legacy DATETIME's coarser rounding) while a
/// precision-correct <c>&gt;= '2024-01-01' AND &lt; '2025-01-01'</c> rewrite includes it.
/// </summary>
public sealed record TemporalBoundaryPrecisionFinding(
    string TableQualifiedName,
    string ColumnName,
    int ColumnScale,
    int BoundaryLiteralFractionalDigits,
    string BoundaryLiteralText,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

