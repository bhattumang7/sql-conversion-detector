using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

public enum NonIndexableColumnFindingKind
{
    /// <summary>A string/binary-family column declared MAX-typed (VARCHAR(MAX)/NVARCHAR(MAX)/VARBINARY(MAX)) - can never be an index KEY column, but can still be carried as a nonclustered index's INCLUDE column.</summary>
    MaxLength,

    /// <summary>A legacy large-object column (TEXT/NTEXT/IMAGE) - stronger than <see cref="MaxLength"/>: oracle-confirmed rejected as both a KEY column (Msg 1919) and an INCLUDE column (Msg 1999), so it can never appear in any index at all, not even as a covering column.</summary>
    LegacyLargeObject,
}

/// <summary>
/// A string/binary-family column declared MAX-typed, or a legacy large-object column
/// (docs/detection-checklist.md Tier 1 "Oversized and MAX-typed parameters" #3) - catalog-only
/// structural fact, independent of whether any scanned query actually uses it in a predicate or
/// JOIN: neither kind can ever be an index KEY at all (SQL Server enforces this at CREATE INDEX
/// time), so neither can ever drive a seek regardless of how it's used. Reported once per column,
/// not once per use site.
///
/// <see cref="NonIndexableColumnFindingKind.LegacyLargeObject"/> (TEXT/NTEXT/IMAGE) is a distinct
/// fact from <see cref="NonIndexableColumnFindingKind.MaxLength"/>, not a variant of it - these
/// are separate, non-overlapping type families (TEXT/NTEXT/IMAGE have no MAX-length declaration
/// to match on) with a stronger restriction: oracle-confirmed directly (Docker SQL Server 2022) a
/// TEXT/NTEXT/IMAGE column is rejected even as a nonclustered index's INCLUDE column (Msg 1999,
/// "is of a type that is invalid for use as included column in an index"), where a MAX-typed
/// column is accepted there without error - only the KEY-column restriction (Msg 1919) is shared
/// between the two kinds.
/// </summary>
public sealed record MaxTypedColumnFinding(
    string TableQualifiedName,
    string ColumnName,
    string TypeDisplay,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    NonIndexableColumnFindingKind Kind = NonIndexableColumnFindingKind.MaxLength,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, 1);
}

