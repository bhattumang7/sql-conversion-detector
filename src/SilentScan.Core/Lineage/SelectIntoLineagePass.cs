using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Parsing;
using SilentScan.Core.Common;

namespace SilentScan.Core.Lineage;

/// <summary>
/// Resolves <c>SELECT ... INTO target FROM ...</c> target columns a second time, after
/// <see cref="LineageResolver"/> has run - closing the gap <see cref="Catalog.SelectIntoColumnResolver"/>
/// documents as out of its own reach: Pass 1 (<see cref="CatalogBuilder"/>) resolves a SELECT
/// INTO target's columns against tables already known to the catalog only, since views/CTEs/
/// UNION sources are a Pass 2 concept catalog-building can't depend on without inverting the
/// pass order. <c>SELECT col INTO #tmp FROM dbo.SomeView</c> therefore left every #tmp column
/// untyped even when the view's own column was perfectly resolvable - not an Unknown verdict,
/// a comparison that silently vanished with no finding, no ledger entry, nothing (the
/// TypedPredicateExtractor operand-resolution path for a null-typed column reaches neither the
/// classifier nor the skip ledger).
///
/// Runs as a distinct step in <see cref="Reporting.ScanReportBuilder"/> between
/// <c>LineageResolver.Resolve</c> and the Tier-1/typed passes, re-resolving each target's SELECT
/// list through <see cref="QueryExpressionResolver"/> (the same machinery a view's own SELECT
/// list uses) - which fixes the UNION give-up too, for free, since that resolver already handles
/// <see cref="BinaryQueryExpression"/> natively. <see cref="Catalog.DatabaseCatalog"/> is the
/// same mutable instance every downstream pass reads, so mutating it here is immediately visible
/// without threading a second catalog through the rest of the pipeline.
///
/// Merges into the Pass-1 entry rather than replacing it: Pass 1 may already have applied an
/// <c>ALTER TABLE #tmp ADD ...</c> or <c>CREATE INDEX ... ON #tmp</c> after the original SELECT
/// INTO (both routinely appear in real procs immediately after populating a temp table) -
/// replacing the whole column/index list would silently discard those and flip a real Indexed
/// finding to false. Only column TYPES are filled in, and only for columns Pass 1 left null.
/// </summary>
public static class SelectIntoLineagePass
{
    public static void Apply(DatabaseCatalog catalog, LineageCatalog lineage, IEnumerable<SqlParseResult> parseResults)
    {
        foreach (var result in parseResults)
        {
            var visitor = new Visitor(catalog, lineage.AllRelations, result.SourcePath);
            result.Fragment.Accept(visitor);
        }
    }

    private sealed class Visitor(DatabaseCatalog catalog, IReadOnlyDictionary<string, ResolvedRelation> resolvedViews, string sourcePath) : TSqlFragmentVisitor
    {
        private readonly Stack<IReadOnlyDictionary<string, ResolvedRelation>> _cteStack = new();
        private string? _currentScope;

        public override void ExplicitVisit(SelectStatement node)
        {
            PushCteScope(node.WithCtesAndXmlNamespaces);

            if (node.Into is not null)
            {
                ResolveSelectIntoTarget(node);
            }

            node.AcceptChildren(this);
            _cteStack.Pop();
        }

        // Mirrors CatalogBuilder's identical overrides (ScriptDOM's Accept() binds at compile
        // time to the most specific ExplicitVisit overload, so a base-type-only override never
        // fires for e.g. an AlterProcedureStatement) - needed so a SELECT INTO inside a proc/
        // function/trigger body resolves its temp target against the same scoped catalog key
        // CatalogBuilder stored it under.
        public override void ExplicitVisit(CreateProcedureStatement node) => VisitScopedBody(node, node.ProcedureReference.Name);

        public override void ExplicitVisit(AlterProcedureStatement node) => VisitScopedBody(node, node.ProcedureReference.Name);

        public override void ExplicitVisit(CreateOrAlterProcedureStatement node) => VisitScopedBody(node, node.ProcedureReference.Name);

        public override void ExplicitVisit(CreateFunctionStatement node) => VisitScopedBody(node, node.Name);

        public override void ExplicitVisit(AlterFunctionStatement node) => VisitScopedBody(node, node.Name);

        public override void ExplicitVisit(CreateOrAlterFunctionStatement node) => VisitScopedBody(node, node.Name);

        public override void ExplicitVisit(CreateTriggerStatement node) => VisitScopedBody(node, node.Name);

        public override void ExplicitVisit(AlterTriggerStatement node) => VisitScopedBody(node, node.Name);

        public override void ExplicitVisit(CreateOrAlterTriggerStatement node) => VisitScopedBody(node, node.Name);

