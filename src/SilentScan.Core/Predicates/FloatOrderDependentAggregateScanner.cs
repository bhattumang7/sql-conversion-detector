using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;
using SilentScan.Core.TypeInference;

namespace SilentScan.Core.Predicates;

public static class FloatOrderDependentAggregateScanner
{
    private static readonly HashSet<string> OrderDependentAggregateFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUM", "AVG", "VAR", "VARP", "STDEV", "STDEVP",
    };

    public static IReadOnlyList<FloatOrderDependentAggregateFinding> Scan(SqlParseResult parseResult, DatabaseCatalog catalog)
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
        : ScopedSqlVisitorBase(sourcePath, catalog, PredicateVisitorSupport.EmptyResolvedViews, ledger: null, currentProcScope: null, callerScopeByCalleeScope: null)
#pragma warning restore CS9107
    {
        public List<FloatOrderDependentAggregateFinding> Findings { get; } = [];

        public override void ExplicitVisit(SelectStatement node)
        {
            PushCteScope(node.WithCtesAndXmlNamespaces);
            base.ExplicitVisit(node);
            PopCteScope();
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            var scopeChain = PredicateVisitorSupport.ScopeChainOf(FromScopeResolver.Resolve(node.FromClause, PredicateVisitorSupport.ResolutionContext(CurrentCteRelations(), sourcePath, catalog)));

            foreach (var element in node.SelectElements.OfType<SelectScalarExpression>())
            {
                Inspect(element.Expression, scopeChain);
            }

            if (node.HavingClause?.SearchCondition is { } having)
            {
                Inspect(having, scopeChain);
            }

            base.ExplicitVisit(node);
        }

        private void Inspect(
            TSqlFragment root,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            var collector = new AggregateCallCollector();
            root.Accept(collector);
            foreach (var call in collector.Calls)
            {
                InspectAggregateCall(call, scopeChain);
            }
        }

        private void InspectAggregateCall(
            FunctionCall call,
            IReadOnlyList<(IReadOnlyDictionary<string, ScopeEntry> ByAlias, IReadOnlyList<ScopeEntry> Ordered)> scopeChain)
        {
            foreach (var parameter in call.Parameters)
            {
                if (BaseColumnResolver.ResolveBaseColumn(parameter, sourcePath, scopeChain) is not { } resolved
                    || resolved.Type?.Category is not (SqlTypeCategory.Real or SqlTypeCategory.Float))
                {
                    continue;
                }

                Findings.Add(new FloatOrderDependentAggregateFinding(
                    resolved.TableQualifiedName,
                    resolved.ColumnName,
                    resolved.Type!.ToString(),
                    call.FunctionName.Value.ToUpperInvariant(),
                    sourcePath,
                    call.StartLine,
                    call.StartColumn));
            }
        }

        private sealed class AggregateCallCollector : TSqlFragmentVisitor
        {
            public List<FunctionCall> Calls { get; } = [];

            public override void ExplicitVisit(FunctionCall node)
            {
                if (node.OverClause is null && OrderDependentAggregateFunctionNames.Contains(node.FunctionName.Value))
                {
                    Calls.Add(node);
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
