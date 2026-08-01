using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// Pass 3 Tier-1: syntactic non-sargable predicate detection that needs no type/lineage
/// information (CLAUDE.md: "Tier-1 syntactic rules (no types needed)"). Scoped to comparison
/// and LIKE predicates specifically - a function call in a SELECT list is not a sargability
/// concern, only one wrapping a column inside a WHERE/ON/HAVING/comparison is.
/// </summary>
public static class NonSargablePredicateScanner
{
    public static IReadOnlyList<SargabilityFinding> Scan(SqlParseResult parseResult)
    {
        var visitor = new Visitor(parseResult.SourcePath);
        parseResult.Fragment.Accept(visitor);
        return visitor.Findings;
    }

    private sealed class Visitor(string sourcePath) : TSqlFragmentVisitor
    {
        public List<SargabilityFinding> Findings { get; } = [];

        public override void Visit(BooleanComparisonExpression node)
        {
            InspectSide(node.FirstExpression);
            InspectSide(node.SecondExpression);
        }

        public override void Visit(BooleanTernaryExpression node)
        {
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
                    when FirstNamedColumn(functionCall.Parameters) is { } named:
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
