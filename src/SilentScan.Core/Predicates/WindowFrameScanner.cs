using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

/// <summary>
/// docs/detection-checklist.md "Small precise adds": RANGE instead of ROWS in window-function
/// frames. Fully syntax-only, single-visitor scan matching <see cref="ForcedSerialScanner"/>'s own
/// "one scanner, many Kind values" shape.
/// </summary>
public static class WindowFrameScanner
{
    public static IReadOnlyList<WindowFrameFinding> Scan(SqlParseResult parseResult)
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
        public List<WindowFrameFinding> Findings { get; } = [];

        public override void ExplicitVisit(OverClause node)
        {
            if (node.OrderByClause is not null)
            {
                if (node.WindowFrameClause is null)
                {
                    // No explicit frame clause with an ORDER BY present - T-SQL silently defaults
                    // this to RANGE BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW, oracle-confirmed
                    // to carry the same measured cost as an explicit RANGE frame.
                    Findings.Add(new WindowFrameFinding(
                        WindowFrameFindingKind.ImplicitDefaultRangeFrame, sourcePath, node.StartLine, node.StartColumn));
                }
                else if (node.WindowFrameClause.WindowFrameType == WindowFrameType.Range)
                {
                    Findings.Add(new WindowFrameFinding(
                        WindowFrameFindingKind.ExplicitRangeFrame, sourcePath,
                        node.WindowFrameClause.StartLine, node.WindowFrameClause.StartColumn));
                }
            }

            base.ExplicitVisit(node);
        }
    }
}
