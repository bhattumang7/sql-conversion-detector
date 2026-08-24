using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Common;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class WindowFunctionArgumentScanner
{
    public static IReadOnlyList<WindowFunctionArgumentFinding> Scan(SqlParseResult parseResult)
    {
        var visitor = new Visitor(parseResult.SourcePath);
        parseResult.Fragment.Accept(visitor);
        return
        [
            .. visitor.Findings
                .OrderBy(f => f.Kind)
                .ThenBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];
    }

    private sealed class Visitor(string sourcePath) : TSqlFragmentVisitor
    {
        public List<WindowFunctionArgumentFinding> Findings { get; } = [];

        public override void ExplicitVisit(FunctionCall node)
        {
            var name = node.FunctionName?.Value;

            if (node.OverClause is not null
                && (string.Equals(name, "LAG", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "LEAD", StringComparison.OrdinalIgnoreCase))
                && node.Parameters.Count >= 2
                && LiteralComparisonFolder.TryFoldToNumeric(node.Parameters[1]) is { } offset
                && offset < 0)
            {
                Add(WindowFunctionArgumentFindingKind.LagLeadNegativeOffset, name!, node.Parameters[1]);
            }
            else if (node.WithinGroupClause is not null
                && (string.Equals(name, "PERCENTILE_CONT", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "PERCENTILE_DISC", StringComparison.OrdinalIgnoreCase))
                && node.Parameters.Count >= 1
                && LiteralComparisonFolder.TryFoldToNumeric(node.Parameters[0]) is { } percentile
                && (percentile < 0 || percentile > 1))
            {
                Add(WindowFunctionArgumentFindingKind.PercentileOutOfRange, name!, node.Parameters[0]);
            }

            base.ExplicitVisit(node);
        }

        private void Add(WindowFunctionArgumentFindingKind kind, string functionName, ScalarExpression argument) =>
            Findings.Add(new WindowFunctionArgumentFinding(
                kind, functionName.ToUpperInvariant(), FragmentTextRenderer.Render(argument),
                sourcePath, argument.StartLine, argument.StartColumn));
    }
}
