using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Pass 3 Tier-1: syntactic non-sargable predicate detection that needs no type/lineage
/// information (CLAUDE.md: "Tier-1 syntactic rules (no types needed)"). Scoped to comparison
/// and LIKE predicates specifically inside a genuine filter context - WHERE, a JOIN's ON
/// clause, or HAVING's own filter - never a SELECT list, ORDER BY, or GROUP BY
/// (docs/audit-remediation-plan.md Phase 3.1: a function/arithmetic wrap on a column that's
/// never used to filter rows isn't a sargability concern at all, since there's no seek to lose).
/// </summary>
public static class NonSargablePredicateScanner
{
    public static IReadOnlyList<SargabilityFinding> Scan(SqlParseResult parseResult)
    {
        var visitor = new Visitor(parseResult.SourcePath);
        parseResult.Fragment.Accept(visitor);
        return visitor.Findings;
    }

    /// <summary>
    /// T-SQL aggregate functions never lose "sargability" the way a scalar function wrap does -
    /// COUNT/SUM/AVG/etc. wrapping a column in a HAVING clause (the only place they can appear
    /// alongside a column reference) reflects per-group aggregation, not an avoidable index-
    /// defeating transform (docs/audit-remediation-plan.md Phase 3.1).
    /// </summary>
    private static readonly HashSet<string> AggregateFunctionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUM", "COUNT", "COUNT_BIG", "AVG", "MIN", "MAX",
        "STDEV", "STDEVP", "VAR", "VARP",
        "GROUPING", "GROUPING_ID", "STRING_AGG", "CHECKSUM_AGG", "APPROX_COUNT_DISTINCT",
    };

    private sealed class Visitor(string sourcePath) : TSqlFragmentVisitor
    {
        private bool _inFilterContext;

        public List<SargabilityFinding> Findings { get; } = [];

        /// <summary>
        /// Resets filter context to false for every part of a query specification except its
        /// own WHERE/HAVING (whose own overrides turn it back on) - without this, a WHERE
        /// clause's own nested subquery (an EXISTS/IN (SELECT ...)) would inherit "filter
        /// context = true" for that subquery's unrelated SELECT list, and a top-level SELECT
        /// list would inherit whatever the enclosing scope happened to be.
        /// </summary>
        public override void ExplicitVisit(QuerySpecification node)
        {
            var previous = _inFilterContext;
            _inFilterContext = false;

            node.FromClause?.Accept(this);

            foreach (var element in node.SelectElements)
            {
                element.Accept(this);
            }

            node.WhereClause?.Accept(this);
            node.GroupByClause?.Accept(this);
            node.HavingClause?.Accept(this);
            node.OrderByClause?.Accept(this);
            node.WindowClause?.Accept(this);

            _inFilterContext = previous;
        }

        public override void ExplicitVisit(WhereClause node)
        {
            var previous = _inFilterContext;
            _inFilterContext = true;
            node.AcceptChildren(this);
            _inFilterContext = previous;
        }

        public override void ExplicitVisit(HavingClause node)
        {
            var previous = _inFilterContext;
            _inFilterContext = true;
            node.AcceptChildren(this);
            _inFilterContext = previous;
        }

        /// <summary>
        /// A JOIN's ON clause is a filter context exactly like WHERE; the table references it
        /// joins are not (a derived-table subquery there has its own SELECT list to protect).
        /// </summary>
        public override void ExplicitVisit(QualifiedJoin node)
        {
            node.FirstTableReference?.Accept(this);
            node.SecondTableReference?.Accept(this);

            var previous = _inFilterContext;
            _inFilterContext = true;
            node.SearchCondition?.Accept(this);
            _inFilterContext = previous;
        }

        public override void Visit(BooleanComparisonExpression node)
        {
            if (!_inFilterContext)
            {
                return;
            }

            InspectSide(node.FirstExpression);
            InspectSide(node.SecondExpression);
        }

        public override void Visit(BooleanTernaryExpression node)
        {
            if (!_inFilterContext)
            {
                return;
            }

            // BETWEEN: "col BETWEEN a AND b" - the tested value is FirstExpression; the
            // range bounds (Second/Third) are typically literals and not inspected here.
            if (node.TernaryExpressionType == BooleanTernaryExpressionType.Between
                || node.TernaryExpressionType == BooleanTernaryExpressionType.NotBetween)
            {
                InspectSide(node.FirstExpression);
            }
        }

        public override void Visit(LikePredicate node)
        {
            if (!_inFilterContext)
            {
                return;
            }

            if (node.FirstExpression is not ColumnReferenceExpression columnRef || ColumnName(columnRef) is not { } columnName)
            {
                return;
            }

            switch (node.SecondExpression)
            {
                case StringLiteral { Value: [ '%', ..] } literal:
                    Add(SargabilityFindingKind.LeadingWildcardLike, columnName, literal.Value, node);
                    break;
                case StringLiteral:
                    // A literal pattern with no leading wildcard is sargable; nothing to report.
                    break;
                default:
                    // The pattern isn't a literal (a parameter/variable/expression) - we can't
                    // rule out a leading wildcard statically. CLAUDE.md: "LIKE @p marked conditional".
                    Add(SargabilityFindingKind.LikePatternNotLiteral, columnName, detail: null, node);
                    break;
            }
        }

        private void InspectSide(ScalarExpression expression)
        {
            switch (expression)
            {
                case FunctionCall { Parameters.Count: > 0 } functionCall
                    when !AggregateFunctionNames.Contains(functionCall.FunctionName.Value) && FirstNamedColumn(functionCall.Parameters) is { } named:
                    Add(SargabilityFindingKind.FunctionWrappedColumn, named.Name, functionCall.FunctionName.Value, functionCall);
                    break;

                case CastCall { Parameter: ColumnReferenceExpression columnRef } castCall when ColumnName(columnRef) is { } name:
                    Add(SargabilityFindingKind.CastOrConvertOnColumn, name, "CAST", castCall);
                    break;

                case ConvertCall { Parameter: ColumnReferenceExpression columnRef } convertCall when ColumnName(columnRef) is { } name:
                    Add(SargabilityFindingKind.CastOrConvertOnColumn, name, "CONVERT", convertCall);
                    break;

                case BinaryExpression binary:
                    InspectArithmetic(binary);
                    break;
            }
        }

        private void InspectArithmetic(BinaryExpression binary)
        {
            if (binary.FirstExpression is ColumnReferenceExpression leftColumn && ColumnName(leftColumn) is { } leftName)
            {
                Add(SargabilityFindingKind.ColumnArithmetic, leftName, binary.BinaryExpressionType.ToString(), binary);
            }
            else if (binary.SecondExpression is ColumnReferenceExpression rightColumn && ColumnName(rightColumn) is { } rightName)
            {
                Add(SargabilityFindingKind.ColumnArithmetic, rightName, binary.BinaryExpressionType.ToString(), binary);
            }
        }

        /// <summary>The first parameter that's a genuine named column reference - COUNT(*) etc. have a Wildcard ColumnReferenceExpression with no MultiPartIdentifier, which isn't "a column" for this rule's purposes.</summary>
        private static (ColumnReferenceExpression Ref, string Name)? FirstNamedColumn(IList<ScalarExpression> parameters)
        {
            foreach (var parameter in parameters.OfType<ColumnReferenceExpression>())
            {
                if (ColumnName(parameter) is { } name)
                {
                    return (parameter, name);
                }
            }

            return null;
        }

        private static string? ColumnName(ColumnReferenceExpression columnRef) =>
            columnRef.MultiPartIdentifier?.Identifiers is { Count: > 0 } identifiers ? identifiers[^1].Value : null;

        private void Add(SargabilityFindingKind kind, string columnName, string? detail, TSqlFragment node) =>
            Findings.Add(new SargabilityFinding(kind, columnName, detail, sourcePath, node.StartLine, node.StartColumn));
    }
}
