namespace SilentScan.Core.Predicates;

public enum IndexCoverageFindingKind
{
    /// <summary>A query whose own WHERE clause genuinely constrains a base table's ONE candidate
    /// nonclustered index leading column (an equality/range comparison reachable without crossing
    /// an OR - the same <c>FlattenAnd</c> discipline <see cref="CompositeIndexLeadingColumnScanner"/>
    /// already established), but that index's own key + INCLUDE columns do not cover every OTHER
    /// column of the same base table the statement references anywhere (SELECT list, WHERE, ORDER
    /// BY, JOIN ON) - so a seek on this index for a matched row still needs a second trip back to
    /// the base table (a Key/RID Lookup) to fetch the missing column(s), per matched row. Oracle-
    /// confirmed directly (Docker instance, SQL Server 2022, real 20,000-row table, real execution
    /// under <c>SET STATISTICS XML ON</c>): a WHERE-equality seek against a non-covering
    /// nonclustered index produced a real plan with an <c>Index Seek</c> feeding a <c>Nested Loops</c>
    /// into a <c>Clustered Index Seek</c> carrying <c>Lookup="1"</c> - confirmed the SAME query
    /// against the identical index widened with an <c>INCLUDE</c> covering every referenced column
    /// produces a single plain <c>Index Seek</c>, no lookup, no Nested Loops at all.
    ///
    /// A nonclustered index's own leaf row always carries the table's clustering key as its row
    /// locator (the very thing a lookup follows back to the base row), so the clustering key's own
    /// columns are always implicitly "covered" regardless of the candidate index's own KeyColumns/
    /// IncludedColumns - computed from <see cref="Catalog.CatalogIndex.IsClustered"/> where that
    /// live-only field is known, falling back to the table's own PRIMARY KEY index (SQL Server's
    /// real default is CLUSTERED unless a script says otherwise) in file mode, where getting this
    /// wrong only means under-reporting, never a false claim.
    ///
    /// The hard precision guard this project's own DBA-script-sweep intro requires for this whole
    /// pairing: fires ONLY when the base table has EXACTLY ONE usable nonclustered index whose
    /// leading key column is among the AND-constrained columns - i.e. exactly one real candidate
    /// seek path exists for this predicate. A table with a second index that ALSO leads with the
    /// same constrained column is a genuine "the optimizer has an alternative access path to
    /// choose from instead" case (that alternative might be the one that's actually covering, or
    /// might not be - this pass cannot know which one the optimizer will pick without seeing every
    /// index's own coverage, and reporting a violation against the wrong one would be a false
    /// claim) and is declined rather than guessed at, matching <see
    /// cref="CompositeIndexLeadingColumnFinding"/>'s own identical "no alternative seek path" guard
    /// verbatim.
    ///
    /// V1 scope, stated honestly: only a base-table, depth-0 predicate site is inspected (no CTE/
    /// view/temp-table scoping - the same limit <see cref="CompositeIndexLeadingColumnScanner"/>
    /// already documents); <c>SELECT</c>/<c>UPDATE</c>/<c>DELETE</c> only, matching that scanner's
    /// own <c>MERGE</c> scope limit for the identical reason. This project's own field-literature-
    /// cited sibling shape - a join/filter column on the inner side of a nested-loop join with no
    /// supporting index at all ("eager-index-spool-prone") - was investigated and deliberately NOT
    /// shipped alongside this one: the "exactly one candidate index" precision guard that makes
    /// THIS finding trustworthy has no clean analogue for a "zero indexes exist at all" shape (there
    /// is no single candidate to point at), and reliably distinguishing a genuine nested-loop-with-
    /// spool plan from a hash-join plan that never spools at all requires exactly the cardinality
    /// information a static pass does not have - shipping it anyway would mean either guessing at
    /// join strategy or dropping the guard that keeps this finding precise, and CLAUDE.md's own
    /// "precision beats recall everywhere" rule says neither is acceptable. Documented as a
    /// deliberately declined v1 scope limit, not a silent gap - see
    /// docs/detection-checklist.md for the full reasoning.
    /// </summary>
    KeyLookupProneIndex,
}

/// <summary>
/// docs/detection-checklist.md "DBA-script family sweep (2026-08-17)" §B "Index-coverage shapes" -
/// the one member of that item's own pair that survived the precision guard both halves were
/// required to meet. Both catalog AND plan-XML oracle confirmation together (the checklist's own
/// framing for why this item needed its own new finding type rather than folding into <see
/// cref="QueryAntiPatternFinding"/>) - a real key-lookup cost, not a style preference: every matched
/// row pays a second random I/O back to the base table, and the plan's own <c>Lookup="1"</c> marker
/// is the oracle-confirmable ground truth this static claim predicts (see
/// <see cref="IndexCoverageFindingKind.KeyLookupProneIndex"/> for the full oracle evidence).
/// </summary>
public sealed record IndexCoverageFinding(
    IndexCoverageFindingKind Kind,
    string TableQualifiedName,
    string? IndexName,
    IReadOnlyList<string> IndexKeyColumns,
    IReadOnlyList<string> IndexIncludedColumns,
    IReadOnlyList<string> UncoveredColumns,
    string SourcePath,
    int Line,
    int Column,
    FindingConfidence Confidence = FindingConfidence.High);
