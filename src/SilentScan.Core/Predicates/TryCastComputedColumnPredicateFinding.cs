namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "Second full-archive practitioner sweep" §G: "<c>TRY_CAST</c> in a
/// non-persisted computed column used in a predicate" - <c>TRY_CAST</c> is session-
/// <c>DATEFORMAT</c>-dependent (and, more generally, session-settings-dependent for any
/// ambiguous-format conversion), so the engine classifies it non-deterministic; a computed column
/// built on it can therefore never be <c>PERSISTED</c>, and - independent of persistence - can
/// never be indexed at all, so a predicate filtering on it can never seek no matter what index
/// exists elsewhere on the table.
///
/// Oracle-confirmed directly (Docker instance, disposable scratch database, dropped immediately
/// after), three separate facts:
/// <list type="bullet">
/// <item><description><c>TRY_CAST('03/04/2024' AS DATE)</c> genuinely returned 2024-03-04 under
/// <c>SET DATEFORMAT mdy</c> and genuinely returned 2024-04-03 under <c>SET DATEFORMAT dmy</c> -
/// the identical call, the identical input, two different results depending purely on session
/// state.</description></item>
/// <item><description><c>ALTER TABLE ... ADD ParsedDate AS TRY_CAST(RawDate AS DATE) PERSISTED</c>
/// failed at DDL time with the engine's own exact wording: "Computed column 'ParsedDate' in table
/// '...' cannot be persisted because the column is non-deterministic."</description></item>
/// <item><description>Even the NON-persisted form rejected an ordinary <c>CREATE INDEX</c>
/// directly against it: "Column '...' cannot be used in an index or statistics or as a partition
/// key because it is non-deterministic." - a strictly wider gap than the checklist's own original
/// "can never be PERSISTED" framing: this rule's actionable claim is "can never be indexed",
/// PERSISTED or not, since non-determinism alone blocks indexing regardless of persistence.
/// </description></item>
/// </list>
///
/// Schema+AST decidable: the computed column's own definition text
/// (<see cref="Catalog.SchemaDependencyKind.ComputedColumn"/> in
/// <see cref="Catalog.DatabaseCatalog.SchemaExpressions"/>, populated in both file mode
/// (<see cref="Catalog.SchemaExpressionCollector"/>) and live mode (<c>LiveCatalogReader</c>) uses
/// <c>TRY_CAST</c> anywhere in it, AND that same column is referenced anywhere inside a genuine
/// filter context (a WHERE clause, a JOIN's own ON clause, or HAVING) at a real query site
/// elsewhere in the corpus - a computed column defined this way but never filtered on costs
/// nothing extra beyond the recompute <see cref="NonPersistedComputedColumnFinding"/> already
/// reports; this kind exists specifically for the "someone is trying to seek through it" case.
/// <see cref="Catalog.CatalogColumn.IsPersisted"/> is also checked defensively (must be false) even
/// though the oracle proves a genuine <c>TRY_CAST</c> computed column can never legally be
/// PERSISTED - a belt-and-suspenders catalog-agreement check, not a scope narrowing.
///
/// Deliberately base-table-only for v1 (mirrors <see cref="CatchAllPredicateScanner"/>'s own
/// documented scope limit): column resolution goes through <see cref="Lineage.FromScopeResolver"/>
/// with no CTE/view/temp-table/subquery scoping - a predicate inside a nested subquery, or one
/// reached only through a view/CTE layer, is a known v1 scope limit, not silently missed.
///
/// <see cref="FindingConfidence.High"/> - the non-determinism mechanism is unconditional,
/// oracle-confirmed engine fact with zero workload dependence, the same certainty tier every other
/// oracle-confirmed DDL-mechanics finding in this codebase uses. SARIF Warning: a real, provably
/// lost seek (not merely a magnitude estimate), but whether it costs anything in practice still
/// depends on the table's own row count/whether an index exists elsewhere the query could
/// otherwise have used - the same "structural risk, not a plan-shape proof for this exact site"
/// tier the rest of the syntactic sargability family (<see cref="SargabilityFinding"/>) already
/// uses, since this stream deliberately does not run a per-finding plan-XML probe the way the
/// type-conversion oracle does.
///
/// Version-insensitive: <c>TRY_CAST</c>'s own non-determinism classification and the PERSISTED/
/// index rejection are ancient, stable T-SQL behavior (<c>TRY_CAST</c> shipped in SQL Server 2012),
/// unaffected by compatibility level.
/// </summary>
public sealed record TryCastComputedColumnPredicateFinding(
    string TableQualifiedName,
    string ColumnName,
    string DefinitionText,
    string DefinitionSourcePath,
    int DefinitionLine,
    string PredicateSourcePath,
    int PredicateLine,
    int PredicateColumn,
    FindingConfidence Confidence = FindingConfidence.High);
