using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md Tier 2 "Dynamic SQL quality" item 3: <c>INSERT INTO #temp EXEC
/// OtherProc</c>'s implicit assumption that <c>OtherProc</c>'s actual, engine-described result
/// set matches <c>#temp</c>'s own declared columns, positionally - T-SQL binds an
/// <c>INSERT ... EXEC</c> result set purely by column POSITION, never by name, so a same-named
/// column in a different position is exactly as silently wrong as a differently-typed one in the
/// same position. <see cref="ColumnCountMismatch"/> is a distinct, cheaper claim than
/// <see cref="ColumnTypeMismatch"/>: the engine raises a hard, immediate error (Msg 213/8164,
/// "column name or number of supplied values does not match table definition") the moment the
/// counts differ, so it's not itself a SILENT defect the way a type mismatch is - but it's still
/// worth reporting, since it names a query that provably fails at runtime every time it's called,
/// which static analysis alone can otherwise never promise. <see cref="TempTableExecShapeFinding.WriteLoss"/> is set only
/// for <see cref="ColumnTypeMismatch"/>, reusing <see cref="Rules.WriteLossClassifier"/> exactly as
/// <see cref="ProcCallArgumentMismatchFinding"/> does for the identical "assignment across a
/// call boundary" shape - not a predicate, so no seek/scan verdict applies, and (like that
/// finding) no plan-XML oracle marker exists for a parameter/result-set binding conversion; the
/// underlying WriteLossKind mechanism is already oracle-proven elsewhere
/// (<c>WriteLossOracleTests</c>).
/// </summary>
public enum TempTableExecShapeFindingKind
{
    /// <summary>The executed proc's described column count differs from the temp table's own declared column count - always a runtime error (Msg 213/8164), not a silent defect, but a real, provable "this call always fails" fact.</summary>
    ColumnCountMismatch,

    /// <summary>Column counts match; at least one position's types risk the silent data loss <see cref="TempTableExecShapeFinding.WriteLoss"/> names.</summary>
    ColumnTypeMismatch,
}

public sealed record TempTableExecShapeFinding(
    TempTableExecShapeFindingKind Kind,
    string TempTableQualifiedName,
    string ExecutedProcQualifiedName,
    int TempTableDeclaredColumnCount,
    int DescribedColumnCount,
    string? ColumnName,
    int? ColumnPosition,
    string? TempColumnTypeDisplay,
    string? DescribedColumnTypeDisplay,
    WriteLossKind? WriteLoss,
    string? CallerScopeQualifiedName,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    [property: JsonIgnore] int Column,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, Column);
}

/// <summary>
/// One <c>INSERT INTO #temp EXEC proc</c> site this pass could not resolve to a verdict, plus
/// why - CLAUDE.md's "unresolved ≠ false" discipline, mirroring
/// <c>Live.Catalog.LiveLineageParityReport</c>'s own <c>UncompilableObjects</c>/<c>Unverified</c>
/// shape: an executed proc this tool cannot describe (compile error, doesn't exist, an OUTPUT or
/// table-valued parameter the probe can't render, or a temp table whose own declared shape this
/// tool's own catalog pass never resolved) is reported honestly here rather than silently
/// counted as clean.
/// </summary>
public sealed record UnanalyzedTempTableExecSite(
    string TempTableQualifiedName, string ExecutedProcQualifiedName, string Reason, string SourcePath, int Line, int Column);

/// <summary>
/// The whole item-3 pass's own result: real findings plus every site this pass declined to judge.
/// Live-mode only by construction - the live round trip (<c>sys.dm_exec_describe_first_result_set</c>)
/// this stream's entire verdict depends on only makes sense against a real connected database,
/// exactly like <c>CrossTableTypeDriftScanner</c>'s FK-linked half or the indexed-view registry;
/// a file-mode scan carries <see cref="Empty"/>.
/// </summary>
public sealed record TempTableExecShapeReport(
    IReadOnlyList<TempTableExecShapeFinding> Findings,
    IReadOnlyList<UnanalyzedTempTableExecSite> Unanalyzed)
{
    public static readonly TempTableExecShapeReport Empty = new([], []);
}
