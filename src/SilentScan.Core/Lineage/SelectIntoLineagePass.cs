using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Diagnostics;
using SilentScan.Core.Parsing;
using SilentScan.Core.Common;

namespace SilentScan.Core.Lineage;

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

            var existing = catalog.Find(qualifiedName, isTemp ? _currentScope : null);
            if (existing is null)
            {

                catalog.Skipped.Record(
                    AnalysisPass.Lineage, sourcePath, select.StartLine, select.StartColumn, "SELECT INTO",
                    $"'{qualifiedName}' has no Pass-1 catalog entry to merge into - the SELECT INTO statement may not have reached CatalogBuilder");
                return;
            }

            var resolved = QueryExpressionResolver.Resolve(
                select.QueryExpression, catalog, resolvedViews, sourcePath, catalog.Skipped, CurrentCteRelations(), _currentScope);

            if (existing.Columns.Count == 0 && resolved.Count > 0)
            {

                var freshColumns = resolved
                    .Select(r => new CatalogColumn(r.Name, ColumnProvenanceAnalysis.TryGetScalarType(r.Provenance), IsNullable: true, IsIdentity: false, IsComputed: false, IsPersisted: false))
                    .ToList();
                catalog.AddOrReplace(existing with { Columns = freshColumns }, isTemp ? _currentScope : null);
                return;
            }

            var resolvedByName = new Dictionary<string, ResolvedColumn>(catalog.IdentifierComparer);
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
            _cteStack.Push(ctes.Count == 0 ? currentCtes : MergeCtes(currentCtes, ctes, catalog.IdentifierComparer));
        }

        private IReadOnlyDictionary<string, ResolvedRelation> CurrentCteRelations() =>
            _cteStack.Count > 0 ? _cteStack.Peek() : EmptyResolvedRelations;

        private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedRelations = new Dictionary<string, ResolvedRelation>();

        private static Dictionary<string, ResolvedRelation> MergeCtes(
            IReadOnlyDictionary<string, ResolvedRelation> outer, IReadOnlyDictionary<string, ResolvedRelation> inner, StringComparer identifierComparer)
        {
            var merged = new Dictionary<string, ResolvedRelation>(outer, identifierComparer);
            foreach (var (name, relation) in inner)
            {
                merged[name] = relation;
            }

            return merged;
        }
    }
}
