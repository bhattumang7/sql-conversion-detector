using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class SessionDateSettingScanner
{
    public static IReadOnlyList<SessionDateSettingFinding> Scan(SqlParseResult parseResult)
    {
        var rule = CreateRule(parseResult.SourcePath);
        var walker = new ModuleWalker(parseResult.SourcePath, new DatabaseCatalog(), EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
    return Harvest(rule);
    }
    internal static Rule CreateRule(string sourcePath) => new(sourcePath);

    internal static IReadOnlyList<SessionDateSettingFinding> Harvest(Rule rule) =>
            [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];


    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    internal sealed class Rule(string sourcePath) : IModuleRule
    {
        public List<SessionDateSettingFinding> Findings { get; } = [];

        public void OnEnterSetCommandStatement(SetCommandStatement node, ModuleWalker walker)
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
        }
    }
}
