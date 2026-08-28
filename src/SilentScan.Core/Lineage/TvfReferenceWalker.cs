using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SilentScan.Core.Lineage;

public readonly record struct TvfLeafReference(SchemaObjectFunctionTableReference Reference, bool IsCorrelatedApplySecondSide);

internal static class TvfReferenceWalker
{
    public static (List<TvfLeafReference> FunctionRefs, List<NamedTableReference> NamedRefs) CollectFromClauses(TSqlFragment root, StringComparer? identifierComparer = null)
    {
        var cteNames = CteNameCollector.Collect(root, identifierComparer);
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