        private void VisitScopedBody(TSqlFragment node, SchemaObjectName name)
        {
            var previousScope = _currentScope;
            _currentScope = SchemaObjectNameHelper.Qualify(name);
            node.AcceptChildren(this);
            _currentScope = previousScope;
        }

        private void ResolveSelectIntoTarget(SelectStatement select)
        {
            var targetName = select.Into!;
            var (schema, _) = SchemaObjectNameHelper.Resolve(targetName);
            var isTemp = schema is null;
            var qualifiedName = SchemaObjectNameHelper.Qualify(targetName);

            // Pass 1 always creates this entry (CatalogBuilder.VisitSelectInto runs
            // unconditionally on every SELECT INTO) - a miss here means the statement never
            // reached Pass 1 at all (a different file/batch than what Pass 1 saw), not
            // something for this pass to guess at.
            var existing = catalog.Find(qualifiedName, isTemp ? _currentScope : null);
            if (existing is null)
            {
                // Should not happen in a well-formed scan (Pass 1's CatalogBuilder.VisitSelectInto
                // runs unconditionally on every SELECT INTO before this pass starts) - but ledgered
                // rather than silently returning, since a future ordering bug here would otherwise
                // vanish with zero trace instead of surfacing as a visible skip.
                catalog.Skipped.Record(
                    AnalysisPass.Lineage, sourcePath, select.StartLine, select.StartColumn, "SELECT INTO",
                    $"'{qualifiedName}' has no Pass-1 catalog entry to merge into - the SELECT INTO statement may not have reached CatalogBuilder");
                return;
            }

            var resolved = QueryExpressionResolver.Resolve(
                select.QueryExpression, catalog, resolvedViews, sourcePath, catalog.Skipped, CurrentCteRelations(), _currentScope);

            if (existing.Columns.Count == 0 && resolved.Count > 0)
            {
                // Pass 1 gave up on this target entirely (SelectIntoColumnResolver bails with
                // zero columns for anything that isn't a plain QuerySpecification - a UNION
                // source, most commonly) - nothing to merge INTO, so populate fresh from Pass
                // 2's resolution instead, matching SelectIntoColumnResolver's own column
                // defaults for a plain column reference.
                var freshColumns = resolved
                    .Select(r => new CatalogColumn(r.Name, ColumnProvenanceAnalysis.TryGetScalarType(r.Provenance), IsNullable: true, IsIdentity: false, IsComputed: false, IsPersisted: false))
                    .ToList();
                catalog.AddOrReplace(existing with { Columns = freshColumns }, isTemp ? _currentScope : null);
                return;
            }

            // Matched by NAME, not position: by the time this pass runs, `existing.Columns` is
            // the catalog's FINAL state for this target, which may already include columns a
            // later ALTER TABLE #tmp ADD merged in (CatalogBuilder ran to completion before this
            // pass starts) - those never appear in `resolved` (this SELECT list's own output)
            // and must pass through untouched, not trip a count mismatch against the original
            // SELECT list's shape.
            var resolvedByName = new Dictionary<string, ResolvedColumn>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in resolved)
            {
                resolvedByName.TryAdd(r.Name, r);
            }

            var mergedColumns = existing.Columns
                .Select(column => column.Type is null && resolvedByName.TryGetValue(column.Name, out var r)
                    ? column with { Type = ColumnProvenanceAnalysis.TryGetScalarType(r.Provenance) }
                    : column)
                .ToList();

            catalog.AddOrReplace(existing with { Columns = mergedColumns }, isTemp ? _currentScope : null);
        }

        private void PushCteScope(WithCtesAndXmlNamespaces? withClause)
        {
            var currentCtes = CurrentCteRelations();
            var ctes = CteResolver.Resolve(withClause, catalog, resolvedViews, sourcePath, catalog.Skipped, _currentScope);
            _cteStack.Push(ctes.Count == 0 ? currentCtes : MergeCtes(currentCtes, ctes));
        }

        private IReadOnlyDictionary<string, ResolvedRelation> CurrentCteRelations() =>
            _cteStack.Count > 0 ? _cteStack.Peek() : EmptyResolvedRelations;

        private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedRelations = new Dictionary<string, ResolvedRelation>();

        private static Dictionary<string, ResolvedRelation> MergeCtes(
            IReadOnlyDictionary<string, ResolvedRelation> outer, IReadOnlyDictionary<string, ResolvedRelation> inner)
        {
            var merged = new Dictionary<string, ResolvedRelation>(outer, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, relation) in inner)
            {
                merged[name] = relation;
            }

            return merged;
        }
    }
}
