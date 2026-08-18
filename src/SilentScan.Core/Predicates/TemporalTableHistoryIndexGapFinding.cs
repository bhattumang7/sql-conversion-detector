using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "Temporal table history-side index gap" - a nonclustered index
/// exists on a system-versioned temporal table's CURRENT side with no structurally matching index
/// on its HISTORY side. <c>FOR SYSTEM_TIME AS OF/BETWEEN/...</c> rewrites to a
/// <c>Concatenation</c> (UNION ALL) of the current and history tables - oracle-confirmed directly
/// (real seeded data: 5,000 current-table rows, 2,500 history-table rows,
/// <c>UPDATE STATISTICS ... WITH FULLSCAN</c> on both): a sargable predicate that seeks the
/// current-table branch via its own nonclustered index degrades to a <c>Clustered Index Scan</c>
/// on the history-table branch when the history table carries no equivalent index, and - the
/// other half of the same probe, oracle-confirmed the same way - seeks BOTH branches once a
/// structurally matching index is added to the history side. This is a real, mechanically-proven
/// cost, but (like <see cref="UntrustedConstraintFinding"/>/<see cref="ForcedSerialFinding"/>) the
/// oracle confirmation is of the GENERAL mechanism, not a per-finding plan-XML probe against a
/// real query site - reported at <see cref="FindingConfidence.High"/>/SARIF Warning, the same
/// "structural risk, not provably-wrong-result" tier those two use, not the Error tier a
/// correctness finding gets.
///
/// <b>Match criterion, oracle-decided rather than assumed:</b> a current-side index is flagged
/// unless the history side carries an index whose <see cref="Catalog.CatalogIndex.KeyColumns"/>
/// are IDENTICAL, in the SAME ORDER (ordinal, case-insensitive) - included columns and uniqueness
/// are ignored (neither affects seek-vs-scan, only covering-ness/cost, oracle-confirmed by the
/// same probe). Key-column order is deliberately treated as significant even though a second
/// oracle probe found one case where a REVERSED key order still produced a seek on both branches
/// (a predicate providing an equality value for every key column, letting the optimizer match
/// them in any order) - order-sensitivity is the conservative, structurally-safe reading for a
/// finding that makes no claim about any one query's own predicate shape: a reversed-order history
/// index is not guaranteed to rescue a predicate that only supplies the CURRENT index's own
/// leading column(s), which is exactly the common, load-bearing case this finding exists to catch.
/// A false negative here (an order-mismatched index this rule still flags, that some specific full-
/// equality query would in fact seek through) is the safe direction to be wrong in - the same
/// trade-off precedent <see cref="NonUniqueUpdateSourceFinding"/>'s own doc comment already accepts
/// for its own fan-out risk, just in the opposite (over-report, not under-report) direction, and
/// deliberately so: CLAUDE.md's precision rule protects against a FALSE POSITIVE finding kind, not
/// against a real, oracle-confirmed risk being described slightly more broadly than one specific
/// query would exercise.
///
/// <b>PRIMARY KEY/UNIQUE constraint current-side indexes are never compared - oracle-confirmed
/// structurally impossible on the history side, not a scope gap.</b> SQL Server outright refuses
/// <c>ALTER TABLE ... ADD CONSTRAINT PRIMARY KEY</c> (Msg 13558) and
/// <c>... ADD CONSTRAINT UNIQUE</c> (Msg 13583) against a temporal history table - a currently-
/// valid history table can never carry either, by construction, so flagging the current table's
/// own PK/unique-constraint index against it would be a guaranteed-always-fire signal with no
/// possible fix, the exact "reinventing a schema fact the engine already forbids" case CLAUDE.md's
/// scope rule steers away from. Only <see cref="Catalog.CatalogIndexKind.Index"/> (an ordinary,
/// non-constraint-backed index) is compared on the CURRENT side; the history table's own
/// engine-auto-created period clustered index (typically keyed on the row-end/row-start columns,
/// confirmed directly: <c>ix_&lt;HistoryTableName&gt;</c> clustered on <c>(ValidTo, ValidFrom)</c>
/// for a table with no explicit history index named) is never itself a candidate finding either -
/// it serves a different purpose (bounding the period range) than the business-column index this
/// rule is about, and comparing it against the current table's own (usually PK-backed, excluded
/// anyway) clustered index would be an unfair, always-mismatched comparison.
///
/// Filtered/columnstore/disabled indexes are excluded on BOTH sides, matching
/// <see cref="Catalog.CatalogTable.IsIndexedColumn"/>'s own "genuinely seekable" definition - a
/// filtered index only covers a predicate-matching subset, a columnstore index has no B-tree to
/// seek, and a disabled index is unusable by the engine regardless of its own definition.
///
/// Catalog-only, unconditional - reported once per current-side index lacking a history-side
/// match, independent of whether any scanned query actually issues a <c>FOR SYSTEM_TIME</c> query
/// against it (the same "reported once per object, not once per use site" precedent
/// <see cref="MaxTypedColumnFinding"/> already establishes for a stable schema fact). Live-mode
/// only - <see cref="Catalog.DatabaseCatalog.TemporalTablePairs"/> is always empty for a file-mode
/// scan (see its own doc comment for why).
/// </summary>
public sealed record TemporalTableHistoryIndexGapFinding(
    string CurrentTableQualifiedName,
    string HistoryTableQualifiedName,
    string? CurrentIndexName,
    IReadOnlyList<string> KeyColumns,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, 1);
}

