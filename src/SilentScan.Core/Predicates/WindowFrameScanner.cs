using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

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
