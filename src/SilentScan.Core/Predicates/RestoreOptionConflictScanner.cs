using Microsoft.SqlServer.TransactSql.ScriptDom;
using SilentScan.Core.Catalog;
using SilentScan.Core.Lineage;
using SilentScan.Core.Parsing;

namespace SilentScan.Core.Predicates;

public static class RestoreOptionConflictScanner
{
    public static IReadOnlyList<RestoreOptionConflictFinding> Scan(SqlParseResult parseResult)
    {
        var rule = CreateRule(parseResult.SourcePath);
        var walker = new ModuleWalker(parseResult.SourcePath, new DatabaseCatalog(), EmptyResolvedViews, rules: [rule]);
        parseResult.Fragment.Accept(walker);
        return Harvest(rule);
    }

    internal static Rule CreateRule(string sourcePath) => new(sourcePath);

    internal static IReadOnlyList<RestoreOptionConflictFinding> Harvest(Rule rule) =>
        [
            .. rule.Findings
                .OrderBy(f => f.SourcePath, StringComparer.Ordinal)
                .ThenBy(f => f.Line)
                .ThenBy(f => f.Column),
        ];

    private static readonly IReadOnlyDictionary<string, ResolvedRelation> EmptyResolvedViews = new Dictionary<string, ResolvedRelation>();

    internal sealed class Rule(string sourcePath) : IModuleRule
    {
        public List<RestoreOptionConflictFinding> Findings { get; } = [];

        public void OnEnterRestoreStatement(RestoreStatement node, ModuleWalker walker)
        {
            var hasRecovery = node.Options.Any(o => o.OptionKind == RestoreOptionKind.Recovery);
            var hasNoRecovery = node.Options.Any(o => o.OptionKind == RestoreOptionKind.NoRecovery);
            var hasStandby = node.Options.Any(o => o.OptionKind == RestoreOptionKind.Standby);

            if (hasRecovery && hasNoRecovery)
            {
                Findings.Add(new RestoreOptionConflictFinding(
                    RestoreOptionConflictKind.RecoveryAndNoRecovery, sourcePath, node.StartLine, node.StartColumn));
            }

            if (hasRecovery && hasStandby)
            {
                Findings.Add(new RestoreOptionConflictFinding(
                    RestoreOptionConflictKind.RecoveryAndStandby, sourcePath, node.StartLine, node.StartColumn));
            }

            if (hasNoRecovery && hasStandby)
            {
                Findings.Add(new RestoreOptionConflictFinding(
                    RestoreOptionConflictKind.NoRecoveryAndStandby, sourcePath, node.StartLine, node.StartColumn));
            }
        }
    }
}
