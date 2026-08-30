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
        var candidates = new Dictionary<(string, string), Candidate>(TableColumnKeyComparer.For(catalog));

        foreach (var expression in catalog.SchemaExpressions)
        {
            if (expression.Kind != SchemaDependencyKind.ComputedColumn || expression.ColumnName is not { } columnName)
            {
                continue;
            }

            if (!DefinesTryCast(expression.DefinitionText, catalog.CompatibilityLevel))
            {
                continue;
            }

            var column = catalog.Find(expression.TableQualifiedName)?.FindColumn(columnName, catalog.IdentifierComparer);
            if (column is not { IsComputed: true, IsPersisted: false })
            {
                continue;
            }

            candidates[(expression.TableQualifiedName, columnName)] =
                new Candidate(expression.DefinitionText, expression.SourcePath, expression.Line);
        }

        return candidates;
    }

    private static bool DefinesTryCast(string definitionText, int? compatibilityLevel)
    {
        var result = SqlScriptParser.ParseText("schema-expression.sql", $"SELECT {definitionText};", initialQuotedIdentifiers: true, compatibilityLevel);
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
                .ThenBy(f => f.Location.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Location.Line)
                .ThenBy(f => f.Location.Column),
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

        protected override void OnQuerySpecificationScope(QuerySpecification node, ScopeChain scopeChain, Action continueDescent)
        {
            if (node.FromClause is not null)
            {
                InspectSearchCondition(node.WhereClause?.SearchCondition, scopeChain);
                InspectSearchCondition(node.HavingClause?.SearchCondition, scopeChain);
                foreach (var tableReference in node.FromClause.TableReferences)
                {
                    InspectJoins(tableReference, scopeChain);
                }
            }

            continueDescent();
        }

        protected override void OnUpdateStatementScope(UpdateStatement node, ScopeChain scopeChain, Action continueDescent)
        {
            InspectSearchCondition(node.UpdateSpecification.WhereClause?.SearchCondition, scopeChain);
            continueDescent();
        }

        protected override void OnDeleteStatementScope(DeleteStatement node, ScopeChain scopeChain, Action continueDescent)
        {
            InspectSearchCondition(node.DeleteSpecification.WhereClause?.SearchCondition, scopeChain);
            continueDescent();
        }

        private void InspectJoins(TableReference tableReference, IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
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
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            if (searchCondition is null)
            {
                return;
            }

            var collector = new ColumnReferenceCollector();
            searchCondition.Accept(collector);

            foreach (var columnRef in collector.References)
            {
                var provenance = ScalarExpressionResolver.ResolveColumnReference(columnRef, scopeChain, sourcePath, ledger: null, catalog);
                if (provenance is not ColumnProvenance.BaseColumn { Depth: 0 } baseColumn)
                {
                    continue;
                }

                if (!candidates.TryGetValue((baseColumn.TableQualifiedName, baseColumn.ColumnName), out var candidate))
                {
                    continue;
                }

                Findings.Add(new TryCastComputedColumnPredicateFinding(
                    baseColumn.TableQualifiedName, baseColumn.ColumnName, candidate.DefinitionText,
                    new SourceSpan(candidate.SourcePath, candidate.Line, 1),
                    new SourceSpan(sourcePath, columnRef.StartLine, columnRef.StartColumn)));
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
