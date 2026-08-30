using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class FloatEqualityPredicateScanner
{
    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    public static IReadOnlyList<FloatEqualityFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
    {
        var visitor = new Visitor(parseResult.SourcePath, catalog);
        parseResult.Fragment.Accept(visitor);
        return
        [
            .. visitor.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];
    }

#pragma warning disable CS9107
    private sealed class Visitor(string sourcePath, DatabaseCatalog catalog)
        : ScopedSqlVisitorBase(sourcePath, catalog, EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null)
#pragma warning restore CS9107
    {
        public List<FloatEqualityFinding> Findings { get; } = [];

        public override void ExplicitVisit(SelectStatement node)
        {
            PushCteScope(node.WithCtesAndXmlNamespaces);
            base.ExplicitVisit(node);
            PopCteScope();
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            ScopeStack.Push(FromScopeResolver.Resolve(node.FromClause, CurrentResolutionContext()));
            var scopeChain = CurrentScopeChain();

            if (node.WhereClause?.SearchCondition is { } whereCondition)
            {
                Inspect(whereCondition, scopeChain);
            }

            InspectJoinOnClauses(node.FromClause?.TableReferences, scopeChain, Inspect);

            base.ExplicitVisit(node);
            ScopeStack.Pop();
        }

        public override void ExplicitVisit(UpdateStatement node)
        {
            var spec = node.UpdateSpecification;
            PushCteScope(node.WithCtesAndXmlNamespaces);
            ScopeStack.Push(FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, CurrentResolutionContext()));
            PopCteScope();
            var scopeChain = CurrentScopeChain();

            if (spec.WhereClause?.SearchCondition is { } whereCondition)
            {
                Inspect(whereCondition, scopeChain);
            }

            InspectJoinOnClauses(spec.FromClause?.TableReferences, scopeChain, Inspect);

            base.ExplicitVisit(node);
            ScopeStack.Pop();
        }

        public override void ExplicitVisit(DeleteStatement node)
        {
            var spec = node.DeleteSpecification;
            PushCteScope(node.WithCtesAndXmlNamespaces);
            ScopeStack.Push(FromScopeResolver.ResolveForDataModification(spec.Target, spec.FromClause, CurrentResolutionContext()));
            PopCteScope();
            var scopeChain = CurrentScopeChain();

            if (spec.WhereClause?.SearchCondition is { } whereCondition)
            {
                Inspect(whereCondition, scopeChain);
            }

            InspectJoinOnClauses(spec.FromClause?.TableReferences, scopeChain, Inspect);

            base.ExplicitVisit(node);
            ScopeStack.Pop();
        }

        private void Inspect(
            BooleanExpression searchCondition,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            var collector = new EqualityCollector();
            searchCondition.Accept(collector);
            foreach (var comparison in collector.Comparisons)
            {
                InspectEquality(comparison, scopeChain);
            }
        }

        private void InspectEquality(
            BooleanComparisonExpression comparison,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            foreach (var side in new[] { comparison.FirstExpression, comparison.SecondExpression })
            {
                if (side is not ColumnReferenceExpression columnRef
                    || BaseColumnResolver.ResolveBaseColumn(columnRef, sourcePath, scopeChain, catalog) is not { } resolved
                    || resolved.Type?.Category is not (SqlTypeCategory.Real or SqlTypeCategory.Float))
                {
                    continue;
                }

                Findings.Add(new FloatEqualityFinding(
                    resolved.TableQualifiedName,
                    resolved.ColumnName,
                    resolved.Type!.ToString(),
                    sourcePath,
                    comparison.StartLine,
                    comparison.StartColumn));

                return;
            }
        }

        private sealed class EqualityCollector : TSqlFragmentVisitor
        {
            public List<BooleanComparisonExpression> Comparisons { get; } = [];

            public override void ExplicitVisit(BooleanComparisonExpression node)
            {
                if (node.ComparisonType == BooleanComparisonType.Equals)
                {
                    Comparisons.Add(node);
                }

                base.ExplicitVisit(node);
            }

            public override void ExplicitVisit(QuerySpecification node)
            {
                _ = node;
            }
        }
    }
}
