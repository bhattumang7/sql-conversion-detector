using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Lineage;

/// <summary>
/// One <c>FROM</c>/<c>JOIN</c> leaf that names a table-valued function, flattened out of
/// whatever join tree it sits in - shared by <see cref="TvfFenceMap"/> (does this view/iTVF's
/// own body contain one) and <c>Predicates.TvfFenceScanner</c> (does this module's own FROM
/// clause reference one directly).
/// </summary>
/// <param name="Reference">The function-call table reference itself.</param>
/// <param name="IsCorrelatedApplySecondSide">
/// True when this reference is the right-hand side of a <c>CROSS/OUTER APPLY</c> AND its own
/// arguments contain at least one column reference - SQL Server's own reason APPLY exists over
/// plain JOIN is to allow the right side to depend on the left, so a column reference in that
/// position is real correlation, not a coincidence. Interleaved execution (2017+) does not
/// rescue this case.
/// </param>
public readonly record struct TvfLeafReference(SchemaObjectFunctionTableReference Reference, bool IsCorrelatedApplySecondSide);

/// <summary>Flattens a query's FROM clauses to their leaf table references, keeping only what the MSTVF-as-fence stream needs: function-call references (with APPLY-correlation evidence) and plain named-table references (candidate view/iTVF names for lineage nesting).</summary>
internal static class TvfReferenceWalker
{
    /// <summary>
    /// CTE names shadow catalog objects of the same name (the same rule FromScopeResolver applies
    /// during real column resolution) - a CTE is never schema-qualified, so an unqualified
    /// NamedTableReference matching one can never mean a real view/TVF instead. Filtered out
    /// here, centrally, since every consumer of this walker (ViewExpansionMap, TvfFenceMap,
    /// ScalarUdfMap) shares the identical failure mode without it: `WITH Foo AS (...) SELECT *
    /// FROM Foo` inside `CREATE VIEW dbo.Foo` used to record a self-reference (a false cycle,
    /// poisoning dbo.Foo to Unknown in ViewExpansionMap and ViewDependencyGraph alike), and a CTE
    /// coinciding with a real view/TVF/UDF-carrying object elsewhere created a false fence-
    /// inheritance or scalar-UDF-carrier finding.
    /// </summary>
    public static (List<TvfLeafReference> FunctionRefs, List<NamedTableReference> NamedRefs) CollectFromClauses(TSqlFragment root)
    {
        var cteNames = CteNameCollector.Collect(root);
        var visitor = new FromClauseVisitor(cteNames);
        root.Accept(visitor);
        return (visitor.FunctionRefs, visitor.NamedRefs);
    }

    private sealed class FromClauseVisitor(IReadOnlySet<string> cteNames) : TSqlFragmentVisitor
    {
        public List<TvfLeafReference> FunctionRefs { get; } = [];

        public List<NamedTableReference> NamedRefs { get; } = [];

        public override void ExplicitVisit(FromClause node)
        {
            foreach (var tableReference in node.TableReferences)
            {
                Flatten(tableReference, isApplySecondSide: false);
            }

            // Subqueries/derived tables/CTEs inside this FROM clause carry their own nested
            // FromClause nodes, which the base walk still reaches (ScriptDom's own tree
            // structure puts them underneath this one) - explicit recursion here would double-
            // visit them, so this override does NOT call base.ExplicitVisit for the table
            // references already flattened above, only for whatever isn't (WHERE/ON predicates
            // that might themselves contain a scalar subquery's own FROM).
            base.ExplicitVisit(node);
        }

        private void Flatten(TableReference tableReference, bool isApplySecondSide)
        {
            switch (tableReference)
            {
                case JoinTableReference join:
                    var isApply = join is UnqualifiedJoin { UnqualifiedJoinType: UnqualifiedJoinType.CrossApply or UnqualifiedJoinType.OuterApply };
                    Flatten(join.FirstTableReference, isApplySecondSide: false);
                    Flatten(join.SecondTableReference, isApplySecondSide: isApply);
                    break;

                case JoinParenthesisTableReference parenthesis:
                    Flatten(parenthesis.Join, isApplySecondSide);
                    break;

                case SchemaObjectFunctionTableReference function:
                    var isCorrelated = isApplySecondSide && function.Parameters.Any(ContainsColumnReference);
                    FunctionRefs.Add(new TvfLeafReference(function, isCorrelated));
                    break;

                case NamedTableReference named:
                    if (named.SchemaObject.SchemaIdentifier is null && cteNames.Contains(named.SchemaObject.BaseIdentifier.Value))
                    {
                        break;
                    }

                    NamedRefs.Add(named);
                    break;
            }

            // Derived tables (QueryDerivedTable), PIVOT/UNPIVOT and variable-table references
            // are deliberately not flattened further here - a TVF cannot appear as their own
            // outermost node, and any FROM clause nested inside a derived table's own query is
            // reached independently by this visitor's ExplicitVisit(FromClause) override, since
            // ScriptDom's tree still contains it underneath.
        }

        private static bool ContainsColumnReference(ScalarExpression argument)
        {
            var finder = new ColumnReferenceFinder();
            argument.Accept(finder);
            return finder.Found;
        }

        private sealed class ColumnReferenceFinder : TSqlFragmentVisitor
        {
            public bool Found { get; private set; }

            public override void ExplicitVisit(ColumnReferenceExpression node) => Found = true;
        }
    }
}
