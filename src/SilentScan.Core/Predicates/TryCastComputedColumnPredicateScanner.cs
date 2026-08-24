using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class TryCastComputedColumnPredicateScanner
{
    public readonly record struct Candidate(string DefinitionText, string SourcePath, int Line);

    public static IReadOnlyDictionary<(string TableQualifiedName, string ColumnName), Candidate> BuildCandidates(DatabaseCatalog catalog)
    {
        var candidates = new Dictionary<(string, string), Candidate>();

        foreach (var expression in catalog.SchemaExpressions)
        {
            if (expression.Kind != SchemaDependencyKind.ComputedColumn || expression.ColumnName is not { } columnName)
            {
                continue;
            }

            if (!DefinesTryCast(expression.DefinitionText))
            {
                continue;
            }

            var column = catalog.Find(expression.TableQualifiedName)?.FindColumn(columnName);
            if (column is not { IsComputed: true, IsPersisted: false })
            {
                continue;
            }

            candidates[(expression.TableQualifiedName, columnName)] =
                new Candidate(expression.DefinitionText, expression.SourcePath, expression.Line);
        }

        return candidates;
    }

    private static bool DefinesTryCast(string definitionText)
    {
        var result = SqlScriptParser.ParseText("schema-expression.sql", $"SELECT {definitionText};");
        if (result.HasErrors || result.Fragment is not TSqlScript script)
        {
            return false;
        }

        var visitor = new TryCastCallDetector();
        script.Accept(visitor);
        return visitor.Found;
    }

    private sealed class TryCastCallDetector : TSqlFragmentVisitor
    {
        public bool Found { get; private set; }

        public override void ExplicitVisit(TryCastCall node)
        {
            Found = true;
            base.ExplicitVisit(node);
        }
    }

    public static IReadOnlyList<TryCastComputedColumnPredicateFinding> Scan(
        SqlParseResult parseResult, DatabaseCatalog catalog, IReadOnlyDictionary<(string TableQualifiedName, string ColumnName), Candidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var visitor = new Visitor(parseResult.SourcePath, catalog, candidates);
        parseResult.Fragment.Accept(visitor);
        return
        [
            .. visitor.Findings
                .OrderBy(f => f.TableQualifiedName, StringComparer.Ordinal)
                .ThenBy(f => f.ColumnName, StringComparer.Ordinal)
                .ThenBy(f => f.PredicateSourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.PredicateLine)
                .ThenBy(f => f.PredicateColumn),
        ];
    }

#pragma warning disable CS9107
    private sealed class Visitor(
        string sourcePath, DatabaseCatalog catalog,
        IReadOnlyDictionary<(string TableQualifiedName, string ColumnName), Candidate> candidates)
        : ScopedSqlVisitorBase(sourcePath, catalog, EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null)
#pragma warning restore CS9107
    {
        private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

        public List<TryCastComputedColumnPredicateFinding> Findings { get; } = [];

        public override void ExplicitVisit(SelectStatement node)
        {
            PushCteScope(node.WithCtesAndXmlNamespaces);
            base.ExplicitVisit(node);
            PopCteScope();
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            if (node.FromClause is not null)
            {
                var (byAlias, ordered) = FromScopeResolver.Resolve(node.FromClause, catalog, EmptyResolvedViews, sourcePath, ledger: null, CurrentCteRelations(), procScope: null);
                var scopeChain = new List<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> { (byAlias, ordered) };

                InspectSearchCondition(node.WhereClause?.SearchCondition, scopeChain);
                InspectSearchCondition(node.HavingClause?.SearchCondition, scopeChain);
                foreach (var tableReference in node.FromClause.TableReferences)
                {
                    InspectJoins(tableReference, scopeChain);
                }
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateStatement node)
        {
            var spec = node.UpdateSpecification;
            var (byAlias, ordered) = FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, ResolutionContext(node.WithCtesAndXmlNamespaces));
            var scopeChain = new List<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> { (byAlias, ordered) };
            InspectSearchCondition(spec.WhereClause?.SearchCondition, scopeChain);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeleteStatement node)
        {
            var spec = node.DeleteSpecification;
            var (byAlias, ordered) = FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, ResolutionContext(node.WithCtesAndXmlNamespaces));
            var scopeChain = new List<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> { (byAlias, ordered) };
            InspectSearchCondition(spec.WhereClause?.SearchCondition, scopeChain);
            base.ExplicitVisit(node);
        }

        private FromScopeResolver.ResolutionContext ResolutionContext(WithCtesAndXmlNamespaces? withClause) =>
            new(catalog, EmptyResolvedViews, sourcePath, Ledger: null, CteResolver.Resolve(withClause, catalog, EmptyResolvedViews, sourcePath, ledger: null), ProcScope: null);

        private void InspectJoins(TableReference tableReference, List<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            switch (tableReference)
            {
                case QualifiedJoin join:
                    InspectJoins(join.FirstTableReference, scopeChain);
                    InspectJoins(join.SecondTableReference, scopeChain);
                    InspectSearchCondition(join.SearchCondition, scopeChain);
                    break;
                case UnqualifiedJoin unqualified:
                    InspectJoins(unqualified.FirstTableReference, scopeChain);
                    InspectJoins(unqualified.SecondTableReference, scopeChain);
                    break;
                default:
                    break;
            }
        }

        private void InspectSearchCondition(
            BooleanExpression? searchCondition,
            List<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            if (searchCondition is null)
            {
                return;
            }

            var collector = new ColumnReferenceCollector();
            searchCondition.Accept(collector);

            foreach (var columnRef in collector.References)
            {
                var provenance = ScalarExpressionResolver.ResolveColumnReference(columnRef, scopeChain, sourcePath, ledger: null);
                if (provenance is not ColumnProvenance.BaseColumn { Depth: 0 } baseColumn)
                {
                    continue;
                }

                if (!candidates.TryGetValue((baseColumn.TableQualifiedName, baseColumn.ColumnName), out var candidate))
                {
                    continue;
                }

                Findings.Add(new TryCastComputedColumnPredicateFinding(
                    baseColumn.TableQualifiedName, baseColumn.ColumnName, candidate.DefinitionText, candidate.SourcePath, candidate.Line,
                    sourcePath, columnRef.StartLine, columnRef.StartColumn));
            }
        }

        private sealed class ColumnReferenceCollector : TSqlFragmentVisitor
        {
            public List<ColumnReferenceExpression> References { get; } = [];

            public override void ExplicitVisit(ColumnReferenceExpression node) => References.Add(node);

            public override void ExplicitVisit(QuerySpecification node)
            {
                _ = node;
            }

            public override void ExplicitVisit(ScalarSubquery node)
            {
                _ = node;
            }
        }
    }
}
