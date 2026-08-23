using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class SessionDateSettingScanner
{
    public static IReadOnlyList<SessionDateSettingFinding> Scan(SqlParseResult parseResult)
    {
        var visitor = new Visitor(parseResult.SourcePath);
        parseResult.Fragment.Accept(visitor);
        return
        [
            .. visitor.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];
    }

    private sealed class Visitor(string sourcePath) : TSqlFragmentVisitor
    {
        public List<SessionDateSettingFinding> Findings { get; } = [];

        public override void ExplicitVisit(SetCommandStatement node)
        {
            foreach (var command in node.Commands)
            {
                if (command is GeneralSetCommand { CommandType: GeneralSetCommandType.DateFormat })
                {
                    Findings.Add(new SessionDateSettingFinding(
                        SessionDateSettingKind.DateFormat, sourcePath, command.StartLine, command.StartColumn));
                }
                else if (command is GeneralSetCommand { CommandType: GeneralSetCommandType.DateFirst })
                {
                    Findings.Add(new SessionDateSettingFinding(
                        SessionDateSettingKind.DateFirst, sourcePath, command.StartLine, command.StartColumn));
                }
            }

            base.ExplicitVisit(node);
        }
    }
}
