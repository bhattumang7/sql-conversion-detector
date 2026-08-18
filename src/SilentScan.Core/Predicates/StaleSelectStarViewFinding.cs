using System.Text.Json.Serialization;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "Second full-archive practitioner sweep" §G: "View defined with
/// <c>SELECT *</c> whose compiled column list has gone stale against the base table's current
/// shape" - a view's column list is frozen at <c>CREATE</c>/last <c>sp_refreshview</c> time; a
/// later <c>ALTER TABLE ... ADD/DROP COLUMN</c> on the base table does not propagate, so the view
/// silently keeps exposing (or omitting) columns that no longer match the base table's real shape.
/// A genuinely different claim from generic "don't SELECT *" style advice (already deliberately
/// out of scope elsewhere in this codebase) - this is specifically about metadata drift, not
/// query-time cost, and from <see cref="SelectStarViewFinding"/> (a frozen list vs. a DIFFERENT
/// consuming query's own narrower column selection) - this kind never needs a second consumer site
/// at all, the drift already exists between the view and its own base table.
///
/// <b>Stronger than "some columns silently missing/extra" - oracle-confirmed to also produce
/// silently WRONG data under an unchanged column NAME</b>, not merely a milder "new column
/// invisible" gap the checklist's own drafted language suggested. Directly against the standing
/// Docker instance (disposable scratch database, dropped immediately after): <c>CREATE TABLE
/// Base(Id, A, B)</c>, <c>CREATE VIEW V AS SELECT * FROM Base</c> (view's own compiled columns:
/// Id, A, B), then <c>ALTER TABLE Base ADD C</c> followed by <c>ALTER TABLE Base DROP COLUMN B</c>
/// (base table's real current columns: Id, A, C). The view's own <c>sys.columns</c> row set - AND
/// <c>sys.dm_exec_describe_first_result_set</c>'s live, describe-only answer for <c>SELECT * FROM
/// V</c> - both still report Id, A, B: describe-only re-probing does not force a re-expansion of
/// a view's own frozen <c>*</c> the way it does for a base table's retyped column (this stream's
/// live-only ground truth is therefore the view's own current <c>sys.columns</c> row set, not
/// <c>sys.dm_exec_describe_first_result_set</c>, unlike this codebase's live-parity gate for an
/// ordinary view/inline-TVF column TYPE). Actually executing <c>SELECT * FROM V</c> after both ALTERs
/// (with a real inserted row, <c>A = 1</c>, the new column <c>C = 99</c>) returned a row labeled
/// <c>Id, A, B</c> whose third value was <c>99</c> - the live data physically occupying the third
/// column slot (now really <c>C</c>) surfaced under the view's stale, frozen label <c>B</c>. A
/// consumer reading this view's "B" column today is silently reading real "C" data.
///
/// Catalog-decidable: <see cref="Catalog.DatabaseCatalog.TryGetViewCompiledColumns"/> (the view's
/// own <c>sys.columns</c> row set, in ordinal order, live-only - <c>SilentScan.Verify.Catalog.
/// LiveCatalogReader</c>) compared, IN ORDER, against the base table's CURRENT <see
/// cref="Catalog.CatalogTable.Columns"/> (also live-only for this same base-table-shape reasoning
/// stated on <see cref="Catalog.DatabaseCatalog.AddViewCompiledColumns"/>'s own doc comment: file
/// mode's own re-derivation always agrees with itself by construction, so staleness is
/// structurally impossible to observe from parsed text alone). Ordinal, not merely set,
/// comparison - deliberately: a same-COUNT, different-identity drift (the DROP-then-ADD phantom
/// shown above) would survive a naive set-equality check untouched.
///
/// Deliberately scoped to v1, matching the sibling <see cref="SelectStarViewFinding"/>'s own
/// documented v1 limits: only the view's own OUTERMOST query specification's bare/qualified
/// <c>*</c>, selecting from exactly ONE real base table (no join, no derived table, no CTE) is
/// inspected - a join or a nested-subquery star is a known, documented v1 scope limit, not
/// silently missed.
///
/// <see cref="FindingConfidence.High"/> - the catalog-level list mismatch is a pure, exact fact
/// once both sides are read, and the freezing/no-re-propagation mechanism is oracle-confirmed,
/// unconditional engine behavior. SARIF Warning, not Error: the mismatch is certain, but this
/// stream does not itself prove that any REAL consuming query relies on the drifted/mislabeled
/// column today - the same "structural risk, not a proven-wrong-result for a specific site" tier
/// <see cref="SelectStarViewFinding"/> itself already uses.
///
/// Version-insensitive: <c>CREATE</c>/<c>ALTER VIEW</c>-time column-list binding for <c>SELECT
/// *</c> is ancient, stable T-SQL behavior, unaffected by compatibility level or CE mode.
/// </summary>
public sealed record StaleSelectStarViewFinding(
    string ViewQualifiedName,
    string BaseTableQualifiedName,
    IReadOnlyList<string> ViewCompiledColumns,
    IReadOnlyList<string> BaseTableCurrentColumns,
    [property: JsonIgnore] string SourcePath,
    [property: JsonIgnore] int Line,
    FindingConfidence Confidence = FindingConfidence.High)
{
    public SourceSpan Location => new(SourcePath, Line, 1);
}

